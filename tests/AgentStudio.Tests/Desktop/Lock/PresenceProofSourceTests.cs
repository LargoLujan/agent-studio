using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Domain.Security;
using AgentStudio.Tests.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// The residual of the re-review of the P0 (2026-11-08) and the security reviews of WP5 (2026-11-09): <see cref="PresenceProof"/>
/// is an enum a caller passes, so a path could claim a proof Windows never gave. The two proofs that confirm the person
/// (<see cref="PresenceProof.WindowsHello"/> and <see cref="PresenceProof.WindowsLockUnlockedAfterRequest"/>) come only from
/// <see cref="PresenceProofs"/>, from a result of Windows; what carries them (<see cref="HelloOutcome"/>) is built only where
/// Windows answers, and what decides what they must be (<see cref="ConfirmationPlan"/>) only where the rule plans it.
/// </summary>
/// <remarks>
/// <para>A scan of the text of each file of <c>src</c>, its comment lines blanked, so an expression split over several lines is
/// read whole (<see cref="Findings"/>; ADR-0053 says the same). Outside the files allowed for each, it finds:</para>
/// <list type="bullet">
/// <item>the two strong proofs by name, a cast to the enum (<c>(PresenceProof)</c>), the generic or <c>typeof</c> methods of
/// <see cref="Enum"/> that make one, and arithmetic or bitwise operators next to a value of the enum
/// (<c>PresenceProof.None + 1</c>);</item>
/// <item>anywhere, a <c>using static</c> of <c>PresenceProof</c> and an alias of <c>PresenceProof</c>, <c>HelloOutcome</c> or
/// <c>ConfirmationPlan</c>, which would hide the names above;</item>
/// <item>a result of Windows written as a literal into a conversion (<c>FromHello(UserPresenceResult.Verified)</c>): only the
/// reading of a recorded claim does that, in <c>PresenceProofs.Claimed</c>;</item>
/// <item>a <see cref="HelloOutcome"/> made by hand: <c>new HelloOutcome(</c>, a target-typed <c>new(</c> (a declaration, a
/// property, an expression body or a <c>return</c> of a method that returns one) and a <c>with</c> that sets <c>Result</c>,
/// <c>Problem</c>, <c>Lock</c> or <c>AnotherPending</c>. Only <c>ReinforcedConfirmation.ConfirmAsync</c> makes one, and never
/// with a literal result that confirms;</item>
/// <item>a <see cref="ConfirmationPlan"/> made by hand, in the same forms, or a <c>with</c> that sets any of what decides it
/// (<c>Confirmation</c>, <c>Availability</c>, <c>Requirement</c>, <c>Action</c>, <c>SettingOn</c>): only
/// <c>ReinforcedConfirmation</c> plans one.</item>
/// </list>
/// <para>What it cannot see, and the review has to (ADR-0053 lists the same): a variable of the enum incremented
/// (<c>proof++</c>), reflection, <c>dynamic</c>, <c>Unsafe.As</c>, and a target-typed <c>new(…)</c> given as an argument or
/// returned by a lambda, where the type is only in the signature of what receives it. A <c>with</c> that sets one of those
/// names on another record is read as one of these too: that is on the safe side, and the file goes in the allowed list
/// with its reason.</para>
/// </remarks>
public sealed partial class PresenceProofSourceTests
{
    private static readonly string Contracts = Path.Combine("AgentStudio.Application", "Security", "UserPresenceContracts.cs");
    private static readonly string Policy = Path.Combine("AgentStudio.Domain", "Security", "PresencePolicy.cs");
    private static readonly string Confirmation = Path.Combine("AgentStudio.Desktop", "Orchestrator", "Approvals", "ApprovalPromptService.cs");

    /// <summary>A file outside every allowed one, for the cases of the scan itself.</summary>
    private static readonly string Elsewhere = Path.Combine("AgentStudio.Desktop", "Lock", "Elsewhere.cs");

