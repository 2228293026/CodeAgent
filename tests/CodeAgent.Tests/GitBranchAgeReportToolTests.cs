using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitBranchAgeReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-branchage-" + Guid.NewGuid().ToString("N"));

    public GitBranchAgeReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    /// <summary>跑 git，返回 (是否成功, 标准输出)。可为提交设置 200 天前的日期以构造陈旧分支。</summary>
    private (bool Ok, string Out) Git(string? committerDate, params string[] args)
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
            if (committerDate is not null)
            {
                psi.Environment["GIT_COMMITTER_DATE"] = committerDate;
                psi.Environment["GIT_AUTHOR_DATE"] = committerDate;
            }
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null)
                return (false, string.Empty);
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode == 0, output.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, string.Empty);
        }
    }

    [Fact]
    public async Task GitBranchAgeReport_OrdersByDateAndMarksStale()
    {
        if (!Git(null, "init", "--quiet").Ok)
            return;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        if (!Git(null, "add", "a.txt").Ok)
            return;
        var old = DateTimeOffset.UtcNow.AddDays(-200).ToString("yyyy-MM-ddTHH:mm:ssZ");
        if (!Git(old, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial").Ok)
            return;
        if (!Git(null, "branch", "old-branch").Ok)
            return;
        var current = Git(null, "rev-parse", "--abbrev-ref", "HEAD").Out;
        Assert.NotEqual(string.Empty, current);

        var result = await new GitBranchAgeReportTool().ExecuteAsync(
            new JsonObject { ["stale_days"] = 30 }, Context(), CancellationToken.None);
        Assert.Contains("Git 分支时效报告: 2 个分支，陈旧（>30 天未提交）: 2", result);
        Assert.Contains("old-branch", result);
        Assert.Contains("⚠陈旧", result);
        Assert.Contains($" {current} *", result); // 当前分支带 * 标记
        Assert.Contains("（* 为当前分支）", result);
    }

    [Fact]
    public async Task GitBranchAgeReport_RejectsNonRepositoryOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitBranchAgeReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.True(Git(null, "init", "--quiet").Ok);
        await Assert.ThrowsAsync<ToolException>(() => new GitBranchAgeReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitBranchAgeReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
