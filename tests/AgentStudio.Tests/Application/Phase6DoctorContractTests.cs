using System.Reflection;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Diagnostics;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/20 D-02 and D-10 (P0 of Phase 6, ADR-0061): the two guarantees that are the type and not a convention. The door for system tools has no way to
/// pass an executable or an argument (F6-7), and a fix cannot be written that applies something with neither an undo nor an approval (F6-2).
/// </summary>
public sealed class Phase6DoctorContractTests
{
    private static readonly DoctorAction Nothing = (_, _) => Task.CompletedTask;

    private static ApprovalRequest Request => new("doctor.fix", "Cambia algo de Agent Studio", RiskLevel.Low, null, []);

    // ---- the fixes ----

    [Fact]
    public void A_fix_is_a_closed_hierarchy_of_exactly_four_forms_and_nobody_else_can_derive_from_it()
    {
        var derived = typeof(DoctorFix).Assembly.GetTypes().Where(t => t != typeof(DoctorFix) && typeof(DoctorFix).IsAssignableFrom(t)).Select(t => t.Name).Order(StringComparer.Ordinal);

        Assert.Equal(["Approved", "Guide", "Navigate", "Reversible"], derived);
        Assert.True(typeof(DoctorFix).IsAbstract);
        Assert.All(typeof(DoctorFix).GetNestedTypes(), type => Assert.True(type.IsSealed, type.Name));
        Assert.Empty(typeof(DoctorFix).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(typeof(DoctorFix).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance), c => c.IsFamily || c.IsFamilyOrAssembly || c.IsAssembly);
        var constructor = Assert.Single(typeof(DoctorFix).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.True(constructor.IsPrivate);
    }

    [Fact]
    public void A_form_that_applies_something_always_carries_its_undo_or_its_approval()
    {
        foreach (var form in typeof(DoctorFix).GetNestedTypes())
        {
            var applies = form.GetProperty("Apply") is not null;
            var undoes = form.GetProperty("Undo") is not null;
            var asks = form.GetProperty("Approval") is not null;

            Assert.Equal(applies, undoes || asks);
            Assert.False(undoes && asks, $"{form.Name} has both: each form has one rule.");
        }

        // The forms that apply cannot be built without the other half.
        Assert.Throws<ArgumentNullException>(() => new DoctorFix.Reversible("doctor.fix.x", Nothing, null!));
        Assert.Throws<ArgumentNullException>(() => new DoctorFix.Reversible("doctor.fix.x", null!, Nothing));
        Assert.Throws<ArgumentNullException>(() => new DoctorFix.Approved("doctor.fix.x", null!, Nothing));
        Assert.Throws<ArgumentNullException>(() => new DoctorFix.Approved("doctor.fix.x", Request, null!));
    }

    [Fact]
    public async Task A_reversible_fix_applies_and_undoes_what_it_carries_and_an_approved_one_carries_its_request()
    {
        var calls = new List<string>();
        var reversible = new DoctorFix.Reversible("doctor.fix.regenerate", (_, _) => { calls.Add("apply"); return Task.CompletedTask; }, (_, _) => { calls.Add("undo"); return Task.CompletedTask; });
        var approved = new DoctorFix.Approved("doctor.fix.disable", Request, Nothing);
        var project = new ProjectContext("p1", @"C:\work\calc");

        await reversible.Apply(project, CancellationToken.None);
        await reversible.Undo(project, CancellationToken.None);

        Assert.Equal(["apply", "undo"], calls);
        Assert.Equal("doctor.fix.regenerate", reversible.Code);
        Assert.Equal("doctor.fix", approved.Approval.Action);
        Assert.Equal(RiskLevel.Low, approved.Approval.Risk);
    }

    [Fact]
    public void A_navigation_names_a_screen_and_a_guide_never_runs_its_command()
    {
        var go = new DoctorFix.Navigate("doctor.fix.open-settings", "settings");

        Assert.Equal("settings", go.Target);
        Assert.Throws<ArgumentException>(() => new DoctorFix.Navigate("doctor.fix.open-settings", "Settings Page"));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Navigate("Open", "settings"));

        var command = new DoctorFix.Guide("doctor.fix.install-sdk", "winget install Microsoft.DotNet.SDK.10", null);
        Assert.Equal("winget install Microsoft.DotNet.SDK.10", command.Command);
        Assert.Null(command.Page);
        Assert.Null(typeof(DoctorFix.Guide).GetMethod("Run"));
        Assert.DoesNotContain(typeof(DoctorFix.Guide).GetProperties(), p => typeof(DoctorAction).IsAssignableFrom(p.PropertyType));
    }

