using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitShowToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitshow-" + Guid.NewGuid().ToString("N"));

    public GitShowToolTests() => Directory.CreateDirectory(_dir);

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
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "hello\n");
        if (!TryGit("add", "file.txt"))
            return false;
        return TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial commit");
    }

    [Fact]
    public async Task GitShow_ShowsCommitMetadataAndPatch()
    {
        if (!InitWithCommit())
            return;
        var result = await new GitShowTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git show", result);
        Assert.Contains("initial commit", result);
        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task GitShow_CanHidePatch()
    {
        if (!InitWithCommit())
            return;
        var result = await new GitShowTool().ExecuteAsync(
            new JsonObject { ["include_diff"] = false }, Context(), CancellationToken.None);
        Assert.Contains("initial commit", result);
        Assert.DoesNotContain("+++ b/file.txt", result);
    }

    [Fact]
    public async Task GitShow_FiltersByPath()
    {
        if (!InitWithCommit())
            return;
        var result = await new GitShowTool().ExecuteAsync(
            new JsonObject { ["path"] = "file.txt" }, Context(), CancellationToken.None);
        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task GitShow_RejectsUnsafeRevision()
    {
        if (!InitWithCommit())
            return;
        await Assert.ThrowsAsync<ToolException>(() => new GitShowTool().ExecuteAsync(
            new JsonObject { ["revision"] = "--output=bad" }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task GitShow_NonRepositoryReturnsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitShowTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.Contains("git show 失败", ex.Message);
    }

    [Fact]
    public async Task GitShow_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitShowTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
