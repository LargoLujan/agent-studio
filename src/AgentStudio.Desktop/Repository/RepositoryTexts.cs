using AgentStudio.Application.Git;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Domain.Tracing;
using AgentStudio.Orchestration.Work;

namespace AgentStudio.Desktop.Repository;

/// <summary>
/// The words of «Repositorio y cambios» for what the readers say in codes and enums, from <c>Resources/RepositoryStrings.resx</c> in the
/// language in use (ADR-0054). A code the resources do not know (a new one) is said as unknown with the code, never as <c>[key]</c>, and
/// never raw. What git itself says (a subject, an author, a branch) is not here: it is data of whoever wrote it and is shown as it is.
/// </summary>
public static class RepositoryTexts
{
    /// <summary>
    /// Why the hardened git layer refused a repository, as the person reads it: the codes of <see cref="RepositorySummary.Warnings"/> and of
    /// <see cref="RemoteListing.RefusalCode"/>. A code is never shown alone and never with a path.
    /// </summary>
    public static string Refusal(string? code) => code switch
    {
        "not-a-repository" => Texts.Get("Repository_Refusal_NotARepository"),
        "inside-worktree-root" => Texts.Get("Repository_Refusal_InsideWorkRoot"),
        "unsafe-configuration" => Texts.Get("Repository_Refusal_UnsafeConfiguration"),
        "unsafe-repository" => Texts.Get("Repository_Refusal_UnsafeRepository"),
        "git-unavailable" => Texts.Get("Repository_Refusal_GitUnavailable"),
        "git-failed" => Texts.Get("Repository_Refusal_GitFailed"),
        "unreadable" => Texts.Get("Repository_Refusal_Unreadable"),
        "refused" => Texts.Get("Repository_Refusal_Refused"),
        RemoteListing.OutputTooBig => Texts.Get("Repository_Refusal_TooBig"),
        _ => Texts.Format("Repository_Refusal_Unknown", code ?? string.Empty),
    };

    /// <summary>The refusals of a summary, said together; empty codes read as a refusal without a reason.</summary>
    public static string Refusal(IReadOnlyList<string> codes) =>
        codes.Count == 0 ? Refusal("refused") : string.Join(" ", codes.Select(Refusal));

    /// <summary>What happened, as the timeline names it.</summary>
    public static string Kind(RepositoryActivityKind kind) => kind switch
    {
        RepositoryActivityKind.Integration => Texts.Get("Repository_Activity_Integration"),
        RepositoryActivityKind.Delivery => Texts.Get("Repository_Activity_Delivery"),
        RepositoryActivityKind.WorktreeRemoved => Texts.Get("Repository_Activity_WorktreeRemoved"),
        RepositoryActivityKind.RemoteOperation => Texts.Get("Hosting_Activity_Operation"),
        RepositoryActivityKind.RemoteObservation => Texts.Get("Hosting_Activity_Observation"),
        _ => kind.ToString(),
    };

    /// <summary>What failed in an event of the timeline, from its code (never the raw code alone).</summary>
    public static string Error(string code) => code switch
    {
        RepositoryActivityReader.ConflictCode => Texts.Get("Repository_Activity_ErrorConflict"),
        RepositoryActivityReader.FailedCode => Texts.Get("Repository_Activity_ErrorNoCommit"),

        // Phase 10: the failure of a remote operation is a code of the platform, of the adapter or of git (or hosting.outcome.*), said by the area of the hosting texts.
        _ when AgentStudio.Application.Localization.UserText.IsCode(code) && Texts.Current.Has(AgentStudio.Application.Localization.UserText.KeyOf(code)) => HostingTexts.Code(code),
        _ => Texts.Format("Repository_Activity_ErrorOther", code),
    };

    /// <summary>A node of the trace in words: «ejecución 2026…», «hito mil-…».</summary>
    public static string Node(TraceRef node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var noun = Named("Repository_Node_", node.Kind) ?? Texts.Get("Repository_Node_Other");
        return Texts.Format("Repository_Origin_Node", noun, node.Id);
    }

    /// <summary>Where a suggestion comes from, in words.</summary>
    public static string Source(TraceSuggestionSource source) => Named("Repository_Source_", source) ?? Texts.Get("Repository_Source_Other");

    /// <summary>Who asserted a link: the person («ti») or the system.</summary>
    public static string Who(TraceAssertedBy by) =>
        by == TraceAssertedBy.User ? Texts.Get("Repository_Origin_LinkPerson") : Texts.Get("Repository_Origin_LinkSystem");

    private static string? Named(string prefix, Enum value) => Texts.Current.Has(prefix + value) ? Texts.Get(prefix + value) : null;
}
