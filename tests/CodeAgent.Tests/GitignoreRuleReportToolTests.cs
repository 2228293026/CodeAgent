using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitignoreRuleReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-ignorule-" + Guid.NewGuid().ToString("N"));

    public GitignoreRuleReportToolTests() => Directory.CreateDirectory(_dir);

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

    [Theory]
    [InlineData("*.log", "a.log", true)]
    [InlineData("*.log", "dir/a.log", true)]       // 无锚定：任意层级都匹配
    [InlineData("/build", "build", true)]
    [InlineData("/build", "src/build", false)]      // 锚定：只在根
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/sub/a.cs", false)]
    [InlineData("build/", "build/a.txt", true)]
    [InlineData("build/", "a.txt", false)]
    [InlineData("**/cache", "x/y/cache", true)]
    public void Matches_FollowsGitignoreSemantics(string pattern, string path, bool expected) =>
        Assert.Equal(expected, GitignoreRuleReportTool.Matches(pattern, path));

    [Fact]
    public void LastMatchingRule_LaterRuleWins()
    {
        var patterns = new List<(string Pattern, bool Negated, int Order)>
        {
            ("*.log", false, 0),
            ("!keep.log", true, 1),
        };
        Assert.Equal("*.log", GitignoreRuleReportTool.LastMatchingRule(patterns, "a.log"));
        Assert.Null(GitignoreRuleReportTool.LastMatchingRule(patterns, "keep.log"));
        Assert.Null(GitignoreRuleReportTool.LastMatchingRule(patterns, "a.txt"));
    }

    [Fact]
    public async Task GitignoreRuleReport_FindsIgnoredButTrackedFile()
    {
        if (!TryGit("init", "--quiet"))
            return;
        Directory.CreateDirectory(Path.Combine(_dir, "logs"));
        File.WriteAllText(Path.Combine(_dir, "logs", "a.log"), "x");
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "*.log\n");
        // 必须 -f 强制加入：文件若在 .gitignore 之前就被追踪，规则才会「命中却无效」
        if (!TryGit("add", "-f", "logs/a.log") || !TryGit("add", ".gitignore", "keep.txt"))
            return;
        var result = await new GitignoreRuleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Gitignore 规则报告: 1 条规则", result);
        Assert.Contains("被忽略规则命中却仍被追踪: 1", result);
        Assert.Contains("logs/a.log", result);
        Assert.Contains("git rm --cached", result);
    }

    [Fact]
    public async Task GitignoreRuleReport_FindsDeadNegation()
    {
        if (!TryGit("init", "--quiet"))
            return;
        // !build/ 前面没有任何规则能匹配 build/xxx → 永不生效
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "!build/\n");
        if (!TryGit("add", ".gitignore"))
            return;
        var result = await new GitignoreRuleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("永不可能生效的否定规则: 1", result);
        Assert.Contains("永不生效", result);
    }

    [Fact]
    public async Task GitignoreRuleReport_CleanRepoHasNoDeadRules()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "build/\n*.log\n!keep.log\n");
        if (!TryGit("add", "-A"))
            return;
        var result = await new GitignoreRuleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现失效规则", result);
    }

    [Fact]
    public async Task GitignoreRuleReport_RejectsNonRepositoryWithoutIgnoreOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitignoreRuleReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.True(TryGit("init", "--quiet"));
        var noIgnore = await new GitignoreRuleReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .gitignore 规则", noIgnore);
        await Assert.ThrowsAsync<ToolException>(() => new GitignoreRuleReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "*.log\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitignoreRuleReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
