using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitTagInfoToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-taginfo-" + Guid.NewGuid().ToString("N"));

    public GitTagInfoToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitTagInfo_ReportsAnnotatedTag()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        Assert.True(TryGit("add", "a.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "tag", "-a", "v1.0", "-m", "release notes"));
        var result = await new GitTagInfoTool().ExecuteAsync(
            new JsonObject { ["tag"] = "v1.0" }, Context(), CancellationToken.None);
        Assert.Contains("Git 标签: v1.0", result);
        Assert.Contains("标签作者: Test <test@example.com>", result);
        Assert.Contains("release notes", result);
    }

    [Fact]
    public async Task GitTagInfo_RejectsUnknownAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitTagInfoTool().ExecuteAsync(
            new JsonObject { ["tag"] = "missing-tag" }, Context(), CancellationToken.None));
        if (TryGit("init", "--quiet"))
        {
            await Assert.ThrowsAsync<ToolException>(() => new GitTagInfoTool().ExecuteAsync(
                new JsonObject { ["tag"] = "v1", ["path"] = ".." }, Context(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task GitTagInfo_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitTagInfoTool().ExecuteAsync(
            new JsonObject { ["tag"] = "v1" }, Context(), cts.Token));
    }
}
