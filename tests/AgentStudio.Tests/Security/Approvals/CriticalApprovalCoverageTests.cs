using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Security;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Desktop.Localization;

namespace AgentStudio.Tests.Security.Approvals;

/// <summary>
/// The security review of WP5 (2026-11-09): every critical or destructive approval the code asks is on the list of the barrier
/// by name (<see cref="ApprovalRiskClassifier.KnownPresenceActions"/>), so none is judged only by its level. A scan of
/// <c>src</c>: every approval names its action by a constant <c>…Action</c> (or forwards the <c>action</c> of the engine, whose
/// values are constants too), and every such constant is known, or is of low risk and asks nothing.
/// </summary>
/// <remarks>
/// What it cannot see: an action built at run time from text (a constant is the convention, and the second test fails on a
/// literal), and a constant not named <c>…Action</c>. An approval the list does not name is «Siempre» anyway when it is critical
/// or destructive (<see cref="ApprovalRiskClassifier.PresenceOf"/>): this test keeps the list complete, it is not the barrier.
/// </remarks>
public sealed partial class CriticalApprovalCoverageTests
{
    /// <summary>The actions of approvals that ask nothing: low risk by their words, and never asked as critical.</summary>
    private static readonly HashSet<string> LowActions = new(StringComparer.Ordinal)
    {
        "replan.dismiss",
        "plan.approve",
    };

    [GeneratedRegex(@"const\s+string\s+(\w*Action)\s*=\s*""([^""]+)""")]
    private static partial Regex ActionConstant();

    [GeneratedRegex(@"new\s+ApprovalRequest\s*\(")]
    private static partial Regex NewRequest();

    [GeneratedRegex(@"^(?:[A-Z]\w*\.)*[A-Z]\w*Action$|^(?:string\s+)?action$")]
    private static partial Regex ConstantOrForwarded();

    [GeneratedRegex(@"StartApproval\s*\(")]
    private static partial Regex StartApproval();

    private static string Sources => Path.Combine(LocalizationTestSupport.RepositoryRoot, "src");

    private static IEnumerable<(string File, string Text)> SourceFiles() =>
        Directory.EnumerateFiles(Sources, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(Sources, f).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Select(f => (Path.GetRelativePath(Sources, f), WithoutComments(File.ReadAllText(f))));

    [Fact]
    public void Every_action_constant_of_an_approval_is_known_or_asks_nothing()
    {
        var unknown = new List<string>();
        var found = 0;
        foreach (var (file, text) in SourceFiles())
        {
            foreach (Match match in ActionConstant().Matches(text))
            {
                found++;
                var value = match.Groups[2].Value;
                if (ApprovalRiskClassifier.KnownPresenceActions.ContainsKey(value))
                {
                    continue;
                }

                var request = new ApprovalRequest(value, string.Empty, RiskLevel.Low, null, []);
                if (!LowActions.Contains(value) || ApprovalRiskClassifier.PresenceOf(request) != PresenceRequirement.Never)
                {
                    unknown.Add($"{file}: {match.Groups[1].Value} = \"{value}\"");
                }
            }
        }

        Assert.True(found >= 20, $"Only {found} action constants found: the scan does not read the sources.");
        Assert.True(unknown.Count == 0, "Actions of approvals the list of the barrier does not name (add them to ApprovalRiskClassifier):\n" + string.Join('\n', unknown));
    }

    /// <summary>
    /// Phase 10 (docs/21 D-15 and ADR-0066): everything that writes on the remote with the account of the person is known by name as a
    /// <c>RemoteOrSecretOperation</c>, «siempre» whatever any setting says, with whatever risk its caller declares; and the action each operation asks
    /// with is one of the six.
    /// </summary>
    [Fact]
    public void Every_action_of_the_remote_is_known_by_name_as_a_remote_operation_that_always_asks_for_the_person()
    {
        Assert.Equal(6, HostingActions.All.Count);
        foreach (var action in HostingActions.All)
        {
            Assert.Equal(PresenceAction.RemoteOrSecretOperation, ApprovalRiskClassifier.KnownPresenceActions[action]);
            foreach (var risk in Enum.GetValues<RiskLevel>())
            {
                Assert.Equal(PresenceRequirement.Always, ApprovalRiskClassifier.PresenceOf(new ApprovalRequest(action, string.Empty, risk, null, [])));
            }
        }

        foreach (var operation in Enum.GetValues<HostingOperationKind>())
        {
            Assert.Contains(HostingActions.ActionOf(operation), HostingActions.All);
        }

        Assert.DoesNotContain(HostingActions.GrantPolicyAction, Enum.GetValues<HostingOperationKind>().Select(HostingActions.ActionOf));
    }

    [Fact]
    public void Every_approval_names_its_action_by_a_constant()
    {
        var problems = new List<string>();
        var requests = 0;
        foreach (var (file, text) in SourceFiles())
        {
            foreach (Match match in NewRequest().Matches(text))
            {
                requests++;
                var action = FirstArgument(text, match.Index + match.Length);
                if (!ConstantOrForwarded().IsMatch(action))
                {
                    problems.Add($"{file}: new ApprovalRequest({action}, …)");
                }
            }

            // The engine forwards the action of its approvals (Workers.ApprovalAsync): what reaches it is a constant too.
            foreach (Match match in StartApproval().Matches(text))
            {
                var arguments = Arguments(text, match.Index + match.Length);
                if (arguments.Count >= 3 && !ConstantOrForwarded().IsMatch(arguments[2]))
                {
                    problems.Add($"{file}: StartApproval(…, {arguments[2]}, …)");
                }
            }
        }

        Assert.True(requests >= 10, $"Only {requests} approval requests found: the scan does not read the sources.");
        Assert.True(problems.Count == 0, "Approvals whose action is not a constant (name it with a constant …Action):\n" + string.Join('\n', problems));
    }

    /// <summary>The first argument of a call whose text starts at <paramref name="start"/>, after its opening parenthesis.</summary>
    private static string FirstArgument(string text, int start) => Arguments(text, start).FirstOrDefault() ?? string.Empty;

    /// <summary>The top-level arguments of a call, from just after its opening parenthesis to the one that closes it.</summary>
    private static List<string> Arguments(string text, int start)
    {
        var arguments = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                var end = text.IndexOf('"', i + 1);
                current.Append(text, i, (end < 0 ? text.Length : end + 1) - i);
                i = end < 0 ? text.Length : end;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    arguments.Add(current.ToString().Trim());
                    return arguments;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        return arguments;
    }

    /// <summary>The code without its line comments (and so without its documentation comments).</summary>
    private static string WithoutComments(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
