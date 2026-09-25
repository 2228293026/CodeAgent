using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitBranchesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitbranches-" + Guid.NewGuid().ToString("N"));

    public GitBranchesToolTests() => Directory.CreateDirectory(_dir);

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
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "content");
        if (!TryGit("add", "file.txt"))
            return false;
        return TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial");
    }

    [Fact]
    public async Task GitBranches_ListsLocalBranches()
    {
        if (!InitWithCommit())
            return;
        Assert.True(TryGit("branch", "feature/test"));

        var result = await new GitBranchesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("feature/test", result);
        Assert.Contains("initial", result);
    }

    [Fact]
    public async Task GitBranches_FiltersByPatternAndCapsOutput()
    {
        if (!InitWithCommit())
            return;
        Assert.True(TryGit("branch", "feature/one"));
        Assert.True(TryGit("branch", "other"));

        var result = await new GitBranchesTool().ExecuteAsync(
            new JsonObject { ["pattern"] = "feature/*", ["max_branches"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("feature/one", result);
        Assert.DoesNotContain("other", result);
    }

    [Fact]
    public async Task GitBranches_RequiresAtLeastOneBranchType()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitBranchesTool().ExecuteAsync(
            new JsonObject { ["include_local"] = false, ["include_remote"] = false },
            Context(), CancellationToken.None));
    }

    [Fact]
    public async Task GitBranches_NonRepositoryReturnsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitBranchesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.Contains("git for-each-ref 失败", ex.Message);
    }

    [Fact]
    public async Task GitBranches_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitBranchesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
