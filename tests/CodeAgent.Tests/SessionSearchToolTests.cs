using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class SessionSearchToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-sstool-" + Guid.NewGuid().ToString("N"));

    public SessionSearchToolTests() => Directory.CreateDirectory(Path.Combine(_dir, ".codeagent", "sessions"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext MakeContext() => new()
    {
        // 绝对路径：工具按 进程当前目录 + SessionDir 拼接，测试进程的 cwd 是 bin 目录
        Config = new AgentConfig { SessionDir = Path.Combine(_dir, ".codeagent", "sessions") },
        Workspace = new Workspace(_dir),
    };

    [Fact]
    public async Task SessionSearch_FindsAcrossLogsAndSnapshots_NewestFirst()
    {
        var sessDir = Path.Combine(_dir, ".codeagent", "sessions");
        // 旧日志：命中 older 关键字
        var oldLog = Path.Combine(sessDir, "20260101-000001.jsonl");
        File.WriteAllLines(oldLog, ["""{"role":"user","content":"讨论 old-plan 方案"}"""]);
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-2));
        // 新日志：命中 newer 关键字
        var newLog = Path.Combine(sessDir, "20260102-000001.jsonl");
        File.WriteAllLines(newLog, ["""{"role":"assistant","content":"newer 结论已记录"}"""]);
        // 命名快照：命中 snapshot 关键字
        File.WriteAllText(Path.Combine(sessDir, "my-snapshot.json"),
            """[{"role":"user","content":"关于 snapshot 的对话"}]""");
        // 空日志跳过
        File.WriteAllText(Path.Combine(sessDir, "empty.jsonl"), "");

        var tool = new SessionSearchTool();
        var output = await tool.ExecuteAsync(
            new JsonObject { ["keyword"] = "plan" }, MakeContext(), CancellationToken.None);
        Assert.Contains("old-plan", output);
        Assert.Contains("/resume", output);

        var snapOut = await tool.ExecuteAsync(
            new JsonObject { ["keyword"] = "snapshot" }, MakeContext(), CancellationToken.None);
        Assert.Contains("快照 my-snapshot", snapOut);
        Assert.Contains("/load my-snapshot", snapOut);
    }

    [Fact]
    public async Task SessionSearch_NoKeyword_Throws()
    {
        var tool = new SessionSearchTool();
        await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject(), MakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SessionSearch_EmptySessionDir_ReturnsFriendlyMessage()
    {
        var tool = new SessionSearchTool();
        var ctx = new AgentContext
        {
            Config = new AgentConfig { SessionDir = ".codeagent/nowhere" },
            Workspace = new Workspace(_dir),
        };
        var output = await tool.ExecuteAsync(
            new JsonObject { ["keyword"] = "x" }, ctx, CancellationToken.None);
        Assert.Contains("还没有任何会话记录", output);
    }

    [Fact]
    public async Task SessionSearch_MaxFiles_ClampsToOne()
    {
        // max_files=1：只列出 1 个命中会话（即便有更多匹配）
        var sessDir = Path.Combine(_dir, ".codeagent", "sessions");
        for (int i = 0; i < 5; i++)
            File.WriteAllLines(Path.Combine(sessDir, $"2026010{i}-000001.jsonl"),
                ["{\"role\":\"user\",\"content\":\"命中 keyword-" + i + "\"}"]);

        var tool = new SessionSearchTool();
        var output = await tool.ExecuteAsync(
            new JsonObject { ["keyword"] = "keyword", ["max_files"] = 1 }, MakeContext(), CancellationToken.None);

        Assert.Contains("匹配 1 个会话", output); // 收敛到 1
    }

    [Fact]
    public async Task SessionSearch_MaxFilesOverCap_ClampsToTen()
    {
        // max_files 超过上限 10 应被收敛到 10
        var sessDir = Path.Combine(_dir, ".codeagent", "sessions");
        for (int i = 0; i < 12; i++)
            File.WriteAllLines(Path.Combine(sessDir, $"202601{i:00}-000001.jsonl"),
                ["{\"role\":\"user\",\"content\":\"命中 keyword-" + i + "\"}"]);

        var tool = new SessionSearchTool();
        var output = await tool.ExecuteAsync(
            new JsonObject { ["keyword"] = "keyword", ["max_files"] = 99 }, MakeContext(), CancellationToken.None);

        Assert.Contains("匹配 10 个会话", output); // 收敛到上限 10
    }
}
