using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Controls.Editor;

/// <summary>Opens files as <see cref="EditorDocument"/>s.</summary>
public interface IEditorDocumentFactory
{
    Task<EditorDocument> OpenAsync(string path, CancellationToken cancellationToken = default);
}

internal sealed class EditorDocumentFactory : IEditorDocumentFactory
{
    public Task<EditorDocument> OpenAsync(string path, CancellationToken cancellationToken = default) =>
        EditorDocument.OpenAsync(path, cancellationToken);
}

public static class EditorServiceCollectionExtensions
{
    /// <summary>Registers the code editor services (document factory and asset locator).</summary>
    public static IServiceCollection AddEditorServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(EditorAssets.Default);
        services.TryAddSingleton<IEditorDocumentFactory, EditorDocumentFactory>();
        return services;
    }
}
