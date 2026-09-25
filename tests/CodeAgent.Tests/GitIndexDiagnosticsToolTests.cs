using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitIndexDiagnosticsToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-indexdiag-" + Guid.NewGuid().ToString("N"));

    public GitIndexDiagnosticsToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitIndexDiagnostics_ReportsUnmergedStages()
    {
        if (!TryGit("init", "--quiet"))
            return;
        Assert.True(TryGit("branch", "-M", "main"));
        var file = Path.Combine(_dir, "conflict.txt");
        File.WriteAllText(file, "base");
        Assert.True(TryGit("add", "."));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "base"));
        Assert.True(TryGit("checkout", "-b", "feature"));
        File.WriteAllText(file, "feature");
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-am", "feature"));
        Assert.True(TryGit("checkout", "main"));
        File.WriteAllText(file, "main");
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-am", "main"));
        Assert.False(TryGit("-c", "merge.ff=false", "merge", "--no-ff", "--no-commit", "feature"));
        var result = await new GitIndexDiagnosticsTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未合并文件", result);
        Assert.Contains("阶段 1", result);
        Assert.Contains("阶段 2", result);
        Assert.Contains("阶段 3", result);
    }

    [Fact]
    public async Task GitIndexDiagnostics_ReportsCleanIndexAndRejectsOutside()
    {
        if (!TryGit("init", "--quiet"))
            return;
        var result = await new GitIndexDiagnosticsTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("0 个未合并文件", result);
        await Assert.ThrowsAsync<ToolException>(() => new GitIndexDiagnosticsTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task GitIndexDiagnostics_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitIndexDiagnosticsTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
