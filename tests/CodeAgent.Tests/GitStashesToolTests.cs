using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitStashesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitstashes-" + Guid.NewGuid().ToString("N"));

    public GitStashesToolTests() => Directory.CreateDirectory(_dir);

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
        return TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial");
    }

    [Fact]
    public async Task GitStashes_ListsStashEntries()
    {
        if (!InitWithCommit())
            return;
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "work in progress");
        Assert.True(TryGit("stash", "push", "--quiet", "-m", "wip changes"));

        var result = await new GitStashesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git stash", result);
        Assert.Contains("wip changes", result);
    }

    [Fact]
    public async Task GitStashes_NoStashReturnsFriendlyMessage()
    {
        if (!InitWithCommit())
            return;
        var result = await new GitStashesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("没有 Git stash", result);
    }

    [Fact]
    public async Task GitStashes_NonRepositoryReturnsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitStashesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.Contains("git stash list 失败", ex.Message);
    }

    [Fact]
    public async Task GitStashes_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitStashesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
