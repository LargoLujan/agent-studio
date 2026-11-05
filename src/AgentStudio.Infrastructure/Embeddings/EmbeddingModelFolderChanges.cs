using System.Collections.Concurrent;

namespace AgentStudio.Infrastructure.Embeddings;

/// <summary>
/// How many installs each model folder has had in this run of the app, for the whole process (review of WP2, point 6):
/// <see cref="HttpEmbeddingModelInstaller"/> counts one at the end of every install, and the checks
/// (<see cref="EmbeddingModelChecks"/>) and the failed openings (<see cref="Onnx.OnnxEmbedderSource"/>) remembered before
/// it are forgotten after it. So a file the user restores with the size and the time of the one before is seen as soon as
/// the model is installed again (the installer then verifies it and finds it in place), without restarting the app.
/// Safe to use from several threads.
/// </summary>
internal static class EmbeddingModelFolderChanges
{
    private static readonly ConcurrentDictionary<string, long> Installs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The installs <paramref name="folder"/> has had so far in this run.</summary>
    public static long Version(string folder) => Installs.TryGetValue(Key(folder), out var version) ? version : 0;

    /// <summary>Counts one more install of <paramref name="folder"/>.</summary>
    public static void Changed(string folder) => Installs.AddOrUpdate(Key(folder), 1, static (_, version) => version + 1);

    private static string Key(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
