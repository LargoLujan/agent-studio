using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Verification;

/// <summary>Whether a project has a verification profile, and whether it can be used.</summary>
public enum VerificationProfileStatus
{
    /// <summary>The file exists and follows every rule.</summary>
    Loaded,

    /// <summary>The project has no profile yet. Nothing is assumed: a default is only ever a proposal the user saves.</summary>
    NotFound,

    /// <summary>The file exists but cannot be used; <see cref="VerificationProfileLoad.Problems"/> says why.</summary>
    Invalid,
}

/// <summary>The outcome of reading a profile file.</summary>
/// <param name="Path">The file that was read.</param>
/// <param name="Profile">The profile when <paramref name="Status"/> is <see cref="VerificationProfileStatus.Loaded"/>; null otherwise.</param>
/// <param name="Problems">Why the file cannot be used, when <paramref name="Status"/> is <see cref="VerificationProfileStatus.Invalid"/>.</param>
public sealed record VerificationProfileLoad(VerificationProfileStatus Status, string Path, VerificationProfile? Profile, IReadOnlyList<string> Problems);

/// <summary>
/// The trusted verification profile of a project, <c>&lt;root&gt;\.agentstudio\verification.json</c> (docs/13 section 5,
/// ADR-0016). It is read from the project root only, never from a worktree; the user edits it, and the engine freezes
/// the loaded profile into the run when the run starts. The JSON is strict: unknown properties, comments, trailing
/// commas, numbers for enums and missing or null required values are errors, and so is every broken
/// <see cref="VerificationRules"/> rule. Nothing is ever filled in, and a project without the file has no profile: a
/// default such as <see cref="DotNetVerificationProfile"/> only becomes the profile when it is saved.
/// </summary>
/// <remarks>
/// Version 2 of the format, the one <see cref="Serialize"/> writes:
/// <code>
/// { "version": 2, "name": "dotnet", "steps": [
///   { "id": "build", "title": "Compilar", "executable": "dotnet", "arguments": ["build"],
///     "kind": "BuildLog", "report": "None", "reportPath": null, "timeoutSeconds": 600 },
///   { "id": "test", ..., "requires": ["build"] } ] }
/// </code>
/// The file must be kept out of git (<c>.git/info/exclude</c>): it decides what Agent Studio runs on the machine.
/// A step may also say <c>"isolation": "none"</c> (any case; <c>"LowIntegrity"</c> is the default and what a step
/// without the field gets): it then runs with the rights of the user in the working directory instead of at Low
/// integrity on a copy of the commit (ADR-0017). Any other value is an error, never a silent "none".
/// <para>
/// A step may say <c>"requires"</c>: the ids of the steps above it that run first whenever it runs
/// (<see cref="VerificationStep.Requires"/>). Without the field, or with an empty list, it requires nothing; a null is an
/// error, as for every other field.
/// </para>
/// <para>
/// <b>Version 1</b>, the format before <c>requires</c>, is still read, and a <c>requires</c> in it (null too) is an error. A version 1
/// file got one rule instead, applied when it is read and nowhere else (<see cref="LegacyRequirements"/>): a step of
/// <c>dotnet</c> with <c>--no-build</c> before any <c>--</c> requires the step <c>build</c> when the profile has one above it
/// with the same isolation. That is what such a profile always meant (a test of the binaries the build left), and what a
/// fresh copy of the commit needs to have any binaries at all. The step shows the requirement wherever its line is read
/// (<see cref="VerificationStepText.Line"/>), and saving the profile writes it as version 2, with the requirement written out.
/// </para>
/// </remarks>
public static class VerificationProfileFile
{
    /// <summary>A larger file is refused unread.</summary>
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>The version <see cref="Serialize"/> writes: 2, with <c>requires</c>.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The version before <c>requires</c>, still read (<see cref="LegacyRequirements"/>).</summary>
    public const int LegacyVersion = 1;

    /// <summary>The step a version 1 step with <c>--no-build</c> requires (<see cref="LegacyRequirements"/>).</summary>
    public const string LegacyBuildStepId = "build";

    /// <summary>The name of the file in the project's <c>.agentstudio</c> folder.</summary>
    public const string FileName = "verification.json";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    /// <summary>Where the profile of the project at <paramref name="projectRoot"/> lives.</summary>
    public static string PathFor(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Path.Combine(Path.GetFullPath(projectRoot), ".agentstudio", FileName);
    }

