using System.IO;
using System.Windows;
using System.Windows.Controls;
using NodifySpike.Harness;
using NodifySpike.Theming;
using NodifySpike.ViewModels;

namespace NodifySpike;

/// <summary>
/// Entry point of the harness. Without arguments it opens the canvas (small sample) in a normal window for manual use.
/// <code>
/// NodifySpike.exe --auto --out &lt;dir&gt; --results &lt;file&gt; [--only small|input|bench|exp]
/// </code>
/// <c>--auto</c> is the scripted run: it never shows anything on the user's display (the window is parked beyond every
/// monitor, input goes in as window messages and pictures are rendered into bitmaps) and exits when done. It takes WPF's
/// keyboard focus only at a moment when that cannot disturb anybody and hands the foreground back at once if Windows lets
/// the window take it (see <c>HarnessRunner.EnsureKeyboardFocusAsync</c>). A hard timer in a background thread ends the
/// process if a scenario hangs, and every failure path exits instead of leaving a crash dialog on the screen.
/// </summary>
public partial class App
{
    private const int WatchdogMilliseconds = 240_000;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Nodify repaints from the app tokens: this must run before the first window is created.
        NodifyThemeTokens.Apply(Resources, Resources);

        var options = HarnessOptions.Parse(e.Args);
        if (options.Auto)
        {
            // A scripted run must never open a popup: WPF keeps tool tips and menus inside a monitor, so one could land on the
            // user's screen even though the window it belongs to is parked beyond every monitor. (Before any element exists.)
            ToolTipService.IsEnabledProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
        }

        var window = new MainWindow();
        MainWindow = window;

        if (!options.Auto)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Loaded += (_, _) => SampleGraphs.LoadSmall((CanvasViewModel)window.DataContext);
            window.Show();
            return;
        }

        var logPath = Path.ChangeExtension(options.ResultsPath, ".abort.log");
        void Abort(string reason, int code)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {reason}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Nothing else to do: the process is about to end.
            }

            Environment.Exit(code);
        }

        // No crash dialog, no hang: every way out of a broken run ends the process.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            Abort($"Unhandled exception on the UI thread: {args.Exception}", 3);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Abort($"Unhandled exception: {args.ExceptionObject}", 3);
        new Thread(() =>
        {
            Thread.Sleep(WatchdogMilliseconds);
            Abort($"The run did not finish in {WatchdogMilliseconds / 1000} s; ending the process.", 2);
        })
        {
            IsBackground = true,
            Name = "NodifySpike watchdog",
        }.Start();

        window.Loaded += async (_, _) =>
        {
            var code = 1;
            try
            {
                code = await HarnessRunner.RunAsync(window, options);
            }
            catch (Exception ex)
            {
                Abort($"The scripted run failed: {ex}", 3);
            }
            finally
            {
                Shutdown(code);
            }
        };

        try
        {
            OffScreenHost.ShowOffScreen(window);
        }
        catch (InvalidOperationException ex)
        {
            Abort(ex.Message, 4);
        }
    }
}
