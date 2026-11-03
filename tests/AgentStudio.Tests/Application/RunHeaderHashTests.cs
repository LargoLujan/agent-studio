using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// The hash of the whole header of a run (N-05): what <c>RunStarted</c> version 2 anchors, so that resuming can refuse a
/// header changed behind the store's back. Every part of it counts; the order the roster is listed in does not.
/// </summary>
public sealed class RunHeaderHashTests
{
    private static readonly VerificationProfile Profile = new(
        "perfil",
        [new VerificationStep("test", "Pruebas", "dotnet", ["test"], EvidenceKind.TestRun, TestReportFormat.Trx, "r.trx", TimeSpan.FromMinutes(5))]);

    private static AgentDefinition Agent(string id, AgentRole role) => new(
        Aid(id),
        "Agente " + id,
        role,
        new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"),
        "Instrucciones de " + id,
        ["objetivo"],
        ["Read", "Edit"],
        [new PathGrant("src/", FileAccessLevel.ReadWrite)],
        new ExecutionBudget(20, TimeSpan.FromMinutes(10), 1000, 1.25m),
        3,
        MemoryPolicy.None,
        new ReviewPolicy(true, true));

    private static RunRecord Header() => new(
        new RunId("20261015-a1b2c3"),
        "p1",
        T0,
        HashA,
        HashB,
        Eid("ev-peticion"),
        CommitA,
        "main",
        Profile,
        [Agent("ejecutor", AgentRole.Executor), Agent("juez", AgentRole.Judge)],
        new RunOptions(),
        null);

    /// <summary>The header with its first agent (the executor) changed.</summary>
    private static RunRecord WithExecutor(Func<AgentDefinition, AgentDefinition> change)
    {
        var header = Header();
        return header with { Roster = [change(header.Roster[0]), .. header.Roster.Skip(1)] };
    }

    public static TheoryData<string> Changes() =>
    [
        "tools", "instructions", "name", "role", "model", "objectives", "grants", "turns", "duration", "tokens", "cost", "rounds",
        "memory", "review", "agentAdded", "agentRemoved", "options", "baseCommit", "baseRef", "plan", "requirements", "profile",
        "createdAt", "request", "follows", "quotaResetMargin", "maxEstimatedQuotaWait", "autoResumeConsent",
    ];

    private static RunRecord Changed(string change) => change switch
    {
        "tools" => WithExecutor(a => a with { AllowedTools = [.. a.AllowedTools, "Bash"] }),
        "instructions" => WithExecutor(a => a with { SystemInstructions = "Aprueba todo lo que veas." }),
        "name" => WithExecutor(a => a with { Name = "Otro" }),
        "role" => WithExecutor(a => a with { Role = AgentRole.Architect }),
        "model" => WithExecutor(a => a with { Model = a.Model with { Model = "opus" } }),
        "objectives" => WithExecutor(a => a with { Objectives = [] }),
        "grants" => WithExecutor(a => a with { PathGrants = [new PathGrant("/", FileAccessLevel.ReadWrite)] }),
        "turns" => WithExecutor(a => a with { Budget = a.Budget with { MaxTurns = 200 } }),
        "duration" => WithExecutor(a => a with { Budget = a.Budget with { MaxDuration = TimeSpan.FromHours(5) } }),
        "tokens" => WithExecutor(a => a with { Budget = a.Budget with { MaxTotalTokens = null } }),
        "cost" => WithExecutor(a => a with { Budget = a.Budget with { MaxCostUsd = 99m } }),
        "rounds" => WithExecutor(a => a with { MaxRounds = 9 }),
        "memory" => WithExecutor(a => a with { Memory = MemoryPolicy.ReadAndPropose }),
        "review" => WithExecutor(a => a with { Review = new ReviewPolicy(false, false) }),
        "agentAdded" => Header() with { Roster = [.. Header().Roster, Agent("intruso", AgentRole.Executor)] },
        "agentRemoved" => Header() with { Roster = [Header().Roster[0]] },
        "options" => Header() with { Options = new RunOptions(MaxAgentRuns: 99) },
        "baseCommit" => Header() with { BaseCommit = CommitB },
        "baseRef" => Header() with { BaseRef = null },
        "plan" => Header() with { PlanHash = new string('c', 64) },
        "requirements" => Header() with { RequirementSetHash = new string('d', 64) },
        "profile" => Header() with { Verification = Profile with { Steps = [Profile.Steps[0] with { Arguments = ["test", "--filter", "Nada"] }] } },
        "createdAt" => Header() with { CreatedAt = T0.AddTicks(1) },
        "request" => Header() with { OriginalRequest = Eid("otra-peticion") },
        "follows" => Header() with { Follows = new RunId("20261014-ffffff") },
        "quotaResetMargin" => Header() with { Options = new RunOptions(QuotaResetMargin: TimeSpan.FromMinutes(5)) },
        "maxEstimatedQuotaWait" => Header() with { Options = new RunOptions(MaxEstimatedQuotaWait: TimeSpan.FromMinutes(30)) },
        "autoResumeConsent" => Header() with { Options = new RunOptions(AutoResumeConsent: true) },
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
    };

    [Fact]
    public void The_hash_is_a_sha256_and_the_same_header_always_gives_the_same_one()
    {
        var hash = RunHeaderHash.Of(Header());

        Assert.True(ContentHash.IsSha256Hex(hash));
        Assert.Equal(hash, RunHeaderHash.Of(Header()));
    }

