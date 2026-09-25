using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitPathDiffToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-pathdiff-" + Guid.NewGuid().ToString("N"));

    public GitPathDiffToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitPathDiff_ReportsPathChangesAndLimit()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one");
        Assert.True(TryGit("add", "."));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "two");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "new");
        Assert.True(TryGit("add", "."));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "change"));
        var result = await new GitPathDiffTool().ExecuteAsync(
            new JsonObject { ["from"] = "HEAD~1", ["to"] = "HEAD", ["max_results"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("Git 路径变化", result);
        Assert.Contains("2 项", result);
        Assert.Contains("未显示", result);
    }

    [Fact]
    public async Task GitPathDiff_RejectsOutsideAndUnknownRef()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitPathDiffTool().ExecuteAsync(
            new JsonObject { ["from"] = "missing-ref" }, Context(), CancellationToken.None));
        if (TryGit("init", "--quiet"))
        {
            await Assert.ThrowsAsync<ToolException>(() => new GitPathDiffTool().ExecuteAsync(
                new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task GitPathDiff_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitPathDiffTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
