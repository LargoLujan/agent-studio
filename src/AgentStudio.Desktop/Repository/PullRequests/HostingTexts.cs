using System.Globalization;
using System.Text;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Desktop.Repository.PullRequests;

/// <summary>
/// The words of the screens of Phase 10 (docs/21 section 7, ADR-0054) for what the layers below say in codes: the outcomes of a call to the platform, the refusals of the
/// adapter and of the git executor, the mode of a merge, the reasons of the board. Everything comes from <c>Resources/HostingStrings.resx</c> as <c>Text_&lt;code&gt;</c>; a code
/// that has no text (a new one) is said with a generic sentence that names it, never as nothing, as <c>[key]</c> or as an exception.
/// </summary>
public static class HostingTexts
{
    /// <summary>The most characters of a code or of a text of the remote that the screen shows as data.</summary>
    public const int MaxDataLength = 200;

    /// <summary>
    /// Every code the layers below may hand the screen as a string: the refusals of the adapter of GitHub (WP2), of the git executor (WP1) and of the domain, the titles of
    /// the chain and the reason of the board (WP4), and the outcome of every call. A test walks them and asks for their text in both languages.
    /// </summary>
    public static IReadOnlyList<string> KnownCodes { get; } =
    [
        // The adapter of GitHub (docs/adr/0065 and Infrastructure/Hosting/GitHub): its refusal codes are text there, since Desktop does not see internal classes.
        "gh.not-installed", "capability.not-declared", "answer.invalid", "account.login-invalid", "remote.moved", "server.error", "text.title-invalid", "text.body-invalid",
        "commit.invalid", "merge.method-not-declared", "merge.blocked", "merge.head-moved", "branch.not-run-branch", "branch.head-differs", "reviewers.invalid", "verdict.run-invalid",
        "text.description-invalid", "reviews.too-many", "merge.rejected", "comment.lookup-unknown", "repository.merge-settings-missing",

        // The git executor (WP1).
        "configuration-refused", "inspection-failed", "inspection-output-too-large", "git-unavailable", "hooks-file-unsafe", "work-folder-unsafe", "project-unreadable",
        "invalid-address", "invalid-commit", "timed-out", "output-too-large", "credentials-needed", "rejected-not-fast-forward", "remote-unreachable", "redirected",
        "protocol-refused", "git-failed", "lfs-detected", "lfs-check-failed", "push-rejected", "partial-clone-unsupported",

        // The domain: the address of a remote and the name of a branch.
        RemoteRepository.RefusalUnrecognized, RemoteRepository.RefusalHost, RemoteRepository.RefusalCredentialsInUrl, RemoteRepository.RefusalPort, RemoteRepository.RefusalOwner,
        RemoteRepository.RefusalName, RemoteBranch.RefusalCharacters, RemoteBranch.RefusalForm,

        // The projections (WP4).
        BoardCardReasons.MergedWithUnjudgedChanges, RemoteTraceTitles.RevertedBy, RemoteTraceTitles.Deployed,
        .. Enum.GetValues<RemoteMergeMode>().Select(RemoteTraceTitles.MergeMode),
        .. Enum.GetValues<HostingOutcome>().Select(OutcomeCode),
    ];

    /// <summary>The code of the outcome of a call, in kebab: <c>hosting.outcome.rate-limited</c>.</summary>
    public static string OutcomeCode(HostingOutcome outcome)
    {
        var builder = new StringBuilder("hosting.outcome.");
        var name = outcome.ToString();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    /// <summary>The words of an outcome of the platform.</summary>
    public static string Outcome(HostingOutcome outcome) => Code(OutcomeCode(outcome));

    /// <summary>The words of a code; without a text of its own, a generic sentence that names the code (cleaned and bounded: it is data).</summary>
    public static string Code(string? code)
    {
        if (code is not null && UserText.IsCode(code) && Texts.Current.Has(UserText.KeyOf(code)))
        {
            return Texts.Get(UserText.KeyOf(code));
        }

        return Texts.Format("Hosting_Code_Unknown", Data(code) ?? Texts.Get("Hosting_Code_None"));
    }

    /// <summary>
    /// A text for the person from the layers below, painted in the language in use. A code with no text, or one whose arguments do not fit its sentence, is said generically
    /// with its code: the screen never shows nothing and never throws.
    /// </summary>
    public static string Say(UserText? text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        if (Texts.Current.Has(text.ResourceKey))
        {
            try
            {
                return Texts.Render(text);
            }
            catch (FormatException)
            {
                // The arguments do not match the placeholders: fall through to the generic sentence.
            }
        }

        return Texts.Format("Hosting_Code_Unknown", Data(text.Code) ?? Texts.Get("Hosting_Code_None"));
    }

    /// <summary>The title of a card of the board when its reason is one of Phase 10, or the reason as the work screens already say it.</summary>
    public static string Reason(string code) => Code(code);

    /// <summary>The words of the mode a pull request was merged with (the code of the chain, <c>remote.merge.squash</c>…).</summary>
    public static string MergeMode(RemoteMergeMode mode) => Code(RemoteTraceTitles.MergeMode(mode));

    /// <summary>The words of a method the person may ask for.</summary>
    public static string Method(MergeMethod method) => Texts.Get("Hosting_Method_" + method);

    /// <summary>
    /// A text of the remote (a title, the name of a check, an environment) as data: without control or format characters, the line breaks as spaces and no longer than
    /// <see cref="MaxDataLength"/>; null when nothing is left. It is painted in a text box as it is, never as a link or as formatting, and never inside a sentence of the system.
    /// </summary>
    public static string? Data(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxDataLength));
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value is '\n' or '\r' or '\t')
            {
                builder.Append(' ');
            }
            else if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                     or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }
            else
            {
                builder.Append(rune.ToString());
            }

            if (builder.Length >= MaxDataLength)
            {
                break;
            }
        }

        var clean = builder.ToString().Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>The short form of a commit: its first eight characters.</summary>
    public static string Short(string commit) => commit.Length <= 8 ? commit : commit[..8];

    /// <summary>A moment in local time, <c>dd/MM/yyyy HH:mm</c>.</summary>
    public static string When(DateTimeOffset at, TimeProvider time) =>
        TimeZoneInfo.ConvertTime(at, time.LocalTimeZone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A clock time in local time, <c>HH:mm</c>.</summary>
    public static string Clock(DateTimeOffset at, TimeProvider time) =>
        TimeZoneInfo.ConvertTime(at, time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>How long ago, in words: «Hace 3 min», «Hace 2 h», «Hace 4 d», or «Ahora».</summary>
    public static string Ago(DateTimeOffset at, TimeProvider time)
    {
        var span = time.GetUtcNow() - at;
        if (span < TimeSpan.FromMinutes(1))
        {
            return Texts.Get("Hosting_Ago_Now");
        }

        if (span < TimeSpan.FromHours(1))
        {
            return Texts.Format("Hosting_Ago_Minutes", (int)span.TotalMinutes);
        }

        return span < TimeSpan.FromDays(1)
            ? Texts.Format("Hosting_Ago_Hours", (int)span.TotalHours)
            : Texts.Format("Hosting_Ago_Days", (int)span.TotalDays);
    }
}
