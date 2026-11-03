using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Providers.Skills;

/// <summary>
/// Materializes the activated skills for each CLI without touching the user's CLI configuration:
/// Claude Code gets a session plugin (<c>&lt;base&gt;/claude-plugin-&lt;runId&gt;/</c>, passed with <c>--plugin-dir</c>);
/// Codex and Gemini get <c>.agents/skills/&lt;name&gt;/</c> inside the run's working directory, which is added to
/// that repository's <c>.git/info/exclude</c> (never to a <c>.gitignore</c> or a global file).
/// Symbolic links and junctions are never followed.
/// <para>
/// Every skill file of a copy (<c>SKILL.md</c> in any letter case, at any depth) is written with
/// <see cref="SkillFrontmatter.ForSession"/>: a header rebuilt from the descriptive keys only, then its body. A skill never
/// grants tools, hooks or another way to run beyond the agent's profile, whatever its source declares (ADR-0018,
/// «Medición»). The source files are only read.
/// </para>
/// </summary>
public sealed partial class SkillMaterializer : ISkillMaterializer
{
    /// <summary>Marker written in everything this class creates; <see cref="Cleanup"/> only removes marked folders.</summary>
    internal const string MarkerFile = ".agent-studio-managed";

    private const string PluginName = "agent-studio-session";
    private const string ExcludeLine = ".agents/";

    private static readonly string[] ProtectedProfileFolders = [".claude", ".codex", ".gemini", ".agents"];

    private readonly string _userProfile;

    public SkillMaterializer()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <param name="userProfile">User profile folder whose CLI configuration folders must never be written.</param>
    public SkillMaterializer(string userProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        _userProfile = Path.GetFullPath(userProfile);
    }

    [GeneratedRegex(@"\A[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex ValidSkillName();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\z")]
    private static partial Regex ValidRunId();

    public Task<string?> MaterializeAsync(
        ProviderId cli,
        IReadOnlyList<ActivatedSkill> activatedSkills,
        SkillSessionContext session,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activatedSkills);
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();

        if (activatedSkills.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        // Validate everything before writing anything.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activated in activatedSkills)
        {
            var name = activated.Skill.Id.Value;
            if (!ValidSkillName().IsMatch(name))
            {
                throw new ArgumentException($"Invalid skill name '{name}': only [a-z0-9-] without separators or '..'.", nameof(activatedSkills));
            }

            if (!names.Add(name))
            {
                throw new ArgumentException($"Duplicate skill '{name}'.", nameof(activatedSkills));
            }

            if (!Directory.Exists(activated.LocalPath) || IsLink(new DirectoryInfo(activated.LocalPath)))
            {
                throw new DirectoryNotFoundException($"Skill folder not found or a link: '{activated.LocalPath}'.");
            }

            // The source must come from the skills catalog, never from a CLI configuration folder (credentials live there).
            EnsureOutsideUserCliFolders(activated.LocalPath);

            if (!File.Exists(Path.Combine(activated.LocalPath, "SKILL.md")))
            {
                throw new FileNotFoundException($"Skill '{name}' has no SKILL.md.", Path.Combine(activated.LocalPath, "SKILL.md"));
            }
        }

        string? result;
        if (cli == ProviderId.ClaudeCode)
        {
            result = MaterializeClaude(activatedSkills, session, ct);
        }
        else if (cli == ProviderId.CodexCli || cli == ProviderId.GeminiCli)
        {
            result = MaterializeAgentsFolder(activatedSkills, session, ct);
        }
        else
        {
            throw new ArgumentException($"Unsupported provider '{cli}'.", nameof(cli));
        }

        return Task.FromResult<string?>(result);
    }

    public void Cleanup(string materializedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materializedDirectory);
        PathIdentity.RequireCanonical(materializedDirectory);
        var dir = Path.GetFullPath(materializedDirectory);
        if (!Directory.Exists(dir))
        {
            return;
        }

        // Validate again: never delete through a link nor inside a CLI configuration folder, marker or not.
        EnsureOutsideUserCliFolders(dir);
        if (IsLink(new DirectoryInfo(dir)))
        {
            return;
        }

        if (HasOurMarker(dir))
        {
            // Claude session plugin: the whole folder is ours.
            DeleteTree(dir);
            return;
        }

