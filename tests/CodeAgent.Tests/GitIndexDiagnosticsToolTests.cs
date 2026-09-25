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

    private bool TryGit(params string[] args) => RunGit("", args).Success;

    private (bool Success, string Output) RunGit(string input, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = _dir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null)
                return (false, "");
            process.StandardInput.Write(input);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode == 0, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, "");
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
        var baseBlob = RunGit("", "rev-parse", "HEAD:conflict.txt").Output.Trim();
        Assert.True(TryGit("checkout", "-b", "feature"));
        File.WriteAllText(file, "feature");
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-am", "feature"));
        var featureBlob = RunGit("", "rev-parse", "HEAD:conflict.txt").Output.Trim();
        Assert.True(TryGit("checkout", "main"));
        File.WriteAllText(file, "main");
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-am", "main"));
        var mainBlob = RunGit("", "rev-parse", "HEAD:conflict.txt").Output.Trim();
        var indexInfo = $"100644 {baseBlob} 1\tconflict.txt\n100644 {mainBlob} 2\tconflict.txt\n100644 {featureBlob} 3\tconflict.txt\n";
        Assert.True(RunGit(indexInfo, "update-index", "--index-info").Success);
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
