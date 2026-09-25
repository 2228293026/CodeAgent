using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitCompareRefsToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-compare-" + Guid.NewGuid().ToString("N"));

    public GitCompareRefsToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitCompareRefs_ReportsDivergenceAndMergeBase()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        Assert.True(TryGit("branch", "-M", "main"));
        Assert.True(TryGit("add", "a.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"));
        const string baseBranch = "main";
        Assert.True(TryGit("checkout", "-b", "feature"));
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "b");
        Assert.True(TryGit("add", "b.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "feature"));
        var result = await new GitCompareRefsTool().ExecuteAsync(
            new JsonObject { ["left"] = baseBranch, ["right"] = "feature" }, Context(), CancellationToken.None);
        Assert.Contains("Git 引用比较", result);
        Assert.Contains("独有提交: 1", result);
        Assert.Contains("共同祖先:", result);
    }

    [Fact]
    public async Task GitCompareRefs_RejectsUnknownAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitCompareRefsTool().ExecuteAsync(
            new JsonObject { ["left"] = "missing-ref" }, Context(), CancellationToken.None));
        if (TryGit("init", "--quiet"))
        {
            await Assert.ThrowsAsync<ToolException>(() => new GitCompareRefsTool().ExecuteAsync(
                new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task GitCompareRefs_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitCompareRefsTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
