using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Setup;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Work;

/// <summary>
/// The budget of a project (docs/16 D-05, ADR-0040), kept in the folder of the app of this computer,
/// <c>&lt;folder&gt;\&lt;id of the project&gt;.json</c> (by default <c>%LOCALAPPDATA%\AgentStudio\budgets</c>): like the options of the
/// project it is the person's, so nothing of it goes into the repository. The gate of the engine (<c>ProjectBudgetGate</c>) reads it
/// through <see cref="IProjectBudgetStore"/> before it launches an attempt.
/// </summary>
/// <remarks>
/// Unlike the options of the project, a file that cannot be read is <b>not</b> «no budget»: it throws
/// <see cref="InvalidDataException"/>, so the gate fails closed and says <c>project.budget-unreadable</c> instead of letting every
/// attempt through. Only a missing file is no budget.
/// </remarks>
public sealed class LocalProjectBudgetStore : IProjectBudgetStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _folder;

    public LocalProjectBudgetStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
    }

    /// <summary>The folder where the budget of each project lives, one file per project.</summary>
    public string Folder => _folder;

    /// <summary>The folder of the app of this computer where the budgets live by default: <c>%LOCALAPPDATA%\AgentStudio\budgets</c>.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "budgets");

    public async Task<WorkBudget?> LoadAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        // An id that is not safe to name a file never had a budget saved (Save refuses it): not an error to ask.
        if (!LocalProjectOptionsStore.IsSafeId(project.ProjectId))
        {
            return null;
        }

        var path = PathOf(project);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<BudgetDocument>(await File.ReadAllTextAsync(path, Utf8, cancellationToken).ConfigureAwait(false), Json)
                ?? throw new InvalidDataException("The budget file is empty.");
            var budget = new WorkBudget(document.MaxTokens, document.MaxAgentRuns, document.Period);
            var problems = budget.Validate();
            return problems.Count == 0 && document.Version == 1
                ? budget
                : throw new InvalidDataException($"The budget file does not hold a valid budget: version {document.Version} {string.Join(", ", problems)}.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Removed between the check and the read (the person took the budget off): the project has no budget.
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Only the name of the file: the message reaches the screen, the alert of the engine and the log, and the full path carries the user name.
            var name = Path.GetFileName(path);
            throw new InvalidDataException($"The budget of the project cannot be read from '{name}': {ex.Message.Replace(path, name, StringComparison.OrdinalIgnoreCase).Replace(_folder, string.Empty, StringComparison.OrdinalIgnoreCase)}", ex);
        }
    }

    public async Task SaveAsync(ProjectContext project, WorkBudget? budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!LocalProjectOptionsStore.IsSafeId(project.ProjectId))
        {
            throw new ArgumentException("The id of the project is not safe to name a file.", nameof(project));
        }

        var path = PathOf(project);
        if (budget is null)
        {
            File.Delete(path);
            return;
        }

        budget.EnsureValid();
        Directory.CreateDirectory(_folder);
        var json = JsonSerializer.Serialize(new BudgetDocument(1, budget.MaxTokens, budget.MaxAgentRuns, budget.Period), Json);

        // Written whole beside the file and moved over it, so a reader never sees half of it.
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, Utf8, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private string PathOf(ProjectContext project) => Path.Combine(_folder, project.ProjectId + ".json");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done for it: the original error is the one that matters.
        }
    }

    private sealed record BudgetDocument(int Version, long? MaxTokens, int? MaxAgentRuns, BudgetPeriod Period);
}
