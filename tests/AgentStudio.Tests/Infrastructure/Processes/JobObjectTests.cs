using System.Diagnostics;
using System.IO;
using System.Text;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Processes;

public sealed class JobObjectTests
{
    [Fact]
    public async Task Closing_the_job_kills_a_grandchild_started_by_an_assigned_process()
    {
        var token = TestContext.Current.CancellationToken;
        var job = new JobObject();
        try
        {
            // The helper waits for a line on stdin before spawning, so the job assignment always precedes the child.
            var startInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "[void][Console]::In.ReadLine(); $p = Start-Process ping -ArgumentList '-n','60','127.0.0.1' -PassThru -WindowStyle Hidden; $p.Id; Start-Sleep 60");

            using var helper = Process.Start(startInfo)!;
            Assert.True(job.TryAssign(helper));
            await helper.StandardInput.WriteLineAsync("go".AsMemory(), token);
            await helper.StandardInput.FlushAsync(token);

            var grandchildPid = int.Parse((await helper.StandardOutput.ReadLineAsync(token))!, System.Globalization.CultureInfo.InvariantCulture);
            using var grandchild = Process.GetProcessById(grandchildPid);
            Assert.False(grandchild.HasExited);

            job.Dispose();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            await grandchild.WaitForExitAsync(limit.Token);
            await helper.WaitForExitAsync(limit.Token);
            Assert.True(grandchild.HasExited);
        }
        finally
        {
            job.Dispose();
        }
    }
}
