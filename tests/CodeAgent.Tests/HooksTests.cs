using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 工具钩子（preToolUse / postToolUse）。
///
/// 它把「工具执行前后跑用户脚本」接进了 <see cref="ToolRegistry.ExecuteAsync"/> 这个唯一出口。
/// 这里守的核心是**否决语义**：退出码 2 必须真的让工具不执行——
/// 若只是打印一句「已拦截」而工具照样跑了，那比没有这个功能更危险
/// （用户以为自己立了规矩，实际什么都没拦住）。
///
/// 端到端用例里的钩子命令一律用 <c>exit N</c>：cmd / powershell / bash / sh **四种都认**，
/// 不依赖具体平台，也不会因为 CI 上没有 Git Bash 而红。
/// </summary>
public class HooksTests : IDisposable
{
    private readonly string _dir;

    public HooksTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ca_hooks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private AgentContext Ctx(params HookConfig[] hooks)
    {
        var config = new AgentConfig();
        config.Hooks.AddRange(hooks);
        return new AgentContext { Config = config, Workspace = new Workspace(_dir) };
    }

    private static HookConfig Hook(string ev, string matcher, string command, int timeout = 30) =>
        new() { Event = ev, Matcher = matcher, Command = command, TimeoutSeconds = timeout };

    /// <summary>一个只数调用次数的假工具：用来证明「钩子否决时工具压根没跑」。</summary>
    private sealed class CountingTool : ITool
    {
        public int Calls { get; private set; }
        public string Name => "probe";
        public string Description => "测试用假工具";
        public JsonObject Parameters => new();

