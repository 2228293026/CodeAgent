using System.Text.Json.Nodes;
using CodeAgent.Tools;

namespace CodeAgent;

/// <summary>钩子触发时机（学自 Claude Code 的 hooks）。</summary>
public enum HookEvent
{
    /// <summary>工具执行**之前**：退出码 2 可以否决这次调用。</summary>
    PreToolUse,

    /// <summary>工具执行**之后**：可以看到结果，退出码 2 把 stderr 作为反馈附给模型。</summary>
    PostToolUse,
}

/// <summary>一条已校验的钩子。</summary>
public sealed record HookDefinition(HookEvent Event, string Matcher, string Command, int TimeoutSeconds);

/// <summary>一次钩子执行的结果。</summary>
public sealed record HookRun(HookDefinition Hook, int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    /// <summary>退出码 2 = 明确否决（与 Claude Code 的约定一致）。
    /// 用 2 而不是「非 0」：脚本因为环境问题退出 1 时不该把工具调用整个拦掉。</summary>
    public bool Blocks => ExitCode == 2;

    /// <summary>给人/给模型看的说明。stderr 优先——它就是钩子约定的「说理」通道。</summary>
    public string Reason =>
        TimedOut ? $"钩子超时（{Hook.TimeoutSeconds}s）"
        : StdErr.Length > 0 ? StdErr
        : StdOut.Length > 0 ? StdOut
        : $"退出码 {ExitCode}";
}

/// <summary>
/// 工具钩子：在 <c>ToolRegistry.ExecuteAsync</c> 这个**唯一**出口前后跑用户脚本。
///
/// 为什么挂在注册表而不是各个工具里：231 个工具，任何一个漏掉都是一条静默的绕过路径；
/// 而注册表是所有工具调用（含未来新增的）必经之地。
///
/// 契约（写进 README，也写在 /hooks 里）：
///   · 命令经 <c>config.shell</c> 执行，工作目录 = 工作区根；
///   · 事件与工具名通过环境变量 CODEAGENT_HOOK_EVENT / CODEAGENT_HOOK_TOOL 传入；
///   · 完整载荷（event/tool/arguments/result/cwd）以 JSON 经 **stdin** 传入；
///   · 退出码 0 = 通过；2 = 否决（stderr 作为原因）；其它 = 失败但不拦（附一行提示）。
///
/// 刻意不做「钩子输出默认并进模型上下文」：钩子脚本的 echo 五花八门，
/// 默认拼进去等于让配置里一行命令悄悄改写每轮上下文。只有退出码 2 的 stderr 才进。
/// </summary>
public static class Hooks
{
    /// <summary>触发时机的字符串值（唯一事实来源：配置校验、/hooks 展示共用）。</summary>
    public static readonly string[] EventNames = ["preToolUse", "postToolUse"];

    public const string EventEnvVar = "CODEAGENT_HOOK_EVENT";
    public const string ToolEnvVar = "CODEAGENT_HOOK_TOOL";

    /// <summary>解析配置里的触发时机；无法识别时返回 null（配置加载阶段已警告）。</summary>
    public static HookEvent? ParseEvent(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pretooluse" => HookEvent.PreToolUse,
        "posttooluse" => HookEvent.PostToolUse,
        _ => null,
    };

    /// <summary>工具名匹配：<c>*</c> 匹配全部，其余按名字精确匹配（不分大小写）。</summary>
    public static bool Matches(string matcher, string toolName) =>
        matcher is "*" or "" || string.Equals(matcher, toolName, StringComparison.OrdinalIgnoreCase);

    /// <summary>把配置项转成可执行的钩子；非法项跳过（配置加载时已经逐条警告过，不重复）。</summary>
    public static IReadOnlyList<HookDefinition> Parse(IEnumerable<HookConfig>? configs)
    {
        var list = new List<HookDefinition>();
        if (configs is null)
            return list;
        foreach (var cfg in configs)
        {
            if (cfg is null)
                continue;
            var ev = ParseEvent(cfg.Event);
            if (ev is null || string.IsNullOrWhiteSpace(cfg.Command))
                continue;
            var matcher = string.IsNullOrWhiteSpace(cfg.Matcher) ? "*" : cfg.Matcher.Trim();
            list.Add(new HookDefinition(ev.Value, matcher, cfg.Command.Trim(), Math.Clamp(cfg.TimeoutSeconds, 1, 300)));
        }
        return list;
    }