    [Fact]
    public void A_guide_gives_a_command_of_one_line_or_an_https_page_without_credentials_and_at_least_one()
    {
        var page = new Uri("https://dotnet.microsoft.com/download");

        Assert.Equal(page, new DoctorFix.Guide("doctor.fix.a", null, page).Page);
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.a", null, null));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.a", "line one\nline two", null));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.a", string.Empty, null));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.a", " padded ", null));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.a", new string('x', DoctorFix.MaxCommandLength + 1), null));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.a", null, new Uri("http://example.org/")));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.a", null, new Uri("file:///C:/Windows/System32/cmd.exe")));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.a", null, new Uri("https://user:secret@example.org/")));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.a", null, new Uri("/relative", UriKind.Relative)));
    }

    // ---- the door for tools ----

    [Fact]
    public void The_door_for_tools_has_one_method_and_no_parameter_that_could_carry_an_executable_or_an_argument()
    {
        var method = Assert.Single(typeof(IDoctorToolRunner).GetMethods());

        Assert.Equal("RunAsync", method.Name);
        Assert.Equal([typeof(DoctorTool), typeof(string), typeof(CancellationToken)], method.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(["tool", "projectRoot", "ct"], method.GetParameters().Select(p => p.Name));
        Assert.Equal(typeof(Task<DoctorToolResult>), method.ReturnType);
        Assert.Empty(typeof(IDoctorToolRunner).GetProperties());
    }

    [Fact]
    public void The_closed_list_of_tools_is_the_one_of_the_design_and_each_entry_is_appended_never_inserted()
    {
        Assert.Equal(
            ["DotNetListSdks", "DotNetListRuntimes", "NodeVersion", "NpmVersion", "PythonVersion", "PyVersion", "GitVersion", "DockerVersionJson", "WslStatus"],
            Enum.GetNames<DoctorTool>());
    }

    [Fact]
    public void A_tool_that_did_not_run_has_no_exit_code_and_no_output_and_a_tool_that_did_is_not_made_by_the_shortcut()
    {
        var missing = DoctorToolResult.Without(DoctorToolOutcome.NotInstalled);
        var inside = DoctorToolResult.Without(DoctorToolOutcome.InsideProject, @"C:\work\calc\node.exe");

        Assert.Equal((DoctorToolOutcome.NotInstalled, (int?)null, string.Empty, false, (string?)null), (missing.Outcome, missing.ExitCode, missing.Output, missing.Truncated, missing.ExecutablePath));
        Assert.Equal(DoctorToolOutcome.InsideProject, inside.Outcome);
        Assert.Throws<ArgumentException>(() => DoctorToolResult.Without(DoctorToolOutcome.Completed));
        Assert.Equal(64_000, DoctorToolResult.MaxOutputLength);
    }

    [Fact]
    public void The_names_of_the_facts_the_packages_share_are_names_a_fact_accepts()
    {
        string[] names = [DoctorFactNames.Declared, DoctorFactNames.Source, DoctorFactNames.Installed, DoctorFactNames.Resolved, DoctorFactNames.Version, DoctorFactNames.Path];

        Assert.Equal(["declared", "source", "installed", "resolved", "version", "path"], names);
        Assert.All(names, name => Assert.Equal("x", new DoctorFact(name, "x").Value));
    }

    [Fact]
    public void The_contracts_of_the_doctor_have_the_members_the_design_lists()
    {
        Assert.Equal(["CheckAsync", "get_Section"], typeof(IDoctorCheckSource).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["RunAsync"], typeof(IDoctorService).GetMethods().Select(m => m.Name));
        Assert.Equal(["ReadAsync"], typeof(IInstalledToolInventory).GetMethods().Select(m => m.Name));
        Assert.Equal(["ReadAsync"], typeof(IDeclaredEnvironmentReader).GetMethods().Select(m => m.Name));
        Assert.Equal(["ApplyAsync", "UndoAsync"], typeof(IDoctorFixRunner).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Done", "Denied", "Failed"], Enum.GetNames<DoctorFixOutcome>());
        Assert.Equal(new DoctorFixResult(DoctorFixOutcome.Done, 7), new DoctorFixResult(DoctorFixOutcome.Done, 7));
    }
}
