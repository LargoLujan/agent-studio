using System;
using System.IO;

namespace TinyTests;

/// <summary>
/// Runs under the Low-IL test host. Proves, from inside a real test method, that the mandatory integrity policy blocks
/// writes to a Medium path (no-write-up) while writes to the Low work folder succeed and Medium files stay readable.
/// Paths come from environment variables set by the harness so nothing is hard-coded.
/// </summary>
public class VerificationProofs
{
    [Fact]
    public void LowWorkFolderIsWritable()
    {
        var dir = Environment.GetEnvironmentVariable("LOW_WORK_DIR");
        Assert.False(string.IsNullOrEmpty(dir), "LOW_WORK_DIR must be set by the harness.");
        var path = Path.Combine(dir!, "low-write-proof.txt");
        File.WriteAllText(path, "written-by-low-test");
        Assert.Equal("written-by-low-test", File.ReadAllText(path));
    }

    [Fact]
    public void MediumFolderIsNotWritable()
    {
        var dir = Environment.GetEnvironmentVariable("EVIL_MEDIUM_DIR");
        Assert.False(string.IsNullOrEmpty(dir), "EVIL_MEDIUM_DIR must be set by the harness.");
        var path = Path.Combine(dir!, "pwned-by-test.txt");

        var error = Record.Exception(() => File.WriteAllText(path, "pwned-by-test"));

        Assert.IsType<UnauthorizedAccessException>(error);
        Assert.False(File.Exists(path), "the Medium file must not exist: the OS must have blocked the write.");
    }

    [Fact]
    public void MediumFileIsReadable()
    {
        var file = Environment.GetEnvironmentVariable("READ_MEDIUM_FILE");
        Assert.False(string.IsNullOrEmpty(file), "READ_MEDIUM_FILE must be set by the harness.");
        Assert.Equal("secret-readable", File.ReadAllText(file!).Trim());
    }
}
