using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitLfsReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-lfs-" + Guid.NewGuid().ToString("N"));

    public GitLfsReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private bool TryGit(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = _dir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [Fact]
    public async Task GitLfsReport_ReportsAvailabilityOrGracefulFallback()
    {
        Assert.True(TryGit("init", "--quiet"));
        var result = await new GitLfsReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git LFS 报告", result);
        Assert.True(result.Contains("可用") || result.Contains("不可用"));
    }

    [Fact]
    public async Task GitLfsReport_RejectsOutsideAndCancellation()
    {
        Assert.True(TryGit("init", "--quiet"));
        await Assert.ThrowsAsync<ToolException>(() => new GitLfsReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitLfsReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
