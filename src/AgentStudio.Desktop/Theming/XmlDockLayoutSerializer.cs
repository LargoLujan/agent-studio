using System.IO;
using System.Xml;
using System.Xml.Serialization;
using AvalonDock.Core;
using AvalonDock.Core.Serialization.Dto;

namespace AgentStudio.Desktop.Theming;

/// <summary>
/// AvalonDock 5 ships the layout DTOs and the serializer lifecycle but no concrete format; this one writes them as XML.
/// Panels still present in the running layout get their content back by ContentId; stored tabs without content are dropped.
/// </summary>
public sealed class XmlDockLayoutSerializer(IDockingManager manager) : LayoutSerializerBase(manager)
{
    private static readonly XmlSerializer Serializer = new(typeof(LayoutRootDto));

    protected override void SerializeCore(Stream stream, LayoutRootDto dto) => Serializer.Serialize(stream, dto);

    protected override LayoutRootDto DeserializeCore(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return Serializer.Deserialize(reader) as LayoutRootDto
            ?? throw new InvalidDataException("The stored layout is not a LayoutRoot.");
    }
}
