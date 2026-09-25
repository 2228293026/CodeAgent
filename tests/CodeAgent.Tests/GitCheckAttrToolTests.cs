using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitCheckAttrToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitattr-" + Guid.NewGuid().ToString("N"));

    public GitCheckAttrToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitCheckAttr_ReportsConfiguredAttributes()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, ".gitattributes"), "*.txt text eol=lf\n");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        var result = await new GitCheckAttrTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt", ["attributes"] = new JsonArray("text", "eol") }, Context(), CancellationToken.None);
        Assert.Contains("Git 属性检查", result);
        Assert.Contains("a.txt", result);
        Assert.Contains("text: set", result);
        Assert.Contains("eol: lf", result);
    }

    [Fact]
    public async Task GitCheckAttr_ReportsPathCap()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "b");
        var result = await new GitCheckAttrTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt", "b.txt"), ["max_paths"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("max_paths=1", result);
    }

    [Fact]
    public async Task GitCheckAttr_RejectsOutsidePath()
    {
        if (!TryGit("init", "--quiet"))
            return;
        await Assert.ThrowsAsync<ToolException>(() => new GitCheckAttrTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task GitCheckAttr_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitCheckAttrTool().ExecuteAsync(
            new JsonObject { ["path"] = "file.txt" }, Context(), cts.Token));
    }
}
