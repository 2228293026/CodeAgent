using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitIgnoredFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-ignored-" + Guid.NewGuid().ToString("N"));

    public GitIgnoredFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitIgnoredFiles_ListsAndFiltersIgnoredFiles()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(_dir, "debug.log"), "ignored");
        File.WriteAllText(Path.Combine(_dir, "visible.txt"), "visible");
        var result = await new GitIgnoredFilesTool().ExecuteAsync(
            new JsonObject { ["patterns"] = new JsonArray("*.log") }, Context(), CancellationToken.None);
        Assert.Contains("Git 忽略文件", result);
        Assert.Contains("debug.log", result);
        Assert.DoesNotContain("visible.txt", result);
    }

    [Fact]
    public async Task GitIgnoredFiles_RespectsLimitAndRejectsOutside()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "*.tmp\n");
        File.WriteAllText(Path.Combine(_dir, "a.tmp"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.tmp"), "b");
        var result = await new GitIgnoredFilesTool().ExecuteAsync(
            new JsonObject { ["max_results"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("未显示", result);
        await Assert.ThrowsAsync<ToolException>(() => new GitIgnoredFilesTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task GitIgnoredFiles_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitIgnoredFilesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
