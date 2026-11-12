using AgentStudio.Application.Documents;
using AgentStudio.Application.Localization;

namespace AgentStudio.Memory.Export;

/// <summary>
/// The renderer of a host that composed none (<see cref="IUserTextRenderer"/> is the desktop's, over its resources): the exporter of
/// the memory is still there, and it fails with a clear message only when it has a file to write, never with a Spanish sentence of
/// its own.
/// </summary>
internal sealed class MissingUserTextRenderer : IUserTextRenderer
{
    public string Render(UserText text, string language) =>
        throw new InvalidOperationException("The export of the memory needs an IUserTextRenderer to write the frame of its files; the host composed none.");
}
