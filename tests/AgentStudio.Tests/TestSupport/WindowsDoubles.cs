using AgentStudio.Application.Execution;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Tests.Desktop.Lock;
using AgentStudio.Tests.Desktop.Phase8;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.TestSupport;

/// <summary>
/// Doubles of what the composition of the app hooks into Windows as soon as it is built, registered before it (its
/// registrations keep what came first): the Windows session (<c>WindowsSession</c> listens to <c>SystemEvents</c>), the power
/// transitions (<c>WindowsPowerEvents</c> registers with Windows for the suspensions), and Windows Hello. A test that composes the
/// app adds these first, so it builds none of the real ones; what the app registers is checked apart, without building it.
/// </summary>
internal static class WindowsDoubles
{
    public static IServiceCollection AddWindowsDoubles(this IServiceCollection services) =>
        services
            .AddSingleton<IWindowsSession>(new FakeWindowsSession())
            .AddSingleton<IPowerEvents>(new SilentPowerEvents())
            .AddSingleton<IUserPresenceVerifier>(new FakeUserPresenceVerifier());
}
