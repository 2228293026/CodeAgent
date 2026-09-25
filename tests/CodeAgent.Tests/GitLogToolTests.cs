using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitLogToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitlog-" + Guid.NewGuid().ToString("N"));

    public GitLogToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitLog_ShowsRecentCommitMessages()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "one");
        Assert.True(TryGit("add", "file.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "first commit"));
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "two");
        Assert.True(TryGit("add", "file.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "second commit"));

        var result = await new GitLogTool().ExecuteAsync(
            new JsonObject { ["max_commits"] = 1, ["include_files"] = true }, Context(), CancellationToken.None);
        Assert.Contains("second commit", result);
        Assert.Contains("file.txt", result);
    }

    [Fact]
    public async Task GitLog_FiltersByMessageKeyword()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "one");
        Assert.True(TryGit("add", "file.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "keep this"));
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "two");
        Assert.True(TryGit("add", "file.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "other change"));

        var result = await new GitLogTool().ExecuteAsync(
            new JsonObject { ["grep"] = "keep" }, Context(), CancellationToken.None);
        Assert.Contains("keep this", result);
        Assert.DoesNotContain("other change", result);
    }

    [Fact]
    public async Task GitLog_NonRepositoryReturnsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitLogTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.Contains("git log 失败", ex.Message);
    }

    [Fact]
    public async Task GitLog_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitLogTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
