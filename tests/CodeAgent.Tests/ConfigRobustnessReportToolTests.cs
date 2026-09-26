using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ConfigRobustnessReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigRobustnessReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteSource(params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, "A.cs"), string.Join("\n", lines));

    [Fact]
    public async Task Cfg_FindsUnguardedEnv()
    {
        WriteSource(
            "using System;", "class A", "{", "    string M()", "    {",
            "        var key = Environment.GetEnvironmentVariable(\"API_KEY\");",
            "        return key;", "    }", "}");
        var result = await new ConfigRobustnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("配置健壮性报告: 1 个 .cs 文件", result);
        Assert.Contains("环境变量不判空", result);
    }

    [Fact]
    public async Task Cfg_CoalesceEnvIsClean()
    {
        WriteSource(
            "using System;", "class A", "{", "    string M()", "    {",
            "        var key = Environment.GetEnvironmentVariable(\"API_KEY\") ?? \"none\";",
            "        return key;", "    }", "}");
        var result = await new ConfigRobustnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("环境变量不判空", result);
    }

    [Fact]
    public async Task Cfg_FindsSilentParseFallback()
    {
        WriteSource(
            "class A", "{", "    int M(string s)", "    {",
            "        return int.TryParse(s, out var v) ? v : 0;", "    }", "}");
        var result = await new ConfigRobustnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("解析失败静默兜底", result);
    }

    [Fact]
    public async Task Cfg_FindsUnvalidatedRange()
    {
        WriteSource(
            "class A", "{", "    int M(Config c)", "    {",
            "        var timeout = c.GetInt(\"timeout\");", "        return timeout;", "    }", "}");
        var result = await new ConfigRobustnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("配置值无范围校验", result);
    }

    [Fact]
    public async Task Cfg_ClampedRangeIsClean()
    {
        WriteSource(
            "class A", "{", "    int M(Config c)", "    {",
            "        var timeout = Math.Clamp(c.GetInt(\"timeout\"), 1, 60);", "        return timeout;", "    }", "}");
        var result = await new ConfigRobustnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("配置值无范围校验", result);
    }

    [Fact]
    public async Task Cfg_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ConfigRobustnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ConfigRobustnessReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConfigRobustnessReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