        public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult("工具结果");
        }
    }

    private static async Task<(string Result, int Calls)> RunAsync(AgentContext ctx)
    {
        var registry = new ToolRegistry();
        var tool = new CountingTool();
        registry.Register(tool);
        var result = await registry.ExecuteAsync("probe", "{}", ctx, CancellationToken.None);
        return (result, tool.Calls);
    }

    // ── 解析与匹配（纯逻辑） ────────────────────────────────────────────

    [Fact]
    public void ParseAcceptsBothEventsAndNormalizes()
    {
        var defs = Hooks.Parse([
            Hook("preToolUse", "", "  echo a  "),
            Hook("PostToolUse", "write_file", "echo b", timeout: 9999),
        ]);

        Assert.Equal(2, defs.Count);
        Assert.Equal(HookEvent.PreToolUse, defs[0].Event);
        Assert.Equal("*", defs[0].Matcher); // 省略 matcher = 全部工具
        Assert.Equal("echo a", defs[0].Command);
        Assert.Equal(HookEvent.PostToolUse, defs[1].Event);
        Assert.Equal(300, defs[1].TimeoutSeconds); // 收敛到上限
    }

    [Fact]
    public void ParseSkipsInvalidEntries()
    {
        // 非法时机与空命令都不会执行（配置加载阶段已逐条警告，这里不重复）
        var defs = Hooks.Parse([
            Hook("onSomething", "*", "exit 2"),
            Hook("preToolUse", "*", "   "),
            Hook("preToolUse", "*", "exit 0"),
        ]);

        Assert.Single(defs);
        Assert.Equal("exit 0", defs[0].Command);
    }

    [Fact]
    public void MatcherIsWildcardOrExactName()
    {
        Assert.True(Hooks.Matches("*", "write_file"));
        Assert.True(Hooks.Matches("", "write_file"));
        Assert.True(Hooks.Matches("WRITE_FILE", "write_file"));
        Assert.False(Hooks.Matches("write", "write_file")); // 不做前缀匹配：半个名字不该命中
        Assert.False(Hooks.Matches("write_file", "read_file"));
    }

    [Fact]
    public void PayloadCarriesEventToolArgsAndCwd()
    {
        var args = new JsonObject { ["path"] = "a.txt" };

        var json = Hooks.BuildPayload(HookEvent.PreToolUse, "write_file", args, null, "/w");
        var payload = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("preToolUse", payload["event"]!.GetValue<string>());
        Assert.Equal("write_file", payload["tool"]!.GetValue<string>());
        Assert.Equal("a.txt", payload["arguments"]!["path"]!.GetValue<string>());
        Assert.Equal("/w", payload["cwd"]!.GetValue<string>());
        // pre 阶段工具还没跑：不该出现 result 字段（写成 null 会让脚本以为“结果就是 null”）
        Assert.False(payload.ContainsKey("result"));
    }

    [Fact]
    public void PostPayloadCarriesTheResult()
    {
        var json = Hooks.BuildPayload(HookEvent.PostToolUse, "probe", new JsonObject(), "工具结果", "/w");
        var payload = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("postToolUse", payload["event"]!.GetValue<string>());
        Assert.Equal("工具结果", payload["result"]!.GetValue<string>());
    }

    [Fact]
    public void OnlyExitCodeTwoBlocks()
    {
        var def = new HookDefinition(HookEvent.PreToolUse, "*", "x", 5);

        Assert.True(new HookRun(def, 2, "", "no", false).Blocks);
        Assert.False(new HookRun(def, 1, "", "boom", false).Blocks); // 环境问题不就等于否决
        Assert.False(new HookRun(def, 0, "", "", false).Blocks);
    }

    [Fact]
    public void ReasonPrefersStdErrThenStdOutThenExitCode()
    {
        var def = new HookDefinition(HookEvent.PreToolUse, "*", "x", 5);

        Assert.Equal("err", new HookRun(def, 2, "out", "err", false).Reason);
        Assert.Equal("out", new HookRun(def, 2, "out", "", false).Reason);
        Assert.Equal("退出码 2", new HookRun(def, 2, "", "", false).Reason);
        Assert.Contains("超时", new HookRun(def, 124, "", "", TimedOut: true).Reason);
    }

    // ── 端到端：真的跑 shell，真的拦 ───────────────────────────────────

    [Fact]
    public async Task PreHookExitTwoReallyPreventsTheToolFromRunning()
    {
        var ctx = Ctx(Hook("preToolUse", "*", "exit 2"));

        var (result, calls) = await RunAsync(ctx);

        Assert.Equal(0, calls); // 关键：不是「打印一句拦住」，而是工具真的没跑
        Assert.Contains("拦截", result);
    }

    [Fact]
    public async Task PreHookExitZeroLetsTheToolRun()
    {
        var ctx = Ctx(Hook("preToolUse", "*", "exit 0"));

        var (result, calls) = await RunAsync(ctx);

        Assert.Equal(1, calls);
        Assert.Equal("工具结果", result); // 通过的钩子不往结果里塞东西
    }

    [Fact]
    public async Task PreHookFailureOtherThanTwoIsReportedButDoesNotBlock()
    {
        var ctx = Ctx(Hook("preToolUse", "*", "exit 3"));

        var (result, calls) = await RunAsync(ctx);

        Assert.Equal(1, calls);
        // 不拦，但必须说出来：静默吞掉的后果是「配了钩子却一直没生效」
        Assert.Contains("退出码 3", result);
        Assert.Contains("工具结果", result);
    }

    [Fact]
    public async Task PostHookExitTwoAppendsFeedbackToTheResult()
    {
        var ctx = Ctx(Hook("postToolUse", "*", "exit 2"));

        var (result, calls) = await RunAsync(ctx);

        Assert.Equal(1, calls); // 事后钩子不拦执行，只补反馈
        Assert.Contains("postToolUse", result);
        Assert.Contains("工具结果", result);
    }

    [Fact]
    public async Task HookWhoseMatcherDoesNotMatchNeverRuns()
    {
        var ctx = Ctx(Hook("preToolUse", "other_tool", "exit 2"));

        var (result, calls) = await RunAsync(ctx);

        Assert.Equal(1, calls);
        Assert.Equal("工具结果", result);
    }

    [Fact]
    public async Task NoHooksMeansPurePassThrough()
    {
        var (result, calls) = await RunAsync(Ctx());

        Assert.Equal(1, calls);
        Assert.Equal("工具结果", result);
    }
}
