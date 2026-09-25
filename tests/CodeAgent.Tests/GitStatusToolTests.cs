using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitStatusToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitstatus-" + Guid.NewGuid().ToString("N"));

    public GitStatusToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitStatus_ReportsBranchAndPorcelainChanges()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "new.txt"), "hello");
        Assert.True(TryGit("add", "new.txt"));

        var result = await new GitStatusTool().ExecuteAsync(
            new JsonObject { ["include_diff"] = false }, Context(), CancellationToken.None);

        Assert.Contains("Git 状态", result);
        Assert.Contains("new.txt", result);
    }

    [Fact]
    public async Task GitStatus_IncludesDiffStatsWhenRequested()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "tracked.txt"), "one");
        Assert.True(TryGit("add", "tracked.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"));
        File.WriteAllText(Path.Combine(_dir, "tracked.txt"), "changed");

        var result = await new GitStatusTool().ExecuteAsync(
            new JsonObject { ["include_diff"] = true }, Context(), CancellationToken.None);
        Assert.Contains("工作区 diff:", result);
        Assert.Contains("tracked.txt", result);
    }

    [Fact]
    public async Task GitStatus_NonRepositoryReturnsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitStatusTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.Contains("git status 失败", ex.Message);
    }

    [Fact]
    public async Task GitStatus_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitStatusTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
