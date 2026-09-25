using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitCheckIgnoreToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitignore-" + Guid.NewGuid().ToString("N"));

    public GitCheckIgnoreToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitCheckIgnore_ReportsIgnoredAndUnignoredPaths()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(_dir, "debug.log"), "log");
        File.WriteAllText(Path.Combine(_dir, "main.cs"), "code");
        var result = await new GitCheckIgnoreTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("debug.log", "main.cs"), ["include_unignored"] = true },
            Context(), CancellationToken.None);
        Assert.Contains("已忽略: debug.log", result);
        Assert.Contains("未忽略: main.cs", result);
        Assert.Contains("*.log", result);
    }

    [Fact]
    public async Task GitCheckIgnore_ReportsPathCap()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(_dir, "a.log"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.log"), "b");
        var result = await new GitCheckIgnoreTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.log", "b.log"), ["max_paths"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("max_paths=1", result);
    }

    [Fact]
    public async Task GitCheckIgnore_RejectsOutsidePath()
    {
        if (!TryGit("init", "--quiet"))
            return;
        await Assert.ThrowsAsync<ToolException>(() => new GitCheckIgnoreTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task GitCheckIgnore_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitCheckIgnoreTool().ExecuteAsync(
            new JsonObject { ["path"] = "file.txt" }, Context(), cts.Token));
    }
}
