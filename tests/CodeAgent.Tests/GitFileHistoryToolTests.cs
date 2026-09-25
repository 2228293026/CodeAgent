using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitFileHistoryToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-filehistory-" + Guid.NewGuid().ToString("N"));

    public GitFileHistoryToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitFileHistory_ReportsFileCommitsAndRespectsLimit()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one");
        Assert.True(TryGit("add", "a.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "first"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "two");
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-am", "second"));
        var result = await new GitFileHistoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt", ["max_commits"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("Git 文件历史", result);
        Assert.Contains("a.txt", result);
        Assert.Contains("1 条", result);
    }

    [Fact]
    public async Task GitFileHistory_RejectsOutsideAndMissingPath()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitFileHistoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing.txt" }, Context(), CancellationToken.None));
        if (TryGit("init", "--quiet"))
        {
            await Assert.ThrowsAsync<ToolException>(() => new GitFileHistoryTool().ExecuteAsync(
                new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task GitFileHistory_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitFileHistoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt" }, Context(), cts.Token));
    }
}
