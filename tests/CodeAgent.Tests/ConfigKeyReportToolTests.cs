using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ConfigKeyReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-configkeys-" + Guid.NewGuid().ToString("N"));

    public ConfigKeyReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    [Fact]
    public void KnownKeys_AreDerivedFromAgentConfig()
    {
        var known = ConfigKeyReportTool.KnownKeys();
        Assert.Contains("tuiansi", known);
        Assert.Equal("TuiAnsi", known["tuiansi"]);
    }

    [Fact]
    public void Nearest_SuggestsClosestKnownKey()
    {
        var known = ConfigKeyReportTool.KnownKeys().Keys.ToList();
        Assert.Equal("tuiansi", ConfigKeyReportTool.Nearest("tuiansi_", known));
        Assert.Equal("", ConfigKeyReportTool.Nearest("zzzzzzzzzzzz", known));
    }

    [Fact]
    public async Task ConfigKeyReport_SeparatesKnownAndUnknownKeys()
    {
        File.WriteAllText(Path.Combine(_dir, "codeagent.json"),
            """{"tuiAnsi": false, "tuiAnsi_": true, "totallyUnrelatedKeyHere": 1}""");
        var result = await new ConfigKeyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = _dir }, Context(), CancellationToken.None);
        Assert.Contains("配置键报告: codeagent.json 共 3 个键，未知 2 个", result);
        Assert.Contains("tuiAnsi: bool", result);
        Assert.Contains("tuiAnsi_", result);
        Assert.Contains("疑似应为 tuiansi", result);
        // 离任何合法键都很远的未知键：仍然报为未知，只是没有建议
        Assert.Contains("totallyUnrelatedKeyHere", result);
        Assert.Contains("无相近的合法键", result);
    }

    [Fact]
    public async Task ConfigKeyReport_WalksNestedObjects()
    {
        File.WriteAllText(Path.Combine(_dir, "codeagent.json"), """{"tuiAnsi": true, "nested": {"a": 1}}""");
        var on = await new ConfigKeyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = _dir, ["include_nested"] = true }, Context(), CancellationToken.None);
        Assert.Contains("nested.a", on);
        var off = await new ConfigKeyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = _dir, ["include_nested"] = false }, Context(), CancellationToken.None);
        Assert.DoesNotContain("nested.a", off);
    }

    [Fact]
    public async Task ConfigKeyReport_RejectsMissingFileBadJsonOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new ConfigKeyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = _dir }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "codeagent.json"), "{ not json");
        await Assert.ThrowsAsync<ToolException>(() => new ConfigKeyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = _dir }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "codeagent.json"), """{"tuiAnsi": true}""");
        await Assert.ThrowsAsync<ToolException>(() => new ConfigKeyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConfigKeyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = _dir }, Context(), cts.Token));
    }
}
