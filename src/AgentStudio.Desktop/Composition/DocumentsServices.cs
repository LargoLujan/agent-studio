using AgentStudio.Application.Documents;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Documents;
using AgentStudio.Desktop.Services;
using AgentStudio.Infrastructure.Documents.Docx;
using AgentStudio.Memory;
using AgentStudio.Memory.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

/// <summary>The composition of the documentation for clients (docs/17 D-58 to D-60; ADR-0057).</summary>
internal static class DocumentsServices
{
    /// <summary>
    /// Registers the documentation for clients: the sentences of the system painted from the resources of the desktop, the builder, the
    /// writers of Markdown and HTML (Memory), of Word (Infrastructure) and of PDF (WebView2, over the same HTML), the exporter and the
    /// «Documentación para clientes» document of the shell. The ports it stands on (the project memory, the stores, the redactor and the
    /// activity of the runs) are the ones the rest of the composition registers.
    /// </summary>
    internal static IServiceCollection AddClientDocumentsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IUserTextRenderer, ResourceUserTextRenderer>();
        services.AddAgentStudioClientDocuments();
        services.AddSingleton<IClientDocumentFormatWriter>(provider => new WordDocumentWriter(
            provider.GetRequiredService<IUserTextRenderer>(),
            provider.GetRequiredService<ISecretRedactor>()));
        services.TryAddSingleton<IHtmlPdfPrinter, WebView2PdfPrinter>();
        services.AddSingleton<IClientDocumentFormatWriter>(provider => new PdfDocumentWriter(
            new HtmlDocumentWriter(provider.GetRequiredService<IUserTextRenderer>(), provider.GetRequiredService<ISecretRedactor>()),
            provider.GetRequiredService<IHtmlPdfPrinter>()));
        services.AddSingleton(provider => new ClientDocumentsViewModel(
            provider.GetService<IProjectWorkspace>(),
            provider.GetService<IClientDocumentBuilder>(),
            provider.GetService<IClientDocumentExporter>(),
            provider.GetServices<IClientDocumentFormatWriter>(),
            provider.GetService<IFolderPicker>()));
        services.AddSingleton<IMilestoneCloseListener>(provider => new ClientDocumentsMilestoneListener(provider.GetRequiredService<ClientDocumentsViewModel>()));
        return services;
    }
}
