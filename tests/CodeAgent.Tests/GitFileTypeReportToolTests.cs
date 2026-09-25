using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitFileTypeReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-filetype-" + Guid.NewGuid().ToString("N"));

    public GitFileTypeReportToolTests() => Directory.CreateDirectory(_dir);

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
    [InlineData("src/Program.cs", "源码")]
    [InlineData("tests/FooTests.cs", "测试")]
    [InlineData("README.md", "文档")]
    [InlineData("codeagent.json", "配置")]
    [InlineData(".github/workflows/ci.yml", "配置")]
    [InlineData("assets/logo.png", "资源")]
    [InlineData("LICENSE", "其他")]
    public void Categorize_ClassifiesByPathAndExtension(string path, string expected) =>
        Assert.Equal(expected, GitFileTypeReportTool.Categorize(path));

    [Theory]
    [InlineData("src/Program.cs", true)]
    [InlineData("tests/FooTests.cs", false)]
    [InlineData("README.md", false)]
    [InlineData("tools/x.png", true)]
    public void LooksLikeSourcePath_DetectsSourceDirectories(string path, bool expected) =>
        Assert.Equal(expected, GitFileTypeReportTool.LooksLikeSourcePath(path));

    [Fact]
    public async Task GitFileTypeReport_GroupsTrackedFiles()
    {
        if (!TryGit("init", "--quiet"))
            return;
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.cs"), "a");
        File.WriteAllText(Path.Combine(_dir, "src", "logo.png"), "p");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# t");
        if (!TryGit("add", "-A"))
            return;
        var result = await new GitFileTypeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git 文件分类报告: 3 个已跟踪文件", result);
        Assert.Contains("源码: 1 个", result);
        Assert.Contains("文档: 1 个", result);
        Assert.Contains("资源: 1 个", result);
        // src/ 下的资源文件应被提示为「错放」
        Assert.Contains("源码目录中的资源/其他文件: 1 个", result);
        Assert.Contains("src/logo.png", result);
    }

    [Fact]
    public async Task GitFileTypeReport_RejectsNonRepositoryOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitFileTypeReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.True(TryGit("init", "--quiet"));
        await Assert.ThrowsAsync<ToolException>(() => new GitFileTypeReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitFileTypeReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
