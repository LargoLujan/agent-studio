using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentStudio.Desktop.Documents;

/// <summary>
/// Prints HTML to PDF with WebView2 (<c>CoreWebView2.PrintToPdfStreamAsync</c>; docs/17 D-59). It is not tested without a desktop and
/// the runtime: its port (<see cref="IHtmlPdfPrinter"/>) is what the rest is tested through, and the real print is part of the
/// demonstration. How it keeps the page closed:
/// <list type="bullet">
/// <item>One document at a time (a gate), in a window off the screen that is closed at the end.</item>
/// <item>The page is a file in a folder of its own, served through a virtual host name that denies cross-origin access; scripts are
/// off, and any navigation but the page itself is cancelled, so the page reads nothing and sends nothing.</item>
/// <item>Without header or footer (no date, no address in the PDF).</item>
/// <item>The PDF is taken as a stream in memory and the folder is deleted: if the app exits before the print ends, no file is saved.</item>
/// </list>
/// </summary>
public sealed class WebView2PdfPrinter : IHtmlPdfPrinter
{
    private const string HostName = "agentstudio.print";

    private const string PageName = "document.html";

    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<byte[]> PrintAsync(byte[] html, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(html);
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? throw new InvalidOperationException("Printing to PDF needs the window of the app.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await dispatcher.InvokeAsync(() => PrintOnUiAsync(html, ct)).Task.Unwrap().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<byte[]> PrintOnUiAsync(byte[] html, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "AgentStudio-print-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var window = new Window
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            Width = 900,
            Height = 700,
            Left = -32000,
            Top = -32000,
        };
        var view = new WebView2();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, PageName), html, ct);
            window.Content = view;
            window.Show();
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            var page = new Uri($"https://{HostName}/{PageName}");
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || target != page)
                {
                    e.Cancel = true;
                }
            };
            core.SetVirtualHostNameToFolderMapping(HostName, folder, CoreWebView2HostResourceAccessKind.Deny);

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate(page.AbsoluteUri);
            using (ct.Register(() => loaded.TrySetCanceled(ct)))
            {
                if (await loaded.Task.WaitAsync(NavigationTimeout, ct) is false)
                {
                    throw new InvalidOperationException("The page of the document did not load.");
                }
            }

            var settings = environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            await using var pdf = await core.PrintToPdfStreamAsync(settings);
            using var memory = new MemoryStream();
            await pdf.CopyToAsync(memory, ct);
            return memory.ToArray();
        }
        finally
        {
            view.Dispose();
            window.Close();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder holds only the page of the document; the next cleanup of the temporary folder takes it.
            }
        }
    }
}
