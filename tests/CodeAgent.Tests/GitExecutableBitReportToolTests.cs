using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitExecutableBitReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitmode-" + Guid.NewGuid().ToString("N"));

    public GitExecutableBitReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitExecutableBitReport_ListsModesAndExecutables()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "run.sh"), "#!/bin/sh\necho hi\n");
        if (!TryGit("add", "a.txt", "run.sh"))
            return;
        if (!TryGit("update-index", "--chmod=+x", "run.sh"))
            return;
        var result = await new GitExecutableBitReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git 文件模式报告:", result);
        Assert.Contains("100755: 1", result);
        Assert.Contains("100644: 1", result);
        Assert.Contains("可执行文件（100755）: 1", result);
        Assert.Contains("run.sh", result);
        Assert.Contains("符号链接（120000）: 0", result);
    }

    [Fact]
    public async Task GitExecutableBitReport_RejectsNonRepositoryOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitExecutableBitReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.True(TryGit("init", "--quiet"));
        await Assert.ThrowsAsync<ToolException>(() => new GitExecutableBitReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitExecutableBitReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