    /// <summary>What makes a proof that confirms the person by hand: allowed only in the rule and in the conversions from Windows.</summary>
    [GeneratedRegex(
        @"PresenceProof\.(WindowsHello|WindowsLockUnlockedAfterRequest)\b"
        + @"|\(\s*PresenceProof\s*\)"
        + @"|Enum\.\w+\s*<\s*PresenceProof\s*>"
        + @"|Enum\.\w+\s*\(\s*typeof\s*\(\s*PresenceProof\s*\)"
        + @"|PresenceProof\.\w+\s*(\+|-|\*|/|%|\||&|\^|<<|>>)(?!=)"
        + @"|(\+|-|\*|/|%|\||&|\^|~)\s*PresenceProof\.\w+")]
    private static partial Regex StrongProofByHand();

    /// <summary>A <c>using static</c> of the enum, or an alias of the enum, the outcome or the plan: allowed nowhere.</summary>
    [GeneratedRegex(@"(?:global\s+)?using\s+(?:static\s+(?:global::)?[\w.]*\bPresenceProof|\w+\s*=\s*(?:global::)?[\w.]*\b(?:PresenceProof|HelloOutcome|ConfirmationPlan))\s*;")]
    private static partial Regex HiddenName();

    /// <summary>A result of Windows that confirms, written as a literal into a conversion: allowed only to read a recorded claim.</summary>
    [GeneratedRegex(@"FromHello\(\s*UserPresenceResult\.Verified\s*\)|FromWindowsLock\(\s*WindowsLockResult\.UnlockedAfterRequest\s*\)")]
    private static partial Regex ConfirmingResultByHand();

    [GeneratedRegex(@"UserPresenceResult\.Verified|WindowsLockResult\.UnlockedAfterRequest")]
    private static partial Regex ConfirmingResult();

    /// <summary>An outcome made by hand: explicitly, target-typed in a declaration, a property or an expression body, or by <c>with</c>.</summary>
    [GeneratedRegex(
        @"new\s+HelloOutcome\s*\("
        + @"|\bHelloOutcome\??\s+\w+\s*(?:=|=>|\{[^{}]*\}\s*=)\s*new\s*\("
        + @"|\bwith\s*\{[^{}]*\b(?:Result|Problem|Lock|AnotherPending)\s*=")]
    private static partial Regex OutcomeByHand();

    /// <summary>A plan made by hand, in the same forms, or a <c>with</c> that sets what decides it.</summary>
    [GeneratedRegex(
        @"new\s+ConfirmationPlan\s*\("
        + @"|\bConfirmationPlan\??\s+\w+\s*(?:=|=>|\{[^{}]*\}\s*=)\s*new\s*\("
        + @"|\bwith\s*\{[^{}]*\b(?:Confirmation|Availability|Requirement|Action|SettingOn)\s*=")]
    private static partial Regex PlanByHand();

