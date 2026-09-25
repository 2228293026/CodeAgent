using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitReflogToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitreflog-" + Guid.NewGuid().ToString("N"));

    public GitReflogToolTests() => Directory.CreateDirectory(_dir);

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
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "one");
        if (!TryGit("add", "file.txt"))
            return false;
        return TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "first commit");
    }

    [Fact]
    public async Task GitReflog_ShowsHeadMovementHistory()
    {
        if (!InitWithCommit())
            return;
        Assert.True(TryGit("checkout", "--quiet", "-b", "feature"));
        var result = await new GitReflogTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git reflog", result);
        Assert.Contains("checkout", result);
    }

    [Fact]
    public async Task GitReflog_CanIncludeAllAndLimitEntries()
    {
        if (!InitWithCommit())
            return;
        var result = await new GitReflogTool().ExecuteAsync(
            new JsonObject { ["include_all"] = true, ["max_entries"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("全部引用", result);
    }

    [Fact]
    public async Task GitReflog_NonRepositoryReturnsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitReflogTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.Contains("git reflog 失败", ex.Message);
    }

    [Fact]
    public async Task GitReflog_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitReflogTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
