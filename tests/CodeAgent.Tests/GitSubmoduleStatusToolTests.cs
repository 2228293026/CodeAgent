using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitSubmoduleStatusToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-submodule-" + Guid.NewGuid().ToString("N"));

    public GitSubmoduleStatusToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitSubmoduleStatus_ReportsRepositoryWithoutSubmodules()
    {
        if (!TryGit("init", "--quiet"))
            return;
        var result = await new GitSubmoduleStatusTool().ExecuteAsync(
            new JsonObject { ["recursive"] = true }, Context(), CancellationToken.None);
        Assert.Contains("Git 子模块状态", result);
        Assert.Contains("0 个", result);
    }

    [Fact]
    public async Task GitSubmoduleStatus_RejectsNonRepositoryAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitSubmoduleStatusTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        if (TryGit("init", "--quiet"))
        {
            await Assert.ThrowsAsync<ToolException>(() => new GitSubmoduleStatusTool().ExecuteAsync(
                new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task GitSubmoduleStatus_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitSubmoduleStatusTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
