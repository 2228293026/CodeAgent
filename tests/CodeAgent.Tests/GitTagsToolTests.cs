using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitTagsToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gittags-" + Guid.NewGuid().ToString("N"));

    public GitTagsToolTests() => Directory.CreateDirectory(_dir);

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

    private bool InitWithCommit()
    {
        if (!TryGit("init", "--quiet"))
            return false;
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "content");
        if (!TryGit("add", "file.txt"))
            return false;
        return TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial");
    }

    [Fact]
    public async Task GitTags_ListsTagsWithObjectType()
    {
        if (!InitWithCommit())
            return;
        Assert.True(TryGit("tag", "v1.0"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "tag", "-a", "v2.0", "-m", "release two"));

        var result = await new GitTagsTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("v1.0", result);
        Assert.Contains("v2.0", result);
        Assert.Contains("commit", result);
        Assert.Contains("tag", result);
    }

    [Fact]
    public async Task GitTags_FiltersPatternAndCapsCount()
    {
        if (!InitWithCommit())
            return;
        Assert.True(TryGit("tag", "v1.0"));
        Assert.True(TryGit("tag", "release-1"));

        var result = await new GitTagsTool().ExecuteAsync(
            new JsonObject { ["pattern"] = "v*", ["max_tags"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("v1.0", result);
        Assert.DoesNotContain("release-1", result);
    }

    [Fact]
    public async Task GitTags_NoTagsReturnsFriendlyMessage()
    {
        if (!InitWithCommit())
            return;
        var result = await new GitTagsTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("没有匹配", result);
    }

    [Fact]
    public async Task GitTags_NonRepositoryReturnsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitTagsTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.Contains("git for-each-ref 失败", ex.Message);
    }

    [Fact]
    public async Task GitTags_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitTagsTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
