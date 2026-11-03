using System.IO;
using System.Runtime.InteropServices;
using AgentStudio.Providers.Cli.Codex;

namespace AgentStudio.Tests.Providers.Codex;

public sealed class CodexExecutableResolverTests : IDisposable
{
    private readonly string _root = CodexTestSupport.NewTempDirectory();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Shim()
    {
        var shim = Path.Combine(_root, "codex.cmd");
        File.WriteAllText(shim, "@echo off");
        return shim;
    }

    private string Native(string platform, string triple)
    {
        var bin = Path.Combine(_root, "node_modules", "@openai", "codex", "node_modules", "@openai", platform, "vendor", triple, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "codex.exe");
        File.WriteAllText(exe, "x");
        return exe;
    }

    [Fact]
    public void Npm_shim_resolves_to_the_native_binary()
    {
        var native = Native("codex-win32-x64", "x86_64-pc-windows-msvc");

        Assert.Equal(native, CodexExecutableResolver.Resolve(Shim(), Architecture.X64));
    }

    [Fact]
    public void Prefers_the_package_of_the_current_architecture()
    {
        Native("codex-win32-x64", "x86_64-pc-windows-msvc");
        var arm = Native("codex-win32-arm64", "aarch64-pc-windows-msvc");

        Assert.Equal(arm, CodexExecutableResolver.Resolve(Shim(), Architecture.Arm64));
    }

    [Fact]
    public void Shim_without_native_package_is_kept()
    {
        var shim = Shim();

        Assert.Equal(shim, CodexExecutableResolver.Resolve(shim, Architecture.X64));
    }

    [Fact]
    public void Platform_package_without_binary_keeps_the_shim()
    {
        var shim = Shim();
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin"));

        Assert.Equal(shim, CodexExecutableResolver.Resolve(shim, Architecture.X64));
    }

    [Fact]
    public void Real_executable_is_returned_unchanged()
    {
        var exe = Path.Combine(_root, "codex.exe");
        File.WriteAllText(exe, "x");
        Native("codex-win32-x64", "x86_64-pc-windows-msvc");

        Assert.Equal(exe, CodexExecutableResolver.Resolve(exe, Architecture.X64));
    }
}
