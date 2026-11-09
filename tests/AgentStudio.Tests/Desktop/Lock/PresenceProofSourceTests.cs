using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Domain.Security;
using AgentStudio.Tests.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// The residual of the re-review of the P0 (2026-11-08) and the security review of WP5 (2026-11-09): <see cref="PresenceProof"/>
/// is an enum a caller passes, so a path could claim a proof Windows never gave. The two proofs that confirm the person
/// (<see cref="PresenceProof.WindowsHello"/> and <see cref="PresenceProof.WindowsLockUnlockedAfterRequest"/>) come only from
/// <see cref="PresenceProofs"/>, from a result of Windows; what carries them (<see cref="HelloOutcome"/>) is built only where
/// Windows answers, and what decides what they must be (<see cref="ConfirmationPlan"/>) only where the rule plans it.
/// </summary>
/// <remarks>
/// <para>A scan of the text of <c>src</c>, line by line, without the comments. It finds, outside the files allowed for each:</para>
/// <list type="bullet">
/// <item>the two strong proofs by name, a cast to the enum (<c>(PresenceProof)</c>), the generic or <c>typeof</c> methods of
/// <see cref="Enum"/> that make one, and arithmetic or bitwise operators next to a value of the enum
/// (<c>PresenceProof.None + 1</c>);</item>
/// <item>a result of Windows written as a literal into a conversion (<c>FromHello(UserPresenceResult.Verified)</c>): only the
/// reading of a recorded claim does that, in <c>PresenceProofs.Claimed</c>;</item>
/// <item><c>new HelloOutcome(</c> outside <c>ReinforcedConfirmation.ConfirmAsync</c>, and, there, with a literal result that
/// confirms;</item>
/// <item><c>new ConfirmationPlan(</c> outside <c>ReinforcedConfirmation</c>, and a <c>with</c> that sets what decides the plan
/// (<c>Confirmation</c>, <c>Availability</c>, <c>SettingOn</c>).</item>
/// </list>
/// <para>What it cannot see, and the review has to: an alias of the type (<c>using P = PresenceProof;</c>), a variable of the
/// enum incremented (<c>proof++</c>), reflection, <c>dynamic</c>, <c>Unsafe.As</c>, an expression split over several lines, and a
/// value of the enum that arrives already made (a number read from a file and cast elsewhere is caught by the cast).</para>
/// </remarks>
public sealed partial class PresenceProofSourceTests
{
    private static readonly string Contracts = Path.Combine("AgentStudio.Application", "Security", "UserPresenceContracts.cs");
    private static readonly string Policy = Path.Combine("AgentStudio.Domain", "Security", "PresencePolicy.cs");
    private static readonly string Confirmation = Path.Combine("AgentStudio.Desktop", "Orchestrator", "Approvals", "ApprovalPromptService.cs");

    /// <summary>What makes a proof that confirms the person by hand: allowed only in the rule and in the conversions from Windows.</summary>
    [GeneratedRegex(
        @"PresenceProof\.(WindowsHello|WindowsLockUnlockedAfterRequest)\b"
        + @"|\(\s*PresenceProof\s*\)"
        + @"|Enum\.\w+\s*<\s*PresenceProof\s*>"
        + @"|Enum\.\w+\s*\(\s*typeof\s*\(\s*PresenceProof\s*\)"
        + @"|PresenceProof\.\w+\s*(\+|-|\*|/|%|\||&|\^|<<|>>)(?!=)"
        + @"|(\+|-|\*|/|%|\||&|\^|~)\s*PresenceProof\.\w+")]
    private static partial Regex StrongProofByHand();

    /// <summary>A result of Windows that confirms, written as a literal into a conversion: allowed only to read a recorded claim.</summary>
    [GeneratedRegex(@"FromHello\(\s*UserPresenceResult\.Verified\s*\)|FromWindowsLock\(\s*WindowsLockResult\.UnlockedAfterRequest\s*\)")]
    private static partial Regex ConfirmingResultByHand();

    [GeneratedRegex(@"new\s+HelloOutcome\s*\(")]
    private static partial Regex NewOutcome();

    [GeneratedRegex(@"UserPresenceResult\.Verified|WindowsLockResult\.UnlockedAfterRequest")]
    private static partial Regex ConfirmingResult();