        // .agents/skills folder: remove only the skill folders we created (staging leftovers or skill-named folders).
        foreach (var child in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(child);
            var plausible = name.StartsWith(StagingPrefix, StringComparison.Ordinal) || ValidSkillName().IsMatch(name);
            if (plausible && !IsLink(new DirectoryInfo(child)) && HasOurMarker(child))
            {
                DeleteTree(child);
            }
        }

        // Tidy up the (now empty) folders we may have created.
        RemoveIfEmpty(dir);
        if (string.Equals(Path.GetFileName(dir), "skills", StringComparison.Ordinal) && Directory.GetParent(dir) is { } agents
            && string.Equals(agents.Name, ".agents", StringComparison.Ordinal))
        {
            RemoveIfEmpty(agents.FullName);
        }
    }

    private string MaterializeClaude(IReadOnlyList<ActivatedSkill> skills, SkillSessionContext session, CancellationToken ct)
    {
        if (!ValidRunId().IsMatch(session.RunId ?? string.Empty))
        {
            throw new ArgumentException($"Invalid run id '{session.RunId}'.", nameof(session));
        }

        PathIdentity.RequireCanonical(session.SessionBaseDirectory);
        var root = Path.GetFullPath(Path.Combine(session.SessionBaseDirectory, "claude-plugin-" + session.RunId));
        EnsureOutsideUserCliFolders(root);
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException($"'{root}' already exists; it is not overwritten.");
        }

        // Ownership: the marker is created atomically (CreateNew). If another process won the race the folder is not
        // ours and nothing is deleted. Only a folder this call owns is rolled back.
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, MarkerFile);
        try
        {
            WriteNewMarker(marker);
        }
        catch (IOException)
        {
            throw new IOException($"'{root}' was created concurrently by another process; it is not touched.");
        }

        if (Directory.EnumerateFileSystemEntries(root).Any(e => !string.Equals(e, marker, StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(marker);
            throw new IOException($"'{root}' is not empty; it is not touched.");
        }

        try
        {
            WriteClaudePlugin(root, skills, ct);
        }
        catch
        {
            // This call owns the marker, so the partial plugin folder is ours and is removed entirely.
            TryDeleteTree(root);
            throw;
        }

        return root;
    }

    private const string MarkerContent = "Created by Agent Studio.\n";
    private const string StagingPrefix = ".staging-";

    /// <summary>The file a CLI reads as a skill, in any letter case (Claude Code 2.1.290 matches it case-insensitively).</summary>
    private const string SkillFile = "SKILL.md";

    private static void WriteNewMarker(string path) => WriteNewText(path, MarkerContent);

    /// <summary>Writes a file that must not exist yet (UTF-8 without BOM): an existing file is never overwritten.</summary>
    private static void WriteNewText(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Utf8NoBom.GetBytes(content));
    }

    /// <summary>
    /// True when <paramref name="directory"/> holds our marker: a regular file (not a link) with exactly the content we
    /// write. Marker-based ownership is a best-effort convention against deleting foreign folders, NOT proof of
    /// ownership: anyone who can write the folder can forge it.
    /// </summary>
    private static bool HasOurMarker(string directory)
    {
        try
        {
            var marker = new FileInfo(Path.Combine(directory, MarkerFile));
            return marker.Exists
                && !marker.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && !marker.Attributes.HasFlag(FileAttributes.Directory)
                && marker.LinkTarget is null
                && marker.Length == Utf8NoBom.GetByteCount(MarkerContent)
                && File.ReadAllText(marker.FullName) == MarkerContent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteClaudePlugin(string root, IReadOnlyList<ActivatedSkill> skills, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        var manifest = JsonSerializer.Serialize(new
        {
            name = PluginName,
            description = "Skills activated by Agent Studio for this session.",
            version = "1.0.0",
        });
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"), manifest, Utf8NoBom);

        var skillsRoot = Path.Combine(root, "skills");
        Directory.CreateDirectory(skillsRoot);
        foreach (var activated in skills)
        {
            ct.ThrowIfCancellationRequested();
            CopyTree(new DirectoryInfo(activated.LocalPath), Path.Combine(skillsRoot, activated.Skill.Id.Value), ct);
        }
    }

    private string MaterializeAgentsFolder(IReadOnlyList<ActivatedSkill> skills, SkillSessionContext session, CancellationToken ct)
    {
        PathIdentity.RequireCanonical(session.WorkingDirectory);
        var workingDirectory = Path.GetFullPath(session.WorkingDirectory);
        var skillsRoot = Path.Combine(workingDirectory, ".agents", "skills");
        EnsureOutsideUserCliFolders(skillsRoot);

        // Nothing below the working directory may be a link: a junction here could redirect the writes anywhere.
        EnsureNotLink(Path.Combine(workingDirectory, ".agents"));
        EnsureNotLink(skillsRoot);

        foreach (var activated in skills)
        {
            var target = Path.Combine(skillsRoot, activated.Skill.Id.Value);
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new IOException($"'{target}' already exists; it is not overwritten.");
            }
        }

        var agentsFolder = Path.Combine(workingDirectory, ".agents");
        var agentsFolderExisted = Directory.Exists(agentsFolder);
        var skillsRootExisted = Directory.Exists(skillsRoot);
        var created = new List<string>();
        try
        {
            Directory.CreateDirectory(skillsRoot);
            foreach (var activated in skills)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(skillsRoot, activated.Skill.Id.Value);

                // Copy into a temporary sibling and rename: a failed or cancelled copy never leaves a half-made skill folder.
                var staging = Path.Combine(skillsRoot, StagingPrefix + Guid.NewGuid().ToString("N"));
                var stagingOwned = false;
                try
                {
                    // Marker FIRST: if deleting a failed staging folder ever fails, Cleanup still recognises it as ours.
                    Directory.CreateDirectory(staging);
                    WriteNewMarker(Path.Combine(staging, MarkerFile));
                    stagingOwned = true;
                    CopyTree(new DirectoryInfo(activated.LocalPath), staging, ct);
                    Directory.Move(staging, target);
                }
                catch
                {
                    // Roll back only a staging folder whose marker this call created.
                    if (stagingOwned)
                    {
                        TryDeleteTree(staging);
                    }

                    throw;
                }

                created.Add(target);
            }
        }
        catch
        {
            foreach (var target in created)
            {
                TryDeleteTree(target);
            }

            if (!skillsRootExisted)
            {
                TryRemoveIfEmpty(skillsRoot);
            }

            if (!agentsFolderExisted)
            {
                TryRemoveIfEmpty(agentsFolder);
            }

            throw;
        }

        ExcludeFromGit(workingDirectory);
        return skillsRoot;
    }

    private static UTF8Encoding Utf8NoBom { get; } = new(false);

    private void EnsureOutsideUserCliFolders(string path)
    {
        PathIdentity.RequireCanonical(path);
        var full = Path.GetFullPath(path);
        PathIdentity.RequireCanonical(full);
        var physical = PathIdentity.FinalPath(full);

        // A junction or symlink (e.g. <workdir>/.agents -> ~/.agents) must be resolved before comparing.
        var resolved = ResolveLinks(full);
        var profile = ResolveLinks(_userProfile);
        foreach (var folder in ProtectedProfileFolders)
        {
            (string Candidate, string Root)[] checks =
            [
                (full, Path.Combine(_userProfile, folder)),
                (resolved, Path.Combine(profile, folder)),
                (resolved, Path.Combine(_userProfile, folder)),
            ];
            if (physical is not null && PathIdentity.FinalPath(Path.Combine(_userProfile, folder)) is { } protectedPhysical)
            {
                // Physical identity: resolves 8.3 names, extended prefixes, localhost shares and any link on the way.
                checks = [.. checks, (physical, protectedPhysical)];
            }

            foreach (var (candidate, root) in checks)
            {
                if (IsUnder(candidate, root))
                {
                    throw new InvalidOperationException($"Refusing to write under the user's CLI configuration folder '{root}'.");
                }
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="fullPath"/> with every existing directory link (junction or symlink) along it replaced by
    /// its final target; the part that does not exist yet is appended unchanged.
    /// </summary>
    private static string ResolveLinks(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var current = root;
        var segments = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                continue;
            }

            try
            {
                var info = new DirectoryInfo(current);
                if (IsLink(info) && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Path.GetFullPath(target.FullName);
                }
            }
            catch (IOException)
            {
                // An unreadable link is kept as is; the caller still compares the unresolved path.
            }
        }

        return current;
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        // Only ".." itself or "..\" / "../" escape the root: a folder literally named "..cache" is inside it.
        var escapes = relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        return relative == "." || (!escapes && !Path.IsPathRooted(relative));
    }

    /// <summary>Refuses a path that exists and is a link (junction/symlink).</summary>
    private static void EnsureNotLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.Exists && IsLink(info))
        {
            throw new InvalidOperationException($"Refusing to use '{path}': it is a link (junction or symbolic link).");
        }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;

    private static void CopyTree(DirectoryInfo source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(target);
        foreach (var file in source.EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            // A source file named like our marker must not collide with the one we put in the staging folder.
            if (IsLink(file) || string.Equals(file.Name, MarkerFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(file.Name, SkillFile, StringComparison.OrdinalIgnoreCase))
            {
                // Any skill file, at any depth: the CLI reads it as a skill, so it gets a header that grants nothing.
                WriteNewText(Path.Combine(target, file.Name), SkillFrontmatter.ForSession(File.ReadAllText(file.FullName)));
                continue;
            }

            file.CopyTo(Path.Combine(target, file.Name), overwrite: false);
        }

        foreach (var dir in source.EnumerateDirectories())
        {
            if (IsLink(dir))
            {
                continue;
            }

            CopyTree(dir, Path.Combine(target, dir.Name), ct);
        }
    }

    private static void DeleteTree(string dir)
    {
        // Links inside are removed as links; a directory link is never traversed.
        foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
        {
            if (IsLink(sub))
            {
                sub.Delete();
            }
            else
            {
                DeleteTree(sub.FullName);
            }
        }

        foreach (var file in new DirectoryInfo(dir).EnumerateFiles())
        {
            file.Attributes = FileAttributes.Normal;
            file.Delete();
        }

        Directory.Delete(dir);
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                DeleteTree(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the original failure is the one worth reporting.
        }
    }

    private static void TryRemoveIfEmpty(string dir)
    {
        try
        {
            RemoveIfEmpty(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static void RemoveIfEmpty(string dir)
    {
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
        }
    }

    /// <summary>Adds <c>.agents/</c> to the repository's <c>info/exclude</c> when the folder is a git repo or worktree.</summary>
    private void ExcludeFromGit(string workingDirectory)
    {
        // The exclude entry is a convenience: anything suspicious skips it instead of failing the run.
        try
        {
            ExcludeFromGitCore(workingDirectory);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Skill materializer: .git/info/exclude not updated: {ex.Message}");
        }
    }

    private void ExcludeFromGitCore(string workingDirectory)
    {
        var gitDir = ResolveCommonGitDir(workingDirectory);
        if (gitDir is null)
        {
            return;
        }

        var infoDir = Path.Combine(gitDir, "info");
        var exclude = Path.Combine(infoDir, "exclude");
        EnsureOutsideUserCliFolders(infoDir);
        EnsureNotLink(infoDir);
        EnsureNotLink(exclude);
        Directory.CreateDirectory(infoDir);

        // Same handle for the link-count check and the write; a multiply-linked exclude is never modified.
        if (!PathIdentity.TryAppendLineOnce(exclude, ExcludeLine))
        {
            System.Diagnostics.Debug.WriteLine("Skill materializer: .git/info/exclude has several hard links; it was not modified.");
        }
    }

    // <wd>/.git is a directory (normal repo) or a file "gitdir: <path>" (worktree; its info/exclude lives in the common dir).
    private string? ResolveCommonGitDir(string workingDirectory)
    {
        var dotGit = Path.Combine(workingDirectory, ".git");
        EnsureNotLink(dotGit);
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var rawGitDir = line["gitdir:".Length..].Trim();
        PathIdentity.RequireCanonical(rawGitDir);
        var gitDir = Path.GetFullPath(Path.Combine(workingDirectory, rawGitDir));
        EnsureOutsideUserCliFolders(gitDir);
        var commonFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonFile))
        {
            EnsureNotLink(commonFile);
            var common = File.ReadAllText(commonFile).Trim();
            PathIdentity.RequireCanonical(common);
            gitDir = Path.GetFullPath(Path.Combine(gitDir, common));
            EnsureOutsideUserCliFolders(gitDir);
        }

        return Directory.Exists(gitDir) ? gitDir : null;
    }
}
