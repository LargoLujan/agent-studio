using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace AgentStudio.Tests.Orchestration.Verification.Reports;

/// <summary>Fixtures and helpers shared by the report parser tests.</summary>
internal static class ReportTestSupport
{
    /// <summary>
    /// Path of a fixture under <c>Orchestration/Verification/Fixtures/xunit-v3-4.0.2</c>, located from the source file of
    /// the test (the test project does not copy these files to the output folder yet). A missing fixture fails the test.
    /// </summary>
    internal static string Fixture(string name, [CallerFilePath] string callerFile = "")
    {
        var directory = Path.GetDirectoryName(callerFile)!;
        var full = Path.GetFullPath(Path.Combine(directory, "..", "Fixtures", "xunit-v3-4.0.2", name));
        Assert.True(File.Exists(full), $"Fixture not found: {full}");
        return full;
    }

    internal static MemoryStream Utf8(string xml) => new(new UTF8Encoding(false).GetBytes(xml));

    /// <summary>
    /// A readable stream that produces its content on the fly and cannot seek, so a parser that tried to load the whole
    /// document would have to buffer it all.
    /// </summary>
    internal sealed class GeneratedStream(IEnumerable<string> chunks) : Stream
    {
        private readonly IEnumerator<string> _chunks = chunks.GetEnumerator();
        private byte[] _current = [];
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_offset >= _current.Length)
            {
                if (!_chunks.MoveNext())
                {
                    return 0;
                }

                _current = Encoding.UTF8.GetBytes(_chunks.Current);
                _offset = 0;
            }

            var length = Math.Min(count, _current.Length - _offset);
            Buffer.BlockCopy(_current, _offset, buffer, offset, length);
            _offset += length;
            return length;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _chunks.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