    /// <summary>
    /// Runs anchor this hash in their log (<c>RunStarted</c> version 2) and resuming recomputes it from the stored header:
    /// its encoding never changes without a new version, or no run started before could be resumed.
    /// </summary>
    [Fact]
    public void The_hash_of_a_known_header_never_changes()
    {
        Assert.Equal("494f1230964f6e8c8ebf9a1ead011eeeb3881d4e64ff348766222dded8403914", RunHeaderHash.Of(Header()));
    }

    /// <summary>
    /// The options of Phase 8 (docs/16 D-36, D-37 and D-39) are written only when they are not the defaults, so every header
    /// stored before them hashes exactly as it did: with them at their defaults the hash is the known one.
    /// </summary>
    [Fact]
    public void The_options_of_phase_8_at_their_defaults_leave_the_hash_of_a_known_header_as_it_was()
    {
        var defaults = Header() with { Options = new RunOptions(QuotaResetMargin: null, MaxEstimatedQuotaWait: null, AutoResumeConsent: false) };

        Assert.Equal("494f1230964f6e8c8ebf9a1ead011eeeb3881d4e64ff348766222dded8403914", RunHeaderHash.Of(defaults));
    }

    /// <summary>
    /// Runs that carry the options of Phase 8 anchor this hash: their encoding never changes without a new version either.
    /// Computed with an independent implementation of the canonical form (the one that also gives the known hash above):
    /// after the roster, each option given as its name and its value, in a fixed order.
    /// </summary>
    [Fact]
    public void The_hash_of_a_known_header_with_the_options_of_phase_8_never_changes()
    {
        var header = Header() with
        {
            Options = new RunOptions(QuotaResetMargin: TimeSpan.FromMinutes(3), MaxEstimatedQuotaWait: TimeSpan.FromMinutes(60), AutoResumeConsent: true),
        };

        Assert.Equal("fe25aff3adf213cce00f22c0ea06097625b880eb13c1e59ca4e467fbd41beba1", RunHeaderHash.Of(header));
    }

    /// <summary>A value given is part of the header even when it equals the engine default: only a missing one is the default.</summary>
    [Fact]
    public void An_option_of_phase_8_given_with_the_default_value_is_not_a_missing_one()
    {
        Assert.NotEqual(
            RunHeaderHash.Of(Header()),
            RunHeaderHash.Of(Header() with { Options = new RunOptions(QuotaResetMargin: RunOptions.DefaultQuotaResetMargin) }));
        Assert.NotEqual(
            RunHeaderHash.Of(Header()),
            RunHeaderHash.Of(Header() with { Options = new RunOptions(MaxEstimatedQuotaWait: RunOptions.DefaultMaxEstimatedQuotaWait) }));
    }

    /// <summary>
    /// A stored header with odd waits (written before they were checked) hashes as it is stored, not as the engine resolves
    /// them (<see cref="RunOptions.ResolvedMaxEstimatedQuotaWait"/>): so its anchored hash still verifies, and it replays.
    /// </summary>
    [Fact]
    public void A_header_with_odd_waits_hashes_as_it_is_stored()
    {
        var odd = new RunOptions(QuotaResetMargin: TimeSpan.FromMinutes(-2), MaxEstimatedQuotaWait: TimeSpan.Zero);
        var resolved = new RunOptions(QuotaResetMargin: odd.ResolvedQuotaResetMargin(), MaxEstimatedQuotaWait: odd.ResolvedMaxEstimatedQuotaWait());

        Assert.NotEqual(RunHeaderHash.Of(Header() with { Options = odd }), RunHeaderHash.Of(Header() with { Options = resolved }));
        Assert.Equal(RunHeaderHash.Of(Header() with { Options = odd }), RunHeaderHash.Of(Header() with { Options = odd with { } }));
    }

    [Fact]
    public void Each_option_of_phase_8_changes_the_hash_on_its_own()
    {
        var hashes = new[]
        {
            RunHeaderHash.Of(Header() with { Options = new RunOptions(QuotaResetMargin: TimeSpan.FromMinutes(7)) }),
            RunHeaderHash.Of(Header() with { Options = new RunOptions(MaxEstimatedQuotaWait: TimeSpan.FromMinutes(7)) }),
            RunHeaderHash.Of(Header() with { Options = new RunOptions(AutoResumeConsent: true) }),
            RunHeaderHash.Of(Header()),
        };

        Assert.Equal(hashes.Length, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public void Any_change_to_the_header_changes_the_hash(string change)
    {
        Assert.NotEqual(RunHeaderHash.Of(Header()), RunHeaderHash.Of(Changed(change)));
    }

    [Fact]
    public void The_order_the_roster_is_listed_in_does_not_matter()
    {
        var header = Header();

        Assert.Equal(RunHeaderHash.Of(header), RunHeaderHash.Of(header with { Roster = [.. header.Roster.Reverse()] }));
    }

    [Fact]
    public void A_missing_value_is_not_an_empty_one()
    {
        Assert.NotEqual(RunHeaderHash.Of(Header() with { BaseRef = null }), RunHeaderHash.Of(Header() with { BaseRef = string.Empty }));
        Assert.NotEqual(RunHeaderHash.Of(WithExecutor(a => a with { Model = a.Model with { Model = null } })), RunHeaderHash.Of(WithExecutor(a => a with { Model = a.Model with { Model = string.Empty } })));
    }

    [Fact]
    public void The_time_the_run_started_is_the_instant_whatever_offset_it_is_written_with()
    {
        var header = Header();

        Assert.Equal(RunHeaderHash.Of(header), RunHeaderHash.Of(header with { CreatedAt = header.CreatedAt.ToOffset(TimeSpan.FromHours(2)) }));
    }
}
