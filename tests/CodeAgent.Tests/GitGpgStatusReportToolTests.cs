using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitGpgStatusReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gpg-" + Guid.NewGuid().ToString("N"));

    public GitGpgStatusReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitGpgStatusReport_ShowsConfigAndRedactsSigningKey()
    {
        Assert.True(TryGit("init", "--quiet"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        Assert.True(TryGit("add", "a.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"));
        Assert.True(TryGit("config", "commit.gpgsign", "true"));
        Assert.True(TryGit("config", "user.signingkey", "ABCDEF0123456789"));
        var result = await new GitGpgStatusReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git 签名状态报告", result);
        Assert.Contains("commit.gpgsign = true", result);
        Assert.Contains("user.signingkey = AB********89", result);
        Assert.Contains("最近提交:", result);
        Assert.DoesNotContain("ABCDEF0123456789", result);
    }

    [Fact]
    public async Task GitGpgStatusReport_RejectsOutsideAndCancellation()
    {
        Assert.True(TryGit("init", "--quiet"));
        await Assert.ThrowsAsync<ToolException>(() => new GitGpgStatusReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitGpgStatusReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
