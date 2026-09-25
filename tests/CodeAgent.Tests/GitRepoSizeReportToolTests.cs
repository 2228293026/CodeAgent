using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitRepoSizeReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-reposize-" + Guid.NewGuid().ToString("N"));

    public GitRepoSizeReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitRepoSizeReport_ListsDotGitBreakdown()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        TryGit("add", "a.txt");
        TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial");
        var result = await new GitRepoSizeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git 仓库体积报告: .git 共", result);
        Assert.Contains("objects:", result);
        Assert.Contains("（根目录文件）", result);
        Assert.Contains("提示:", result);
    }

    [Fact]
    public async Task GitRepoSizeReport_RejectsNonRepositoryOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitRepoSizeReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.True(TryGit("init", "--quiet"));
        await Assert.ThrowsAsync<ToolException>(() => new GitRepoSizeReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitRepoSizeReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