    /// <summary>单个字符串进载荷的上限。钩子要判断的是「这次写哪个文件、写得对不对」，
    /// 不是把整段文件内容搬一遍——一个 write_file 的 content 动辄几百 KB，
    /// 每调用一次工具就搬一遍纯属浪费，也让管道缓冲成为隐患。</summary>
    public const int MaxPayloadStringChars = 10_000;

    private const string TruncationMark = "…（已截断）";

    /// <summary>构造传给钩子的 JSON 载荷。result 为 null 表示「工具还没跑」（pre 阶段）。</summary>
    public static string BuildPayload(HookEvent ev, string toolName, JsonObject? args, string? result, string cwd)
    {
        // 深拷贝一份再截断：把工具自己的 JsonObject 交给序列化，之后任何就地修改都会影响工具，
        // 而截断**必须只发生在副本上**——截了工具的参数就等于把它要写的内容改坏了。
        var argsCopy = args?.DeepClone() as JsonObject ?? new JsonObject();
        TruncateStrings(argsCopy);
        var payload = new JsonObject
        {
            ["event"] = ev == HookEvent.PreToolUse ? EventNames[0] : EventNames[1],
            ["tool"] = toolName,
            ["arguments"] = argsCopy,
            ["cwd"] = cwd,
        };
        if (result is not null)
            payload["result"] = result.Length > MaxPayloadStringChars
                ? result[..MaxPayloadStringChars] + TruncationMark
                : result;
        return payload.ToJsonString();
    }

    /// <summary>把超长的字符串值就地截断（递归处理嵌套对象/数组）。</summary>
    private static void TruncateStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (obj[key] is JsonValue v)
                    {
                        if (v.TryGetValue<string>(out var s) && s.Length > MaxPayloadStringChars)
                            obj[key] = s[..MaxPayloadStringChars] + TruncationMark;
                        continue;
                    }
                    TruncateStrings(obj[key]);
                }
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v)
                    {
                        if (v.TryGetValue<string>(out var s) && s.Length > MaxPayloadStringChars)
                            arr[i] = s[..MaxPayloadStringChars] + TruncationMark;
                        continue;
                    }
                    TruncateStrings(arr[i]);
                }
                break;
        }
    }

    /// <summary>跑一遍某个时机的全部匹配钩子，按配置顺序，收集结果（不在这里做「拦不拦」的决策）。</summary>
    public static async Task<IReadOnlyList<HookRun>> RunAsync(
        HookEvent ev, string toolName, JsonObject? args, string? result, AgentContext ctx, CancellationToken ct)
    {
        var definitions = Parse(ctx.Config.Hooks);
        var runs = new List<HookRun>();
        if (definitions.Count == 0)
            return runs;

        var shell = string.IsNullOrWhiteSpace(ctx.Config.Shell) ? ShellRunner.AutoShell() : ctx.Config.Shell;
        var cwd = ctx.Workspace.Root;
        var payload = BuildPayload(ev, toolName, args, result, cwd);

        foreach (var hook in definitions)
        {
            if (hook.Event != ev || !Matches(hook.Matcher, toolName))
                continue;

            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventEnvVar] = ev == HookEvent.PreToolUse ? EventNames[0] : EventNames[1],
                [ToolEnvVar] = toolName,
            };
            try
            {
                var captured = await ShellRunner.CaptureAsync(shell, hook.Command, cwd, hook.TimeoutSeconds, payload, env, ct);
                runs.Add(new HookRun(hook, captured.ExitCode, captured.StdOut, captured.StdErr, captured.TimedOut));
            }
            catch (OperationCanceledException)
            {
                throw; // 用户取消整轮：不该被当成「钩子失败」继续往下跑
            }
            catch (Exception ex)
            {
                // 钩子命令本身起不来（shell 不存在等）：记成一次失败运行，不拦工具。
                // 拦下来的话，一个配错的钩子会让**所有**工具永久失效，用户还看不出为什么。
                runs.Add(new HookRun(hook, -1, string.Empty, $"钩子无法执行: {ex.Message}", TimedOut: false));
            }
        }
        return runs;
    }
}