    [GeneratedRegex(@"new\s+ConfirmationPlan\s*\(|with\s*\{[^}]*\b(Confirmation|Availability|SettingOn)\s*=")]
    private static partial Regex PlanByHand();

    /// <summary>Every line of code of <c>src</c> (no comments), with its file relative to <c>src</c>.</summary>
    private static IEnumerable<(string File, int Line, string Text)> CodeLines()
    {
        var sources = Path.Combine(LocalizationTestSupport.RepositoryRoot, "src");
        foreach (var file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sources, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (!code.StartsWith("//", StringComparison.Ordinal) && !code.StartsWith('*'))
                {
                    yield return (relative, i + 1, lines[i]);
                }
            }
        }
    }

    private static bool Is(string file, string allowed) => string.Equals(file, allowed, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void No_code_makes_up_a_proof_that_confirms_the_person()
    {
        var found = new List<string>();
        foreach (var (file, line, text) in CodeLines())
        {
            if (StrongProofByHand().IsMatch(text) && !Is(file, Policy) && !Is(file, Contracts))
            {
                found.Add($"{file}:{line}: {text.Trim()}");
            }

            if (ConfirmingResultByHand().IsMatch(text) && !Is(file, Contracts))
            {
                found.Add($"{file}:{line}: {text.Trim()}");
            }

            if (NewOutcome().IsMatch(text) && (!Is(file, Confirmation) || ConfirmingResult().IsMatch(text)))
            {
                found.Add($"{file}:{line}: {text.Trim()}");
            }

            if (PlanByHand().IsMatch(text) && !Is(file, Confirmation))
            {
                found.Add($"{file}:{line}: {text.Trim()}");
            }
        }

        Assert.True(found.Count == 0, "A proof, an outcome or a plan of the confirmation made by hand (they come from PresenceProofs and ReinforcedConfirmation):\n" + string.Join('\n', found));
    }

    /// <summary>The scan itself finds what it looks for, and leaves what is allowed.</summary>
    [Theory]
    [InlineData("var p = PresenceProof.WindowsHello;", true)]
    [InlineData("var p = (PresenceProof)3;", true)]
    [InlineData("var p = PresenceProof.None + 1;", true)]
    [InlineData("var p = PresenceProof.Dialog | PresenceProof.None;", true)]
    [InlineData("var p = Enum.Parse<PresenceProof>(text);", true)]
    [InlineData("var p = (PresenceProof)Enum.ToObject(typeof(PresenceProof), 3);", true)]
    [InlineData("var p = PresenceProof.Dialog;", false)]
    [InlineData("if (proof is PresenceProof.None) { }", false)]
    [InlineData("var p = PresenceProofs.FromHello(result);", false)]
    public void The_scan_finds_a_proof_made_by_hand(string code, bool found)
    {
        Assert.Equal(found, StrongProofByHand().IsMatch(code));
    }

    [Theory]
    [InlineData("return new HelloOutcome(UserPresenceResult.Verified);", true, true)]
    [InlineData("return new HelloOutcome(null, Lock: WindowsLockResult.UnlockedAfterRequest);", true, true)]
    [InlineData("return new HelloOutcome(result);", true, false)]
    public void The_scan_finds_an_outcome_made_by_hand(string code, bool outcome, bool confirming)
    {
        Assert.Equal((outcome, confirming), (NewOutcome().IsMatch(code), ConfirmingResult().IsMatch(code)));
    }

    [Theory]
    [InlineData("var plan = new ConfirmationPlan(ApprovalVerification.WindowsHello);", true)]
    [InlineData("var plan = ConfirmationPlan.AnswerAlone with { Availability = UserPresenceAvailability.NotConfiguredForUser };", true)]
    [InlineData("var plan = old with { Confirmation = ApprovalVerification.WindowsLock };", true)]
    [InlineData("var request = asked with { Risk = risk };", false)]
    [InlineData("var plan = ConfirmationPlan.AnswerAlone;", false)]
    public void The_scan_finds_a_plan_made_by_hand(string code, bool found)
    {
        Assert.Equal(found, PlanByHand().IsMatch(code));
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
