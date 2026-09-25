using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitTreeEntriesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-treeentries-" + Guid.NewGuid().ToString("N"));

    public GitTreeEntriesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitTreeEntries_ListsTreeTypesAndLimits()
    {
        if (!TryGit("init", "--quiet"))
            return;
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.txt"), "a");
        Assert.True(TryGit("add", "."));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"));
        var result = await new GitTreeEntriesTool().ExecuteAsync(
            new JsonObject { ["recursive"] = true, ["max_results"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("Git tree 条目", result);
        Assert.Contains("显示 1", result);
    }

    [Fact]
    public async Task GitTreeEntries_RejectsUnknownAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitTreeEntriesTool().ExecuteAsync(
            new JsonObject { ["object"] = "not-an-object" }, Context(), CancellationToken.None));
        if (TryGit("init", "--quiet"))
        {
            await Assert.ThrowsAsync<ToolException>(() => new GitTreeEntriesTool().ExecuteAsync(
                new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task GitTreeEntries_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitTreeEntriesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