    /// <summary>The start of a method that returns an outcome or a plan (also in a task): its body is read for a target-typed <c>return new(</c>.</summary>
    [GeneratedRegex(@"\b(HelloOutcome|ConfirmationPlan)\??\s*>?\s+\w+\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex ReturnsOutcomeOrPlan();

    [GeneratedRegex(@"\breturn\s+new\s*\(")]
    private static partial Regex ReturnTargetTyped();

    [GeneratedRegex(@"^=>\s*new\s*\(")]
    private static partial Regex BodyTargetTyped();

    /// <summary>Every file of <c>src</c>, relative to it, with its comment lines blanked (so the lines keep their numbers).</summary>
    private static IEnumerable<(string File, string Code)> SourceFiles()
    {
        var sources = Path.Combine(LocalizationTestSupport.RepositoryRoot, "src");
        foreach (var file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sources, file);
            if (!relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            {
                yield return (relative, WithoutComments(File.ReadAllText(file)));
            }
        }
    }

    private static string WithoutComments(string text) =>
        string.Join('\n', text.Split('\n').Select(l => l.TrimStart() is var code && (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith('*')) ? string.Empty : l));

    private static bool Is(string file, string allowed) => string.Equals(file, allowed, StringComparison.OrdinalIgnoreCase);

    /// <summary>The lines of <paramref name="code"/>, a file of <c>src</c> named <paramref name="file"/>, where something of the confirmation is made by hand.</summary>
    private static IReadOnlyList<int> Findings(string file, string code)
    {
        var lines = new SortedSet<int>();
        var confirmation = Is(file, Confirmation);

        void Each(Regex regex, Func<Match, bool> found)
        {
            foreach (Match match in regex.Matches(code))
            {
                if (found(match))
                {
                    lines.Add(LineOf(code, match.Index));
                }
            }
        }

        Each(StrongProofByHand(), _ => !Is(file, Policy) && !Is(file, Contracts));
        Each(HiddenName(), _ => true);
        Each(ConfirmingResultByHand(), _ => !Is(file, Contracts));

        // Where the confirmation is, an outcome is made from what Windows answered: never from a literal that confirms.
        Each(OutcomeByHand(), m => !confirmation || ConfirmingResult().IsMatch(Statement(code, m.Index)));
        Each(PlanByHand(), _ => !confirmation);
        foreach (var (index, type) in TargetTypedReturns(code))
        {
            if (!confirmation || (type == "HelloOutcome" && ConfirmingResult().IsMatch(Statement(code, index))))
            {
                lines.Add(LineOf(code, index));
            }
        }

        return [.. lines];
    }

    /// <summary>Where a method that returns an outcome or a plan returns a target-typed <c>new(</c>, with the type it returns.</summary>
    private static IEnumerable<(int Index, string Type)> TargetTypedReturns(string code)
    {
        foreach (Match match in ReturnsOutcomeOrPlan().Matches(code))
        {
            var type = match.Groups[1].Value;
            var parameters = Close(code, match.Index + match.Length - 1);
            if (parameters < 0)
            {
                continue;
            }

            var i = parameters + 1;
            while (i < code.Length && code[i] != '{' && code[i] != ';' && !(code[i] == '=' && i + 1 < code.Length && code[i + 1] == '>'))
            {
                i++;
            }

            if (i >= code.Length || code[i] == ';')
            {
                continue;
            }

            if (code[i] == '=')
            {
                var end = code.IndexOf(';', i);
                if (BodyTargetTyped().IsMatch(code[i..(end < 0 ? code.Length : end)]))
                {
                    yield return (i, type);
                }

                continue;
            }

            var close = Close(code, i);
            var body = code[i..(close < 0 ? code.Length : close)];
            foreach (Match found in ReturnTargetTyped().Matches(body))
            {
                yield return (i + found.Index, type);
            }
        }
    }

    /// <summary>The index of the bracket that closes the one at <paramref name="open"/>; -1 when it is not closed. Text in quotes is skipped.</summary>
    private static int Close(string code, int open)
    {
        var (opening, closing) = code[open] switch { '(' => ('(', ')'), '{' => ('{', '}'), _ => ('[', ']') };
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] is '"' or '\'')
            {
                var quote = code[i];
                for (i++; i < code.Length && code[i] != quote; i++)
                {
                    if (code[i] == '\\')
                    {
                        i++;
                    }
                }

                continue;
            }

            if (code[i] == opening)
            {
                depth++;
            }
            else if (code[i] == closing && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The statement that starts at <paramref name="index"/>: up to its semicolon.</summary>
    private static string Statement(string code, int index)
    {
        var end = code.IndexOf(';', index);
        return code[index..(end < 0 ? code.Length : end)];
    }

    private static int LineOf(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

    [Fact]
    public void No_code_makes_up_a_proof_that_confirms_the_person()
    {
        var found = new List<string>();
        var files = 0;
        foreach (var (file, code) in SourceFiles())
        {
            files++;
            var lines = code.Split('\n');
            found.AddRange(Findings(file, code).Select(line => $"{file}:{line}: {lines[line - 1].Trim()}"));
        }

        Assert.True(files > 100, $"Only {files} files read: the scan does not read the sources.");
        Assert.True(found.Count == 0, "A proof, an outcome or a plan of the confirmation made by hand (they come from PresenceProofs and ReinforcedConfirmation):\n" + string.Join('\n', found));
    }

    /// <summary>Every form the scan looks for is found in a file outside the allowed ones, and what is allowed is not.</summary>
    [Theory]
    [InlineData("var p = PresenceProof.WindowsHello;", true)]
    [InlineData("var p = (PresenceProof)3;", true)]
    [InlineData("var p = PresenceProof.None + 1;", true)]
    [InlineData("var p = PresenceProof.Dialog | PresenceProof.None;", true)]
    [InlineData("var p = PresenceProof.None\n    + 1;", true)]
    [InlineData("var p = Enum.Parse<PresenceProof>(text);", true)]
    [InlineData("var p = (PresenceProof)Enum.ToObject(typeof(PresenceProof), 3);", true)]
    [InlineData("using static AgentStudio.Domain.Security.PresenceProof;", true)]
    [InlineData("global using static global::AgentStudio.Domain.Security.PresenceProof;", true)]
    [InlineData("using P = AgentStudio.Domain.Security.PresenceProof;", true)]
    [InlineData("using O = AgentStudio.Desktop.Orchestrator.Approvals.HelloOutcome;", true)]
    [InlineData("var p = PresenceProofs.FromHello(UserPresenceResult.Verified);", true)]
    [InlineData("return new HelloOutcome(result);", true)]
    [InlineData("HelloOutcome x = new(UserPresenceResult.Verified);", true)]
    [InlineData("HelloOutcome? x = new(null, Lock: WindowsLockResult.UnlockedAfterRequest);", true)]
    [InlineData("var faked = outcome with { Result = UserPresenceResult.Verified };", true)]
    [InlineData("var faked = outcome with\n{\n    Lock = WindowsLockResult.UnlockedAfterRequest,\n};", true)]
    [InlineData("static HelloOutcome Confirmed() => new(UserPresenceResult.Verified);", true)]
    [InlineData("public HelloOutcome Last { get; } = new(UserPresenceResult.Verified);", true)]
    [InlineData("async Task<HelloOutcome> FakeAsync(string message)\n{\n    await Task.Yield();\n    return new(UserPresenceResult.Verified);\n}", true)]
    [InlineData("var plan = new ConfirmationPlan(ApprovalVerification.WindowsHello);", true)]
    [InlineData("ConfirmationPlan plan = new(ApprovalVerification.WindowsLock, UserPresenceAvailability.NotConfiguredForUser);", true)]
    [InlineData("var plan = ConfirmationPlan.AnswerAlone with { Availability = UserPresenceAvailability.NotConfiguredForUser };", true)]
    [InlineData("var plan = old with { Confirmation = ApprovalVerification.WindowsLock };", true)]
    [InlineData("var plan = old with { Requirement = PresenceRequirement.Never };", true)]
    [InlineData("var plan = old with { Action = PresenceAction.AnswerAgentQuestion };", true)]
    [InlineData("var plan = old with\n{\n    SettingOn = false,\n};", true)]
    [InlineData("static ConfirmationPlan Weak() => new(ApprovalVerification.Dialog);", true)]
    [InlineData("ConfirmationPlan Weak()\n{\n    return new(ApprovalVerification.Dialog);\n}", true)]
    [InlineData("var p = PresenceProof.Dialog;", false)]
    [InlineData("if (proof is PresenceProof.None) { }", false)]
    [InlineData("var p = PresenceProofs.FromHello(result);", false)]
    [InlineData("using static AgentStudio.Application.Security.PresenceProofs;", false)]
    [InlineData("var plan = ConfirmationPlan.AnswerAlone;", false)]
    [InlineData("var request = asked with { Risk = risk };", false)]
    [InlineData("HelloOutcome outcome = await confirmation.ConfirmAsync(plan, message, token);", false)]
    [InlineData("public static bool Approves(ConfirmationPlan plan, PresenceProof proof) => plan.Action is { } action;", false)]
    public void The_scan_finds_what_is_made_by_hand_elsewhere(string code, bool found)
    {
        Assert.Equal(found, Findings(Elsewhere, code).Count > 0);
    }

    /// <summary>Where the confirmation is, an outcome is made from what Windows answered, and a plan is planned: a literal that confirms is still found.</summary>
    [Theory]
    [InlineData("return new HelloOutcome(result);", false)]
    [InlineData("return new HelloOutcome(null, Lock: locked);", false)]
    [InlineData("return ConfirmationPlan.AnswerAlone with { Requirement = requirement, Action = action };", false)]
    [InlineData("return new ConfirmationPlan(ApprovalVerification.WindowsHello, availability, requirement, action, settingOn);", false)]
    [InlineData("return new HelloOutcome(UserPresenceResult.Verified);", true)]
    [InlineData("return outcome with { Lock = WindowsLockResult.UnlockedAfterRequest };", true)]
    [InlineData("async Task<HelloOutcome> ConfirmAsync()\n{\n    return new(UserPresenceResult.Verified);\n}", true)]
    public void Where_the_confirmation_is_only_a_literal_that_confirms_is_found(string code, bool found)
    {
        Assert.Equal(found, Findings(Confirmation, code).Count > 0);
    }

    /// <summary>The line of what is found is its own, also after a multi-line expression.</summary>
    [Fact]
    public void The_scan_says_the_line_of_what_it_finds()
    {
        Assert.Equal([3], Findings(Elsewhere, "var a = 1;\nvar b = 2;\nvar faked = outcome with { Result = UserPresenceResult.Verified };"));
    }

    /// <summary>What a confirmation proves is what <see cref="PresenceProofs"/> says of its result, for every result Windows can give.</summary>
    [Fact]
    public void The_proof_of_a_confirmation_is_the_one_presence_proofs_gives_its_result()
    {
        Assert.All(Enum.GetValues<UserPresenceResult>(), r => Assert.Equal(PresenceProofs.FromHello(r), new HelloOutcome(r).Proof));
        Assert.All(Enum.GetValues<WindowsLockResult>(), r => Assert.Equal(PresenceProofs.FromWindowsLock(r), new HelloOutcome(null, Lock: r).Proof));
        Assert.Equal(PresenceProof.None, new HelloOutcome(null, "The interop is not there.").Proof);
        Assert.Equal(PresenceProof.None, new HelloOutcome(null, AnotherPending: true).Proof);
    }

    /// <summary>The answer alone proves the dialog, with or without its warning: never what confirms the person.</summary>
    [Theory]
    [InlineData(ApprovalVerification.Dialog, PresenceProof.Dialog)]
    [InlineData(ApprovalVerification.WindowsHelloUnavailable, PresenceProof.DialogWithHelloUnavailable)]
    public void The_answer_alone_proves_only_the_dialog(ApprovalVerification confirmation, PresenceProof proof)
    {
        Assert.Equal(proof, new ConfirmationPlan(confirmation).AnswerAloneProof);
    }

    /// <summary>The gate, the engine and the recorder read a recorded method as the proof it claims, and the proofs that confirm come from <see cref="PresenceProofs"/>.</summary>
    [Fact]
    public void A_recorded_method_is_read_as_the_proof_presence_proofs_gives()
    {
        Assert.Equal(PresenceProofs.FromHello(UserPresenceResult.Verified), PresenceProofs.Claimed(ApprovalVerification.WindowsHello));
        Assert.Equal(PresenceProofs.FromWindowsLock(WindowsLockResult.UnlockedAfterRequest), PresenceProofs.Claimed(ApprovalVerification.WindowsLock));
        Assert.Equal(PresenceProof.Dialog, PresenceProofs.Claimed(ApprovalVerification.Dialog));
        Assert.Equal(PresenceProof.DialogWithHelloUnavailable, PresenceProofs.Claimed(ApprovalVerification.WindowsHelloUnavailable));
        Assert.Equal(PresenceProof.None, PresenceProofs.Claimed((ApprovalVerification)42));
        Assert.All(Enum.GetValues<ApprovalVerification>(), v => Assert.Equal(v, PresenceProofs.Verification(PresenceProofs.Claimed(v))));
    }
}
