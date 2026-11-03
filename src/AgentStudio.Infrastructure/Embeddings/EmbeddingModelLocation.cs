using AgentStudio.Application.Semantic;

namespace AgentStudio.Infrastructure.Embeddings;

/// <summary>
/// The folder of a pinned embedding model (docs/17 D-03 and D-04; ADR-0047): where <see cref="Onnx.OnnxEmbedderSource"/>
/// reads its files and <see cref="HttpEmbeddingModelInstaller"/> installs them. The app uses <see cref="ForCurrentUser"/>,
/// <c>%LOCALAPPDATA%\AgentStudio\models\multilingual-e5-small\&lt;revision&gt;</c>
/// (<see cref="SemanticIndexLocation.ModelDirectory"/>), outside every project; the tests use a folder of their own.
/// </summary>
/// <param name="ModelDirectory">The folder that holds the files of <paramref name="Model"/>, by their plain names.</param>
/// <param name="Model">The pinned model: its files, with their sizes and SHA-256, and the rule of its token ids.</param>
public sealed record EmbeddingModelLocation(string ModelDirectory, EmbeddingModelDescriptor Model)
{
    /// <summary>The folder of <paramref name="model"/> under <paramref name="localAppData"/> (the folder <c>%LOCALAPPDATA%</c> resolves to).</summary>
    public static EmbeddingModelLocation Under(string localAppData, EmbeddingModelDescriptor model) =>
        new(SemanticIndexLocation.ModelDirectory(localAppData, model), model);

    /// <summary><see cref="EmbeddingModels.MultilingualE5Small"/> in the <c>%LOCALAPPDATA%</c> of the user running the app.</summary>
    public static EmbeddingModelLocation ForCurrentUser() =>
        Under(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), EmbeddingModels.MultilingualE5Small);

    /// <summary>The files of the model, the small tokenizer first: what is checked, read and installed, in that order.</summary>
    public IReadOnlyList<EmbeddingModelFile> Files => [Model.Tokenizer, Model.Model];

    /// <summary>Where <paramref name="file"/> lives: its plain name in <see cref="ModelDirectory"/>.</summary>
    /// <exception cref="ArgumentException">The name of the file is not a plain file name.</exception>
    public string PathOf(EmbeddingModelFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.FileName)
            || file.FileName is "." or ".."
            || !string.Equals(Path.GetFileName(file.FileName), file.FileName, StringComparison.Ordinal)
            || file.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{file.FileName}' is not the plain name of a file of the model folder.", nameof(file));
        }

        return Path.Combine(ModelDirectory, file.FileName);
    }
}
