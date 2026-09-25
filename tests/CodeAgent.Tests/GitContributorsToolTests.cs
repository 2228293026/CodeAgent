using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitContributorsToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-contributors-" + Guid.NewGuid().ToString("N"));

    public GitContributorsToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitContributors_CountsAuthorsAndCanShowEmail()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        Assert.True(TryGit("add", "a.txt"));
        Assert.True(TryGit("-c", "user.name=Alice", "-c", "user.email=alice@example.com", "commit", "--quiet", "-m", "one"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "b");
        Assert.True(TryGit("-c", "user.name=Bob", "-c", "user.email=bob@example.com", "commit", "--quiet", "-am", "two"));
        var result = await new GitContributorsTool().ExecuteAsync(
            new JsonObject { ["include_email"] = true, ["max_results"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("Git 贡献者", result);
        Assert.Contains("2 人", result);
        Assert.Contains("未显示", result);
    }

    [Fact]
    public async Task GitContributors_RejectsNonRepositoryAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitContributorsTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        if (TryGit("init", "--quiet"))
        {
            await Assert.ThrowsAsync<ToolException>(() => new GitContributorsTool().ExecuteAsync(
                new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task GitContributors_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitContributorsTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
