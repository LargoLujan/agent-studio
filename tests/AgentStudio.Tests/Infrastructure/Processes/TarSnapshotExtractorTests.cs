using System.Formats.Tar;
using System.IO;
using System.Text;
using AgentStudio.Infrastructure.Processes.Isolation;

namespace AgentStudio.Tests.Infrastructure.Processes;

/// <summary>
/// The extractor that turns the tar of <c>git archive</c> into the copy an isolated verification runs on (ADR-0017): it
/// writes regular files and folders only, inside its destination, and refuses everything else before writing it.
/// </summary>
public sealed class TarSnapshotExtractorTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private readonly string _container = Directory.CreateTempSubdirectory("as-tar-").FullName;

    private string Destination => Path.Combine(_container, "src");

    private string Outside => Path.Combine(_container, "fuera");

    private static MemoryStream Archive(params TarEntry[] entries)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                writer.WriteEntry(entry);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static PaxTarEntry File(string name, string content) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };

    private static PaxTarEntry Folder(string name) => new(TarEntryType.Directory, name);

    private static PaxGlobalExtendedAttributesTarEntry GlobalHeader() =>
        new(new Dictionary<string, string> { ["comment"] = Commit });

    private TarSnapshot Extract(Stream archive, TarSnapshotLimits? limits = null)
    {
        Directory.CreateDirectory(Destination);
        return TarSnapshotExtractor.Extract(archive, Destination, limits ?? TarSnapshotLimits.Default);
    }

    [Fact]
    public void Files_and_folders_are_written_with_their_content_and_the_global_header_gives_the_commit()
    {
        using var archive = Archive(
            GlobalHeader(),
            Folder("src/"),
            File("src/Calc.cs", "public static class Calc { }\n"),
            File("docs/guía de uso.md", "uno\ndos\n"),
            File("vacío.txt", string.Empty));

        var snapshot = Extract(archive);

        Assert.Equal(Commit, snapshot.Comment);
        Assert.Equal(3, snapshot.Files);
        Assert.Equal("public static class Calc { }\n", System.IO.File.ReadAllText(Path.Combine(Destination, "src", "Calc.cs")));
        Assert.Equal("uno\ndos\n", System.IO.File.ReadAllText(Path.Combine(Destination, "docs", "guía de uso.md")));
        Assert.Equal(0, new FileInfo(Path.Combine(Destination, "vacío.txt")).Length);
    }

    public static TheoryData<string> UnsafeNames() =>
    [
        "/etc/passwd",
        "C:/Windows/evil.txt",
        "C:evil.txt",
        "//server/share/evil.txt",
        "../fuera/evil.txt",
        "src/../../fuera/evil.txt",
        "src/..",
        "./evil.txt",
        "src//evil.txt",
        "src\\..\\..\\fuera\\evil.txt",
        "notas.txt:oculto",
        "NUL",
        "src/con.txt",
        "src/COM1",
        "evil.txt.",
        "evil.txt ",
        "evil\u0001.txt",
    ];

    [Theory]
    [MemberData(nameof(UnsafeNames))]
    public void A_name_that_could_land_outside_the_destination_or_mean_something_else_on_Windows_is_refused(string name)
    {
        using var archive = Archive(File("bien.txt", "ok"), File(name, "pwned"));

        Assert.Throws<InvalidDataException>(() => Extract(archive));

        Assert.False(Directory.Exists(Outside));
        Assert.DoesNotContain(Directory.EnumerateFiles(_container, "*", SearchOption.AllDirectories), f => System.IO.File.ReadAllText(f) == "pwned");
    }

    public static TheoryData<TarEntryType> LinksAndDevices() =>
    [
        TarEntryType.SymbolicLink,
        TarEntryType.HardLink,
        TarEntryType.CharacterDevice,
        TarEntryType.BlockDevice,
        TarEntryType.Fifo,
    ];

    [Theory]
    [MemberData(nameof(LinksAndDevices))]
    public void Links_and_devices_are_refused(TarEntryType type)
    {
        var entry = new PaxTarEntry(type, "enlace");
        if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
        {
            entry.LinkName = Outside;
        }

        using var archive = Archive(entry);

        Assert.Throws<InvalidDataException>(() => Extract(archive));
        Assert.False(Path.Exists(Path.Combine(Destination, "enlace")));
    }

    [Fact]
    public void A_second_entry_for_the_same_file_is_refused_instead_of_overwriting_the_first()
    {
        using var archive = Archive(File("Leeme.txt", "primero"), File("LEEME.txt", "segundo"));

        Assert.Throws<InvalidDataException>(() => Extract(archive));
        Assert.Equal("primero", System.IO.File.ReadAllText(Path.Combine(Destination, "Leeme.txt")));
    }

    [Fact]
    public void An_archive_larger_than_the_limits_is_refused()
    {
        using var big = Archive(File("a.txt", new string('x', 600)), File("b.txt", new string('y', 600)));
        Assert.Throws<InvalidDataException>(() => Extract(big, new TarSnapshotLimits(MaxTotalBytes: 1000, MaxEntries: 100)));

        Directory.Delete(Destination, recursive: true);
        using var many = Archive(File("a.txt", "a"), File("b.txt", "b"), File("c.txt", "c"));
        Assert.Throws<InvalidDataException>(() => Extract(many, new TarSnapshotLimits(MaxTotalBytes: 1000, MaxEntries: 2)));
    }

    public void Dispose() => Directory.Delete(_container, recursive: true);
}
