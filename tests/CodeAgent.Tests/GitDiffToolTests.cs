using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitDiffToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitdiff-" + Guid.NewGuid().ToString("N"));

    public GitDiffToolTests() => Directory.CreateDirectory(_dir);

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

    private bool InitWithCommit()
    {
        if (!TryGit("init", "--quiet"))
            return false;
        File.WriteAllText(Path.Combine(_dir, "tracked.txt"), "one\ntwo\n");
        if (!TryGit("add", "tracked.txt"))
            return false;
        return TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial");
    }

    [Fact]
    public async Task GitDiff_ShowsUnstagedChanges()
    {
        if (!InitWithCommit())
            return;
        File.WriteAllText(Path.Combine(_dir, "tracked.txt"), "one\nchanged\n");

        var result = await new GitDiffTool().ExecuteAsync(
            new JsonObject { ["context_lines"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("未暂存差异", result);
        Assert.Contains("-two", result);
        Assert.Contains("+changed", result);
    }

    [Fact]
    public async Task GitDiff_ShowsStagedChanges()
    {
        if (!InitWithCommit())
            return;
        File.WriteAllText(Path.Combine(_dir, "tracked.txt"), "one\nstaged\n");
        Assert.True(TryGit("add", "tracked.txt"));

        var result = await new GitDiffTool().ExecuteAsync(
            new JsonObject { ["unstaged"] = false, ["staged"] = true }, Context(), CancellationToken.None);
        Assert.Contains("已暂存差异", result);
        Assert.Contains("+staged", result);
    }

    [Fact]
    public async Task GitDiff_NoChangesReturnsFriendlyMessage()
    {
        if (!InitWithCommit())
            return;
        var result = await new GitDiffTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("没有匹配", result);
    }

    [Fact]
    public async Task GitDiff_NonRepositoryReturnsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitDiffTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.Contains("git diff 失败", ex.Message);
    }

    [Fact]
    public async Task GitDiff_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitDiffTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
