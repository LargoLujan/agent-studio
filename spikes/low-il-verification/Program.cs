using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LowIlVerification.Native;

namespace LowIlVerification;

/// <summary>
/// Spike harness: proves that <c>dotnet build</c> / <c>dotnet test</c> can run under a Low integrity-level token,
/// without admin rights, so that the mandatory no-write-up policy blocks writes outside dedicated Low folders while
/// build and test still work. Run with no arguments (<c>run</c>) to execute every proof; the other verbs are the
/// child modes the harness launches under the Low token.
/// </summary>
internal static class Program
{
    internal static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: LowIlVerification <run|overhead|probe-token|probe-net|probe-write|spawn-tree>");
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "run" => Harness.Run(),
                "overhead" => Harness.Overhead(),
                "probe-token" => ChildProbes.Token(),
                "probe-net" => ChildProbes.Network(),
                "probe-write" => ChildProbes.Write(args),
                "spawn-tree" => ChildProbes.SpawnTree(args),
                var other => Fail($"unknown verb '{other}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}

/// <summary>The modes launched as Low-IL children.</summary>
internal static class ChildProbes
{
    public static int Token()
    {
        Console.WriteLine($"INTEGRITY_SID={LowIntegrityLauncher.CurrentIntegritySid()}");
        Console.WriteLine($"USER={Environment.UserName}");
        Console.WriteLine($"PID={Environment.ProcessId}");
        return 0;
    }

    public static int Network()
    {
        // DNS lookup.
        try
        {
            var addresses = Dns.GetHostAddresses("api.nuget.org");
            Console.WriteLine($"DNS api.nuget.org -> {string.Join(", ", addresses.Select(a => a.ToString()))}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DNS FAILED {ex.GetType().Name}: {ex.Message}");
        }

        // Outbound TCP to a real host (expected to connect: Low IL does not block sockets).
        TcpConnect("api.nuget.org", 443);

        // Loopback to a closed port (expected refusal, not a security block).
        TcpConnect("127.0.0.1", 1);
        return 0;
    }

    public static int Write(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("probe-write <lowDir> <mediumDir>");
            return 2;
        }

        var lowDir = args[1];
        var mediumDir = args[2];

        try
        {
            var p = Path.Combine(lowDir, "probe-write-low.txt");
            File.WriteAllText(p, "ok");
            Console.WriteLine($"WRITE_LOW ok -> {p}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WRITE_LOW FAILED {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var p = Path.Combine(mediumDir, "probe-write-medium.txt");
            File.WriteAllText(p, "pwned");
            Console.WriteLine($"WRITE_MEDIUM UNEXPECTED-OK -> {p}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WRITE_MEDIUM blocked {ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }

    public static int SpawnTree(string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 60;
        var ping = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), $"-n {seconds} 127.0.0.1")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            },
        };
        ping.Start();
        Console.WriteLine($"CHILD_PID={Environment.ProcessId}");
        Console.WriteLine($"GRANDCHILD_PID={ping.Id}");
        Console.Out.Flush();
        ping.WaitForExit();
        return 0;
    }

    private static void TcpConnect(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            if (task.Wait(TimeSpan.FromSeconds(5)) && client.Connected)
            {
                Console.WriteLine($"TCP {host}:{port} CONNECTED");
            }
            else
            {
                Console.WriteLine($"TCP {host}:{port} no connection within 5s");
            }
        }
        catch (Exception ex)
        {
            var inner = (ex as AggregateException)?.InnerException ?? ex;
            var se = inner as SocketException;
            Console.WriteLine($"TCP {host}:{port} {inner.GetType().Name} {(se is not null ? se.SocketErrorCode.ToString() : string.Empty)}: {inner.Message}");
        }
    }
}