    /// <summary>Reads the profile of a project. A missing file is <see cref="VerificationProfileStatus.NotFound"/>, never a default.</summary>
    public static async Task<VerificationProfileLoad> LoadAsync(string projectRoot, CancellationToken cancellationToken)
    {
        var path = PathFor(projectRoot);
        if (!File.Exists(path))
        {
            return new VerificationProfileLoad(VerificationProfileStatus.NotFound, path, null, []);
        }

        byte[] content;
        int length;
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                // One byte more than the limit tells a file that is too large from one that is exactly at it.
                content = new byte[MaxFileBytes + 1];
                length = 0;
                int read;
                while (length < content.Length && (read = await stream.ReadAsync(content.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    length += read;
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new VerificationProfileLoad(VerificationProfileStatus.NotFound, path, null, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid(path, $"The profile file cannot be read: {ex.Message}");
        }

        if (length > MaxFileBytes)
        {
            return Invalid(path, $"The profile file is larger than {MaxFileBytes / 1024} KB; it is not read.");
        }

        string json;
        try
        {
            // A byte order mark is accepted (editors add it); StrictUtf8 has no preamble of its own to compare with.
            ReadOnlySpan<byte> byteOrderMark = [0xEF, 0xBB, 0xBF];
            var span = content.AsSpan(0, length);
            json = StrictUtf8.GetString(span.StartsWith(byteOrderMark) ? span[byteOrderMark.Length..] : span);
        }
        catch (DecoderFallbackException)
        {
            return Invalid(path, "The profile file is not UTF-8 text.");
        }

        return Parse(json, path);
    }

    /// <summary>Reads a profile from its JSON text. <paramref name="path"/> only labels the result.</summary>
    public static VerificationProfileLoad Parse(string json, string path)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(path);

        ProfileDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ProfileDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Invalid(path, $"The file is not a valid verification profile: {ex.Message}");
        }

        if (document is null)
        {
            return Invalid(path, "The file is not a valid verification profile: it holds no object.");
        }

        if (document.Version is not (LegacyVersion or CurrentVersion))
        {
            return Invalid(path, $"The profile has version {document.Version}; this Agent Studio reads versions {LegacyVersion} and {CurrentVersion}.");
        }

        // Read from the text itself: a null reads as no field, and nothing is ever filled in. A version 1 file has no
        // "requires" at all, null included; in version 2 it is a list, left out to require nothing, never a null.
        var written = RequiresWritten(json);
        if (document.Version == LegacyVersion && written.Count > 0)
        {
            return Invalid(path, $"The profile has version {LegacyVersion}, which has no \"requires\" (step {string.Join(", ", written.Select(w => $"'{w.Step}'"))}): write version {CurrentVersion} to use it.");
        }

        if (written.Where(w => w.IsNull).Select(w => w.Step).ToList() is { Count: > 0 } nulls)
        {
            return Invalid(path, $"Step {string.Join(", ", nulls.Select(n => $"'{n}'"))}: \"requires\" is null; it is a list of step ids, and a step that requires nothing leaves it out.");
        }

        var steps = document.Steps.Select(s => s is null
                ? null!
                : new VerificationStep(s.Id, s.Title, s.Executable, s.Arguments, s.Kind, s.Report, s.ReportPath, TimeSpan.FromSeconds(s.TimeoutSeconds), s.Isolation, s.Requires))
            .ToArray();
        if (document.Version == LegacyVersion)
        {
            steps = [.. LegacyRequirements(steps)];
        }

        var profile = new VerificationProfile(document.Name, steps);
        var problems = VerificationRules.Check(profile);
        return problems.Count > 0
            ? new VerificationProfileLoad(VerificationProfileStatus.Invalid, path, null, problems)
            : new VerificationProfileLoad(VerificationProfileStatus.Loaded, path, profile, []);
    }

    /// <summary>The JSON text of a valid profile, as <see cref="SaveAsync"/> writes it.</summary>
    /// <exception cref="ArgumentException">The profile breaks a rule, or a timeout is not a whole number of seconds.</exception>
    public static string Serialize(VerificationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var steps = profile.Steps ?? [];
        var problems = new List<string>(VerificationRules.Check(profile));
        problems.AddRange(steps
            .Where(s => s is not null && s.Timeout.Ticks % TimeSpan.TicksPerSecond != 0)
            .Select(s => $"Step '{s.Id}': the file keeps timeouts in whole seconds, and {s.Timeout} is not."));
        if (problems.Count > 0)
        {
            throw new ArgumentException("The verification profile is not valid: " + string.Join(" ", problems), nameof(profile));
        }

        var document = new ProfileDocument(
            CurrentVersion,
            profile.Name,
            steps.Select(s => new StepDocument(
                s.Id, s.Title, s.Executable, s.Arguments, s.Kind, s.Report, s.ReportPath, (int)(s.Timeout.Ticks / TimeSpan.TicksPerSecond), s.Isolation,
                s.Requires is { Count: > 0 } requires ? requires : null))
            .ToArray());
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    /// <summary>
    /// The one rule a version 1 file gets, applied when it is read (never to a version 2 file, a profile built in code or a
    /// profile frozen in a run): a step whose executable is <c>dotnet</c> and whose arguments hold <c>--no-build</c> before
    /// any <c>--</c> (after it they are the test application's) requires the step <see cref="LegacyBuildStepId"/> when the
    /// profile has one above it with the same isolation. Every other step is kept as it is. A <c>--no-build</c> without such
    /// a build is left alone: it fails as it always did, and the user sees why in its evidence.
    /// </summary>
    /// <remarks>
    /// A version 1 profile had no way to say it, but its <c>dotnet test --no-build</c> always meant the binaries of its
    /// <c>dotnet build</c>; in an isolated copy of the commit nothing else is there, and a plan that names only the test
    /// step got «No test projects were found» on every attempt. The requirement makes the profile hash otherwise than the
    /// file did before (<see cref="VerificationProfile.ContentHash"/>), which only reaches runs started from now on: a run
    /// keeps the profile frozen when it started, and resumes with it.
    /// </remarks>
    public static IReadOnlyList<VerificationStep> LegacyRequirements(IReadOnlyList<VerificationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var result = new VerificationStep[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            result[i] = step;
            if (step is null || step.Requires is { Count: > 0 } || !BuildsNothing(step))
            {
                continue;
            }

            var build = steps.Take(i).FirstOrDefault(s => s is not null && string.Equals(s.Id, LegacyBuildStepId, StringComparison.OrdinalIgnoreCase));
            if (build is not null && build.Isolation == step.Isolation)
            {
                result[i] = step with { Requires = [build.Id] };
            }
        }

        return result;
    }

    /// <summary>The steps of the file that write <c>"requires"</c>, by id (<c>?</c> without one), and whether its value is null.</summary>
    private static List<(string Step, bool IsNull)> RequiresWritten(string json)
    {
        var found = new List<(string, bool)>();
        using var text = JsonDocument.Parse(json);
        if (text.RootElement.ValueKind != JsonValueKind.Object
            || !text.RootElement.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
        {
            return found;
        }

        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind == JsonValueKind.Object && step.TryGetProperty("requires", out var requires))
            {
                var id = step.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "?" : "?";
                found.Add((id, requires.ValueKind == JsonValueKind.Null));
            }
        }

        return found;
    }

    /// <summary>A <c>dotnet</c> command told not to build: <c>--no-build</c> among its own arguments, before any <c>--</c>.</summary>
    private static bool BuildsNothing(VerificationStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Executable) || step.Arguments is null
            || !string.Equals(Path.GetFileNameWithoutExtension(step.Executable.Trim()), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var argument in step.Arguments)
        {
            if (argument == "--")
            {
                return false;
            }

            if (argument == "--no-build")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the profile of a project, replacing the previous one in a single move. An invalid profile throws before
    /// anything is written. This is the only way a proposal becomes the profile. The file is written through
    /// <paramref name="files"/>, the policy of the project's storage: a link left at <c>.agentstudio</c>, at its
    /// temporary folder or at the file is refused, with nothing written through it (ADR-0010, addenda of 2026-10-18).
    /// </summary>
    public static Task SaveAsync(string projectRoot, VerificationProfile profile, IProjectStorageFiles files, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(files);
        var json = Serialize(profile);
        cancellationToken.ThrowIfCancellationRequested();
        files.Replace(projectRoot, FileName, StrictUtf8.GetBytes(json));
        return Task.CompletedTask;
    }

    private static VerificationProfileLoad Invalid(string path, string problem) =>
        new(VerificationProfileStatus.Invalid, path, null, [problem]);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            WriteIndented = true,

            // The file is local and edited by people: keep accents readable instead of \u escapes.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    // The shape of the file. Every constructor parameter is required, and only the report path may be null, except the
    // isolation and the requirements: a file written before them has none, a step without the isolation is isolated
    // (ADR-0017), and a step without requirements requires nothing. The requirements are only written when there are.
    private sealed record ProfileDocument(int Version, string Name, IReadOnlyList<StepDocument> Steps);

    private sealed record StepDocument(
        string Id,
        string Title,
        string Executable,
        IReadOnlyList<string> Arguments,
        EvidenceKind Kind,
        TestReportFormat Report,
        string? ReportPath,
        int TimeoutSeconds,
        ProcessIsolation Isolation = ProcessIsolation.LowIntegrity,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Requires = null);
}
