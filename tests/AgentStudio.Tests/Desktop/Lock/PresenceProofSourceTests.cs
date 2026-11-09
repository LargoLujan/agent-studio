using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Domain.Security;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// The residual of the re-review of the P0 (2026-11-08): <see cref="PresenceProof"/> is an enum a caller passes, so a path could
/// claim a proof Windows never gave. The two proofs that confirm the person (<see cref="PresenceProof.WindowsHello"/> and
/// <see cref="PresenceProof.WindowsLockUnlockedAfterRequest"/>) come only from <see cref="PresenceProofs.FromHello"/> and
/// <see cref="PresenceProofs.FromWindowsLock"/>, that is, from a result of Windows: no code outside the domain rule and those
/// conversions names them, casts to the enum or parses it.
/// </summary>
public sealed partial class PresenceProofSourceTests
{
    /// <summary>The only files that may name the proofs that confirm the person: the rule and the conversions from Windows.</summary>
    private static readonly string[] Allowed =
    [
        Path.Combine("AgentStudio.Domain", "Security", "PresencePolicy.cs"),
        Path.Combine("AgentStudio.Application", "Security", "UserPresenceContracts.cs"),
    ];

    [GeneratedRegex(@"PresenceProof\.(WindowsHello|WindowsLockUnlockedAfterRequest)\b|\(\s*PresenceProof\s*\)|Enum\.(Parse|TryParse|GetValues|ToObject)\s*<\s*PresenceProof\s*>|Enum\.(Parse|TryParse|ToObject)\s*\(\s*typeof\s*\(\s*PresenceProof\s*\)")]
    private static partial Regex StrongProofByHand();

    [Fact]
    public void No_code_makes_up_a_proof_that_confirms_the_person()
    {
        var sources = Path.Combine(LocalizationTestSupport.RepositoryRoot, "src");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sources, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj") || Allowed.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && StrongProofByHand().IsMatch(lines[i]))
                {
                    found.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(found.Count == 0, "A proof that confirms the person must come from PresenceProofs (a result of Windows):\n" + string.Join('\n', found));
    }

    /// <summary>What a confirmation proves is what <see cref="PresenceProofs"/> says of its result, for every result Windows can give.</summary>
    [Fact]
    public void The_proof_of_a_confirmation_is_the_one_presence_proofs_gives_its_result()
    {
        Assert.All(Enum.GetValues<UserPresenceResult>(), r => Assert.Equal(PresenceProofs.FromHello(r), new HelloOutcome(r).Proof));
        Assert.All(Enum.GetValues<WindowsLockResult>(), r => Assert.Equal(PresenceProofs.FromWindowsLock(r), new HelloOutcome(null, Lock: r).Proof));
        Assert.Equal(PresenceProof.None, new HelloOutcome(null, "The interop is not there.").Proof);
    }

    /// <summary>The answer alone proves the dialog, with or without its warning: never what confirms the person.</summary>
    [Theory]
    [InlineData(ApprovalVerification.Dialog, PresenceProof.Dialog)]
    [InlineData(ApprovalVerification.WindowsHelloUnavailable, PresenceProof.DialogWithHelloUnavailable)]
    public void The_answer_alone_proves_only_the_dialog(ApprovalVerification confirmation, PresenceProof proof)
    {
        Assert.Equal(proof, new ConfirmationPlan(confirmation).AnswerAloneProof);
    }

    /// <summary>The gate reads the method a prompt recorded as the proof it claims, and the proofs that confirm come from <see cref="PresenceProofs"/>.</summary>
    [Fact]
    public void The_gate_reads_a_recorded_method_as_the_proof_presence_proofs_gives()
    {
        Assert.Equal(PresenceProofs.FromHello(UserPresenceResult.Verified), ApprovalRiskClassifier.ClaimedProof(ApprovalVerification.WindowsHello));
        Assert.Equal(PresenceProofs.FromWindowsLock(WindowsLockResult.UnlockedAfterRequest), ApprovalRiskClassifier.ClaimedProof(ApprovalVerification.WindowsLock));
        Assert.Equal(PresenceProof.Dialog, ApprovalRiskClassifier.ClaimedProof(ApprovalVerification.Dialog));
        Assert.Equal(PresenceProof.DialogWithHelloUnavailable, ApprovalRiskClassifier.ClaimedProof(ApprovalVerification.WindowsHelloUnavailable));
        Assert.Equal(PresenceProof.None, ApprovalRiskClassifier.ClaimedProof((ApprovalVerification)42));
        Assert.All(Enum.GetValues<ApprovalVerification>(), v => Assert.Equal(v, PresenceProofs.Verification(ApprovalRiskClassifier.ClaimedProof(v))));
    }
}
