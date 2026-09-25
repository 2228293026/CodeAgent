using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitStashInfoToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-stashinfo-" + Guid.NewGuid().ToString("N"));

    public GitStashInfoToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitStashInfo_ReportsMetadataAndFiles()
    {
        if (!TryGit("init", "--quiet"))
            return;
        Assert.True(TryGit("branch", "-M", "main"));
        File.WriteAllText(Path.Combine(_dir, "tracked.txt"), "base");
        Assert.True(TryGit("add", "tracked.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"));
        File.WriteAllText(Path.Combine(_dir, "tracked.txt"), "changed");
        File.WriteAllText(Path.Combine(_dir, "untracked.txt"), "new");
        Assert.True(TryGit("stash", "push", "--include-untracked", "--message", "save work"));
        var result = await new GitStashInfoTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git stash: stash@{0}", result);
        Assert.Contains("作者:", result);
        Assert.Contains("摘要: On main: save work", result);
        Assert.Contains("tracked.txt", result);
        Assert.Contains("untracked.txt", result);
    }

    [Fact]
    public async Task GitStashInfo_RejectsMissingOutsideAndInvalidRef()
    {
        if (TryGit("init", "--quiet"))
            await Assert.ThrowsAsync<ToolException>(() => new GitStashInfoTool().ExecuteAsync(
                new JsonObject(), Context(), CancellationToken.None));
        if (TryGit("init", "--quiet"))
        {
            await Assert.ThrowsAsync<ToolException>(() => new GitStashInfoTool().ExecuteAsync(
                new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
            await Assert.ThrowsAsync<ToolException>(() => new GitStashInfoTool().ExecuteAsync(
                new JsonObject { ["stash"] = "missing-ref" }, Context(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task GitStashInfo_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitStashInfoTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
