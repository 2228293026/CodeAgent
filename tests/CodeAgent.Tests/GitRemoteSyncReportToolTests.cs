using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitRemoteSyncReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-remotesync-" + Guid.NewGuid().ToString("N"));

    public GitRemoteSyncReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitRemoteSyncReport_ReportsNoUpstreamBranch()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        if (!TryGit("add", "a.txt"))
            return;
        if (!TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"))
            return;
        var result = await new GitRemoteSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git 远程同步报告: 1 个分支", result);
        Assert.Contains("需推送: 0", result);
        Assert.Contains("无上游/无法比较: 1", result);
        Assert.Contains("无法比较", result);
    }

    [Fact]
    public async Task GitRemoteSyncReport_ReportsInSyncBranch()
    {
        // 本地裸仓库充当远程：推上去后本地与上游应完全同步
        if (!TryGit("init", "--quiet", "--bare"))
            return;
        var remoteDir = _dir + "-remote";
        Directory.CreateDirectory(remoteDir);
        if (!TryGit("init", "--quiet"))
            return;
        try
        {
            File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
            if (!TryGit("add", "a.txt"))
                return;
            if (!TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"))
                return;
            if (!TryGit("remote", "add", "origin", remoteDir))
                return;
            if (!TryGit("push", "--quiet", "-u", "origin", "HEAD"))
                return;
            var result = await new GitRemoteSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
            Assert.Contains("领先 0 / 落后 0", result);
            Assert.Contains("✓ 同步", result);
        }
        finally
        {
            try { Directory.Delete(remoteDir, true); } catch { }
        }
    }

    [Fact]
    public async Task GitRemoteSyncReport_RejectsNonRepositoryOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitRemoteSyncReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.True(TryGit("init", "--quiet"));
        await Assert.ThrowsAsync<ToolException>(() => new GitRemoteSyncReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitRemoteSyncReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
