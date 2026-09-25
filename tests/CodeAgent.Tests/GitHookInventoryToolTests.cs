using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitHookInventoryToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-hooks-" + Guid.NewGuid().ToString("N"));

    public GitHookInventoryToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitHookInventory_ListsActiveAndSampleHooks()
    {
        if (!TryGit("init", "--quiet"))
            return;
        var hooks = Path.Combine(_dir, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\necho hook\n");
        File.WriteAllText(Path.Combine(hooks, "pre-push.sample"), "#!/bin/sh\necho sample\n");
        var result = await new GitHookInventoryTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git hooks 清单", result);
        Assert.Contains("pre-commit [活动]", result);
        Assert.Contains("pre-push.sample [示例]", result);
        Assert.Contains("#!/bin/sh", result);
    }

    [Fact]
    public async Task GitHookInventory_RejectsNonRepositoryOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitHookInventoryTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        if (TryGit("init", "--quiet"))
            await Assert.ThrowsAsync<ToolException>(() => new GitHookInventoryTool().ExecuteAsync(
                new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitHookInventoryTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
