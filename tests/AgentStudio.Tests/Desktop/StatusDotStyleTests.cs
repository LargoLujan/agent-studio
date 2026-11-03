using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// Applies the real StatusDot style from Themes/Dark.xaml. Regression test: a Trigger on Tag compared a boxed enum
/// with a string and painted every provider amber.
/// </summary>
public sealed class StatusDotStyleTests
{
    static StatusDotStyleTests()
    {
        // Registers the pack:// URI scheme, which is normally done by System.Windows.Application.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
    }

    [Theory]
    [InlineData(StatusKind.Ok, "SuccessBrush")]
    [InlineData(StatusKind.Warning, "WarningBrush")]
    [InlineData(StatusKind.Error, "ErrorBrush")]
    public void Dot_colour_follows_the_status(StatusKind status, string expectedBrushKey)
    {
        var (actual, expected) = RunOnSta(() =>
        {
            var theme = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/AgentStudio.Desktop;component/Themes/Dark.xaml", UriKind.Relative));
            var dot = new Ellipse
            {
                Style = (Style)theme["StatusDot"],
                DataContext = new ProviderStatusItem("Proveedor", status, "Instalado", "1.0.0", "Sesión iniciada", "—", "", []),
            };

            // Let bindings and data triggers run.
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            return (((SolidColorBrush)dot.Fill).Color, ((SolidColorBrush)theme[expectedBrushKey]).Color);
        });

        Assert.Equal(expected, actual);
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
#pragma warning disable CA1031 // Re-thrown on the test thread below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException($"STA test body failed: {failure.GetType().Name}: {failure.Message}", failure);
        }

        return result;
    }
}
