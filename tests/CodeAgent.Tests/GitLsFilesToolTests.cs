using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitLsFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitlsfiles-" + Guid.NewGuid().ToString("N"));

    public GitLsFilesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitLsFiles_ListsTrackedAndUntrackedModes()
    {
        if (!TryGit("init", "--quiet"))
            return;
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.cs"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "b");
        Assert.True(TryGit("add", "src/a.cs"));
        var tracked = await new GitLsFilesTool().ExecuteAsync(
            new JsonObject { ["mode"] = "tracked" }, Context(), CancellationToken.None);
        Assert.Contains("src/a.cs", tracked);
        Assert.DoesNotContain("b.txt", tracked);
        var untracked = await new GitLsFilesTool().ExecuteAsync(
            new JsonObject { ["mode"] = "untracked" }, Context(), CancellationToken.None);
        Assert.Contains("b.txt", untracked);
    }

    [Fact]
    public async Task GitLsFiles_FiltersPatternsAndReportsCap()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "a");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "b");
        Assert.True(TryGit("add", "a.cs", "b.txt"));
        var filtered = await new GitLsFilesTool().ExecuteAsync(
            new JsonObject { ["patterns"] = new JsonArray("*.cs") }, Context(), CancellationToken.None);
        Assert.Contains("a.cs", filtered);
        Assert.DoesNotContain("b.txt", filtered);
        var capped = await new GitLsFilesTool().ExecuteAsync(
            new JsonObject { ["max_results"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("另有", capped);
    }

    [Fact]
    public async Task GitLsFiles_RejectsOutsideAndCancels()
    {
        if (!TryGit("init", "--quiet"))
            return;
        await Assert.ThrowsAsync<ToolException>(() => new GitLsFilesTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitLsFilesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
