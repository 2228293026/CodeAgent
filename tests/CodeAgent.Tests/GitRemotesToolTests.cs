using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitRemotesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitremotes-" + Guid.NewGuid().ToString("N"));

    public GitRemotesToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitRemotes_ListsFetchAndPushUrlsAndRedactsCredentials()
    {
        if (!TryGit("init", "--quiet"))
            return;
        Assert.True(TryGit("remote", "add", "origin", "https://user:secret@example.com/repo.git"));

        var result = await new GitRemotesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("origin", result);
        Assert.Contains("fetch:", result);
        Assert.Contains("push:", result);
        Assert.Contains("***", result);
        Assert.Contains("example.com", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public async Task GitRemotes_FiltersByPattern()
    {
        if (!TryGit("init", "--quiet"))
            return;
        Assert.True(TryGit("remote", "add", "origin", "https://example.com/one.git"));
        Assert.True(TryGit("remote", "add", "upstream", "https://example.com/two.git"));

        var result = await new GitRemotesTool().ExecuteAsync(
            new JsonObject { ["pattern"] = "upstream" }, Context(), CancellationToken.None);
        Assert.Contains("upstream", result);
        Assert.DoesNotContain("origin", result);
    }

    [Fact]
    public async Task GitRemotes_NoRemoteReturnsFriendlyMessage()
    {
        if (!TryGit("init", "--quiet"))
            return;
        var result = await new GitRemotesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("没有配置", result);
    }

    [Fact]
    public async Task GitRemotes_NonRepositoryReturnsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitRemotesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.Contains("git remote -v 失败", ex.Message);
    }

    [Fact]
    public async Task GitRemotes_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitRemotesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
