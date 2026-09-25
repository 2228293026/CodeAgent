using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitConfigReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitconfig-" + Guid.NewGuid().ToString("N"));

    public GitConfigReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitConfigReport_SummarizesSectionsAndRedactsSecrets()
    {
        if (!TryGit("init", "--quiet"))
            return;
        Assert.True(TryGit("config", "user.name", "Tester"));
        Assert.True(TryGit("config", "user.email", "tester@example.com"));
        Assert.True(TryGit("config", "http.password", "super-secret"));
        Assert.True(TryGit("config", "remote.origin.url", "https://user:pass@example.com/repo.git"));
        var result = await new GitConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git 配置报告", result);
        Assert.Contains("user.name = Tester", result);
        Assert.Contains("http.password = <redacted>", result);
        Assert.Contains("remote.origin.url = <redacted>", result);
        Assert.DoesNotContain("super-secret", result);
    }

    [Fact]
    public async Task GitConfigReport_RejectsOutsideAndCancellation()
    {
        Assert.True(TryGit("init", "--quiet"));
        await Assert.ThrowsAsync<ToolException>(() => new GitConfigReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitConfigReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
