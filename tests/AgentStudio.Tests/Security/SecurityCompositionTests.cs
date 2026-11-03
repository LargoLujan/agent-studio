using AgentStudio.Application.Security;
using AgentStudio.Security;
using AgentStudio.Security.Redaction;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Security;

public sealed class SecurityCompositionTests
{
    private sealed class OtherRedactor : ISecretRedactor
    {
        public string Redact(string text) => text;
    }

    [Fact]
    public void The_security_module_registers_one_redactor_for_the_whole_app()
    {
        using var provider = new ServiceCollection().AddAgentStudioSecurity().BuildServiceProvider();

        var first = provider.GetRequiredService<ISecretRedactor>();

        Assert.IsType<SecretRedactor>(first);
        Assert.Same(first, provider.GetRequiredService<ISecretRedactor>());
    }

    [Fact]
    public void A_redactor_registered_earlier_is_not_replaced()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretRedactor, OtherRedactor>();

        using var provider = services.AddAgentStudioSecurity().BuildServiceProvider();

        Assert.IsType<OtherRedactor>(provider.GetRequiredService<ISecretRedactor>());
    }

    [Fact]
    public void The_registered_redactor_really_redacts()
    {
        using var provider = new ServiceCollection().AddAgentStudioSecurity().BuildServiceProvider();

        Assert.Equal("password=[REDACTED]", provider.GetRequiredService<ISecretRedactor>().Redact("password=hunter2"));
    }
}
