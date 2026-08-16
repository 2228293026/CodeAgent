using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAgent.Providers;
using CodeAgent.Tools;

namespace CodeAgent.Agent;

/// <summary>
/// Agent 主循环：维护对话历史，调用 Provider；若返回工具调用则执行并把结果回填，
/// 循环直到模型给出最终答复、调用 stop 工具或达到最大轮数。
/// </summary>
public sealed class Agent
{
    private IAgentProvider _provider;
    private readonly ToolRegistry _tools;
    private readonly AgentContext _ctx;
    private readonly AgentConfig _config;
    private readonly List<ProviderMessage> _messages = [];
    private readonly StreamWriter? _sessionLog;

    public Agent(AgentConfig config, IAgentProvider provider, ToolRegistry tools)
    {
        _provider = provider;
        _tools = tools;
        _config = config;
        _ctx = new AgentContext
        {
            Config = config,
            Workspace = new Workspace(Environment.CurrentDirectory),
        };
        _messages.Add(new ProviderMessage { Role = MessageRole.System, Content = config.SystemPrompt });

        if (config.SaveSessions)
        {
            try
            {
                var dir = Path.Combine(Environment.CurrentDirectory, config.SessionDir);
                Directory.CreateDirectory(dir);
                SessionPath = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
                _sessionLog = new StreamWriter(SessionPath, append: true) { AutoFlush = true };
            }
            catch
            {
                _sessionLog = null;
            }
        }
    }

    public AgentContext Context => _ctx;
    public string? SessionPath { get; }

    /// <summary>本轮运行是否已把最终答复流式打印到控制台（Program 据此避免重复打印）。</summary>
    public bool StreamedLastRun { get; private set; }

    private bool _streamedThisCall;

    /// <summary>最近一次用户请求文本（/retry 用）。</summary>
    public string? LastPrompt { get; private set; }

    /// <summary>当前工作模式。</summary>
    public AgentMode CurrentMode { get; private set; } = Modes.All[0];

    private ConsoleRenderer? _renderer;

    /// <summary>本轮会话的 Provider 调用次数与 token 用量统计（/stats 用）。</summary>
    public int ProviderCalls { get; private set; }
    public long TotalInputTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }

    // 单轮统计（回合结束后用于摘要行）
    public int TurnRounds { get; private set; }
    public int TurnToolCalls { get; private set; }
    public long TurnInputTokens { get; private set; }
    public long TurnOutputTokens { get; private set; }
    public long TurnCachedTokens { get; private set; }
    public int LastTurnStartCount { get; private set; }
    public int MessageCount => _messages.Count;

    /// <summary>当前对话消息列表（只读，/history 与 /load 显示用）。</summary>
    public IReadOnlyList<ProviderMessage> Messages => _messages;

    /// <summary>上一轮是否失败（模型空回复）——REPL 红色提示 / 一次性模式非零退出码。</summary>
    public bool LastTurnFailed { get; private set; }

    /// <summary>切换 Provider（/model 命令用）。</summary>
    public void SetProvider(IAgentProvider provider) => _provider = provider;

    /// <summary>清空对话历史（保留系统提示），/clear 命令用。</summary>
    public void Reset()
    {
        _messages.Clear();
        _messages.Add(new ProviderMessage { Role = MessageRole.System, Content = EffectivePrompt(CurrentMode) });
        // 清空后没有「上一轮」可撤回：重置起点，避免 ESC 撤回按过期索引误删
        LastTurnStartCount = _messages.Count;
    }

    /// <summary>切换工作模式：替换系统提示并限制可用工具。</summary>
    public void SetMode(AgentMode mode)
    {
        CurrentMode = mode;
        if (_messages.Count > 0 && _messages[0].Role == MessageRole.System)
            _messages[0] = new ProviderMessage { Role = MessageRole.System, Content = EffectivePrompt(mode) };
    }

    /// <summary>code 模式使用配置的自定义 systemPrompt；其他模式用模式自身的提示词。</summary>
    private string EffectivePrompt(AgentMode mode) =>
        mode.Name == "code" ? _config.SystemPrompt : mode.SystemPrompt;

    /// <summary>撤回最后一轮：移除最后一条用户消息及其回复，恢复发送前状态（ESC 撤回）。</summary>
    public string? UndoLastTurn()
    {
        if (LastTurnStartCount <= 0 || LastTurnStartCount >= _messages.Count)
            return null;
        _messages.RemoveRange(LastTurnStartCount, _messages.Count - LastTurnStartCount);
        return $"⏪ 已撤回最后一轮（剩余 {LastTurnStartCount} 条历史消息）。";
    }

    public void Close()
    {
        try { _sessionLog?.Flush(); _sessionLog?.Dispose(); } catch { }
    }

    /// <summary>把当前对话保存为命名会话（.codeagent/sessions/&lt;name&gt;.json）。</summary>
    public void SaveSession(string name)
    {
        var path = SessionFilePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dto = _messages.Select(ToDto).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
    }

    /// <summary>从命名会话恢复对话（替换当前历史）。</summary>
    public void LoadSession(string name)
    {
        _messages.Clear();
        _messages.AddRange(LoadMessages(name));
        // 加载的会话没有「上一轮」可撤回：重置起点，否则 ESC 撤回会按过期索引删掉刚加载的消息
        LastTurnStartCount = _messages.Count;
    }

    /// <summary>把当前对话（或指定命名会话）导出为 Markdown 记录，返回文件路径。</summary>
    public string ExportMarkdown(string? name)
    {
        IReadOnlyList<ProviderMessage> msgs = name is null ? _messages : LoadMessages(name);
        var dir = Path.Combine(Environment.CurrentDirectory, _ctx.Config.ExportDir);
        Directory.CreateDirectory(dir);
        // name 同样需 sanitize：/export ../evil 曾写入 ExportDir 父目录（路径穿越）
        var safeName = name is null ? $"chat-{DateTime.Now:yyyyMMdd-HHmmss}" : SanitizeName(name);
        var file = Path.Combine(dir, safeName + ".md");

        var sb = new StringBuilder();
        sb.AppendLine($"# CodeAgent 会话{(name is null ? "" : $"：{name}")}");
        sb.AppendLine();
        foreach (var m in msgs)
        {
            sb.AppendLine(m.Role switch
            {
                MessageRole.System => "## 系统",
                MessageRole.User => "## 用户",
                MessageRole.Assistant => "## 助手",
                _ => $"## 工具：{m.ToolName}",
            });
            if (m.Role == MessageRole.Assistant && m.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in m.ToolCalls)
                    sb.AppendLine($"- 调用工具 `{tc.Name}`：`{tc.ArgumentsJson}`");
            }
            if (!string.IsNullOrEmpty(m.Content))
                sb.AppendLine();
            sb.AppendLine(m.Content ?? "");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    private List<ProviderMessage> LoadMessages(string name)
    {
        var path = SessionFilePath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"会话不存在: {name}（{path}）");
        var dto = JsonSerializer.Deserialize<List<MessageDto>>(File.ReadAllText(path), JsonOpts)
                  ?? throw new InvalidDataException($"会话文件损坏: {path}");
        return dto.Select(FromDto).ToList();
    }

    /// <summary>会话名/导出名 sanitize：非法文件名字符替换为 _（防目录穿越与非法文件名）。</summary>
    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.Length == 0 ? "session" : sb.ToString();
    }

    private string SessionFilePath(string name)
    {
        var safe = SanitizeName(name);
        return Path.Combine(Environment.CurrentDirectory, _ctx.Config.SessionDir, safe + ".json");
    }

    private static MessageDto ToDto(ProviderMessage m) => new()
    {
        role = m.Role.ToString().ToLowerInvariant(),
        content = m.Content,
        toolCalls = m.ToolCalls?.Select(tc => new ToolCallDto { id = tc.Id, name = tc.Name, arguments = tc.ArgumentsJson }).ToList(),
        toolCallId = m.ToolCallId,
        toolName = m.ToolName,
        isError = m.IsError,
    };

    private static ProviderMessage FromDto(MessageDto d) => new()
    {
        Role = Enum.TryParse<MessageRole>(d.role, true, out var r) ? r : MessageRole.User,
        Content = d.content,
        ToolCalls = d.toolCalls?.Select(tc => new ToolCall { Id = tc.id, Name = tc.name, ArgumentsJson = tc.arguments }).ToList(),
        ToolCallId = d.toolCallId,
        ToolName = d.toolName,
        IsError = d.isError,
    };

    private sealed class MessageDto
    {
        public string role { get; set; } = "";
        public string? content { get; set; }
        public List<ToolCallDto>? toolCalls { get; set; }
        public string? toolCallId { get; set; }
        public string? toolName { get; set; }
        public bool isError { get; set; }
    }

    private sealed class ToolCallDto
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string arguments { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>执行一轮用户请求，返回模型最终答复文本。</summary>
    public async Task<string> RunAsync(string userPrompt, CancellationToken ct)
    {
        _ctx.StopRequested = false;
        StreamedLastRun = false;
        LastPrompt = userPrompt;
        _renderer = new ConsoleRenderer(_ctx.Config.RenderMarkdown);
        TurnRounds = 0;
        TurnToolCalls = 0;
        TurnInputTokens = 0;
        TurnOutputTokens = 0;
        TurnCachedTokens = 0;
        TurnThinkingSeconds = 0;
        LastTurnFailed = false;
        if (!_sessionSw.IsRunning)
            _sessionSw.Start(); // 整个对话的累计计时
        LastTurnStartCount = _messages.Count; // 记录本轮起点（ESC 撤回用）
        _messages.Add(new ProviderMessage { Role = MessageRole.User, Content = userPrompt });
        LogMessage(_messages[^1]);

        for (int i = 0; i < _ctx.Config.MaxToolIterations; i++)
        {
            var resp = await CallProviderAsync(ct);
            if (_streamedThisCall)
                _renderer?.Flush();
            ProviderCalls++;
            TurnRounds++;
            if (resp.InputTokens is int inTokT)
                TurnInputTokens += inTokT;
            if (resp.OutputTokens is int outTokT)
                TurnOutputTokens += outTokT;
            if (resp.CachedTokens is int cTokT)
                TurnCachedTokens += cTokT;
            if (resp.InputTokens is int inTok)
                TotalInputTokens += inTok;
            if (resp.OutputTokens is int outTok)
                TotalOutputTokens += outTok;

            _messages.Add(new ProviderMessage
            {
                Role = MessageRole.Assistant,
                Content = resp.Text,
                ToolCalls = resp.ToolCalls,
            });
            LogMessage(_messages[^1]);
            await TrimHistoryAsync(ct);

            // 无工具调用：模型给出最终答复
            if (resp.ToolCalls.Count == 0)
            {
                _ctx.StopRequested = false;
                StreamedLastRun = _streamedThisCall;
                if (resp.Text is null)
                {
                    LastTurnFailed = true;
                    return "(模型未返回内容：可能是免费模型限额、上下文过长或速率限制，可 /retry 或换模型重试)";
                }
                return resp.Text;
            }

            // 模型在调用工具前若已流式输出文本，补一个换行，避免与后续输出粘连
            if (_streamedThisCall)
                Console.WriteLine();

            // 执行本轮全部工具调用：默认并行；确认模式或同路径写冲突时顺序执行
            List<ProviderMessage> results;
            try
            {
                results = await ExecuteToolCallsAsync(resp.ToolCalls, ct);
            }
            catch (OperationCanceledException)
            {
                // 用户中断：撤掉未完成的 assistant 工具调用轮，保持历史对 Provider 合法
                if (_messages.Count > 0 && _messages[^1] is { Role: MessageRole.Assistant } last && last.ToolCalls is { Count: > 0 })
                    _messages.RemoveAt(_messages.Count - 1);
                throw;
            }
            foreach (var r in results)
            {
                _messages.Add(r);
                LogMessage(r);
            }
            await TrimHistoryAsync(ct);

            if (_ctx.StopRequested)
                return "⏹ 已按 stop 工具请求结束本轮任务。";
        }

        // 达到轮数上限说明任务未完成：标记失败，REPL 显示 ⚠、一次性模式退出码非 0
        LastTurnFailed = true;
        return "⚠ 达到最大工具调用轮数（MaxToolIterations），任务可能未完成。";
    }

    private async Task<ProviderResponse> CallProviderAsync(CancellationToken ct)
    {
        ShowSpinner();
        _streamedThisCall = false;
        try
        {
            if (!_ctx.Config.StreamOutput)
            {
                var resp = await CallWithRetryAsync(
                    () => _provider.ChatAsync(_messages, ToolsForMode(), _ctx.Config.ThinkingEffort, ct), ct);
                ClearSpinner();
                return resp;
            }

            // 流式：文本增量实时打印到控制台，首次增量到达时先清掉思考指示器
            _reasoningShown = false;
            var result = await CallWithRetryAsync(
                () => _provider.ChatStreamAsync(_messages, ToolsForMode(), _ctx.Config.ThinkingEffort, delta =>
                {
                    if (!_streamedThisCall)
                    {
                        // 思考结束（首个文本到达）：定格"用时 · tokens"统计行，结论文本从下一行流式输出
                        FinalizeSpinner();
                        _streamedThisCall = true;
                    }
                    _renderer?.Append(delta);
                    _streamTokens += delta.Length / 4; // 内容 token 计数（spinner 尚在时继续 ↑）
                }, reason =>
                {
                    // 思考内容：实时流式输出（暗色），而非缓冲到最后一次性显示
                    if (!_reasoningShown)
                    {
                        ClearSpinner(); // 思考内容开始显示：清掉 spinner 行
                        _reasoningShown = true;
                    }
                    _streamTokens += reason.Length / 4; // 估算已生成 token（spinner ↑ 显示）
                    lock (ConsoleLock)
                    {
                        SafeColor.Foreground(ConsoleColor.DarkGray);
                        Console.Write(reason);
                        SafeColor.Reset();
                    }
                }, frag =>
                {
                    _streamTokens += frag.Length / 4; // 工具调用参数也计入 ↑ tokens
                }, ct), ct);

            if (!_streamedThisCall && !_reasoningShown)
                ClearSpinner();
            return result;
        }
        catch
        {
            ClearSpinner();
            throw;
        }
    }

    /// <summary>
    /// 对可重试的 Provider 错误（429 / 5xx / 连接失败）自动重试，指数退避。
    /// 流式已输出过文本时不重试（避免重复输出）；取消令牌生效时立即中止。
    /// </summary>
    private async Task<ProviderResponse> CallWithRetryAsync(Func<Task<ProviderResponse>> call, CancellationToken ct)
    {
        const int maxRetries = 2;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (ProviderException ex) when (ex.Retryable && attempt < maxRetries && !_streamedThisCall)
            {
                var delay = 2 * (attempt + 1);
                ClearSpinner();
                Console.WriteLine($"⚠ 请求失败（{DescribeFailure(ex)}），{delay}s 后重试…");
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
        }
    }

    private static string DescribeFailure(ProviderException ex) =>
        ex.StatusCode is int code ? $"HTTP {code}" : "网络错误";

    /// <summary>当前模式下暴露给模型的工具（按模式过滤）。</summary>
    public IReadOnlyList<ToolSpec> ToolsForMode()
    {
        var all = _tools.ToToolSpecs();
        if (CurrentMode.AllowedTools is not { } allowed)
            return all;
        var set = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        return all.Where(t => set.Contains(t.Name)).ToList();
    }

    private System.Threading.CancellationTokenSource? _spinnerCts;
    private readonly System.Diagnostics.Stopwatch _spinnerSw = new();
    private readonly System.Diagnostics.Stopwatch _sessionSw = new(); // 整个对话的累计用时
    private bool _reasoningShown; // 本轮是否已开始实时输出思考内容（首段到达时清掉 spinner）
    private long _streamTokens; // 当前调用已流式生成的 token 估算（字符数/4）
    private static readonly string[] SpinnerFrames = ["⠦", "⠸", "⠼", "⠴", "⠦", "⠇"];

    /// <summary>本轮模型产出首个输出前的耗时（思考时间，秒）。</summary>
    public double TurnThinkingSeconds { get; private set; }

    /// <summary>思考计时器：动画帧 + 整个对话的累计时间 + ↑ 累计 tokens。
    /// 先换到输入行下方独立一行（spinner 专属行），\r 只更新该行，不覆盖输入框。</summary>
    private void ShowSpinner()
    {
        _spinnerSw.Restart();
        _streamTokens = 0;
        _spinnerCts = new System.Threading.CancellationTokenSource();
        var cts = _spinnerCts;
        var frame = 0;
        Console.WriteLine(); // 输入行下方新行：spinner 独立显示，避免与输入框/输出混在一起
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var f = SpinnerFrames[frame++ % SpinnerFrames.Length];
                    var total = TotalInputTokens + TotalOutputTokens + _streamTokens;
                    var tok = total >= 1000 ? $"{total / 1000.0:F1}K" : total.ToString();
                    // 显示实际用时与 token（而非"思考中"），实时更新
                    lock (ConsoleLock)
                        Console.Write($"\r{f} 用时 {TextUtil.FormatSessionTime(_sessionSw.Elapsed)} · ↑ {tok} tokens");
                    await Task.Delay(120, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* 忽略 */ }
        });
    }

    private void ClearSpinner()
    {
        _spinnerCts?.Cancel();
        TurnThinkingSeconds = _spinnerSw.Elapsed.TotalSeconds;
        // 清空 spinner 行并回到行首：不留常驻文本（思考时长由回合摘要行显示），
        // 流式输出/工具日志从该行继续，输入行保持在上方
        Console.Write("\r" + new string(' ', 60) + "\r");
    }

    /// <summary>思考结束（首个文本到达）：把 spinner 行定格为「用时 X · ↑ tokens」统计行并换行。
    /// 用户要求思考结束后仍实时看到用时与 token，而不是 spinner 直接消失。</summary>
    private void FinalizeSpinner()
    {
        _spinnerCts?.Cancel();
        TurnThinkingSeconds = _spinnerSw.Elapsed.TotalSeconds;
        var total = TotalInputTokens + TotalOutputTokens + _streamTokens;
        var tok = total >= 1000 ? $"{total / 1000.0:F1}K" : total.ToString();
        if (!_reasoningShown)
        {
            // spinner 动画行还在：先清掉再定格（避免残留帧字符）
            Console.Write("\r" + new string(' ', 60) + "\r");
        }
        // 定格统计行并换行：思考结束后的用时与 token 可见，结论文本从下一行流式输出
        Console.WriteLine($"✓ 用时 {TextUtil.FormatSessionTime(_sessionSw.Elapsed)} · ↑ {tok} tokens");
    }

    /// <summary>把工具名与参数压缩为一行展示文本（跳过 content 等大字段）。</summary>
    private static string SummarizeCall(string name, string argsJson)
    {
        JsonObject? args;
        try
        {
            args = JsonNode.Parse(argsJson) as JsonObject;
        }
        catch (JsonException)
        {
            args = null;
        }

        if (args is null || args.Count == 0)
            return name;

        var parts = new List<string>();
        foreach (var kv in args)
        {
            if (kv.Key == "content")
                continue;
            var v = (kv.Value?.ToJsonString() ?? "").Trim('"');
            if (v.Length > 60)
                v = v[..60] + "…";
            parts.Add($"{kv.Key}={v}");
        }
        return parts.Count == 0 ? name : $"{name}({string.Join(" ", parts)})";
    }

    /// <summary>控制台输出锁：并行工具调用时防止进度日志交错。</summary>
    private static readonly object ConsoleLock = new();

    /// <summary>并行执行一批工具调用；确认模式或同路径写冲突时退化为顺序执行。</summary>
    private async Task<List<ProviderMessage>> ExecuteToolCallsAsync(IReadOnlyList<ToolCall> calls, CancellationToken ct)
    {
        var results = new List<ProviderMessage>(calls.Count);

        // 确认模式下逐个确认命令，输入会串扰，必须顺序执行；同路径写操作并发会互相覆盖，也顺序执行
        var conflict = _ctx.Config.ConfirmCommands;
        if (!conflict)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tc in calls)
            {
                if (tc.Name is "write_file" or "edit_file")
                {
                    var p = ExtractPath(tc.ArgumentsJson);
                    if (p.Length > 0 && !paths.Add(p))
                    {
                        conflict = true;
                        break;
                    }
                }
            }
        }

        if (conflict || calls.Count <= 1)
        {
            foreach (var tc in calls)
            {
                results.Add(await ExecuteToolCallAsync(tc, ct));
                if (_ctx.StopRequested)
                    break;
            }
            return results;
        }

        using var sem = new SemaphoreSlim(8);
        var tasks = calls.Select(async tc =>
        {
            await sem.WaitAsync(ct);
            try
            {
                return await ExecuteToolCallAsync(tc, ct);
            }
            finally
            {
                sem.Release();
            }
        });
        results.AddRange(await Task.WhenAll(tasks));
        return results;
    }

    private async Task<ProviderMessage> ExecuteToolCallAsync(ToolCall tc, CancellationToken ct)
    {
        // 模式限制：只读模式下拦截写操作（防御性，正常情况模型看不到这些工具）
        if (CurrentMode.AllowedTools is { } allowed && !allowed.Contains(tc.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new ProviderMessage
            {
                Role = MessageRole.Tool,
                ToolCallId = tc.Id,
                ToolName = tc.Name,
                Content = $"工具 {tc.Name} 在当前模式（{CurrentMode.Name}）下不可用。",
                IsError = true,
            };
        }

        TurnToolCalls++;
        var summary = SummarizeCall(tc.Name, tc.ArgumentsJson);
        var showLog = _ctx.Config.ShowToolCalls;

        if (showLog)
        {
            lock (ConsoleLock)
            {
                SafeColor.Foreground(ConsoleColor.DarkGray);
                Console.WriteLine($"  🔧 {summary} …");
                SafeColor.Reset();
            }
        }
        var sw = Stopwatch.StartNew();
        string output;
        bool isError;
        try
        {
            output = await _tools.ExecuteAsync(tc.Name, tc.ArgumentsJson, _ctx, ct);
            isError = false;
        }
        catch (ToolException ex)
        {
            output = $"工具错误: {ex.Message}";
            isError = true;
        }
        catch (Exception ex)
        {
            output = $"工具异常: {ex.GetType().Name}: {ex.Message}";
            isError = true;
        }
        sw.Stop();
        output = TextUtil.Truncate(output, 24_000);

        if (showLog)
        {
            lock (ConsoleLock)
            {
                var status = $"  {(isError ? "⚠" : "✔")} {summary} ({TextUtil.FormatDuration(sw.Elapsed)})";
                if (isError)
                {
                    SafeColor.Foreground(ConsoleColor.Red);
                    Console.WriteLine(status);
                    if (output.Length > 0)
                    {
                        SafeColor.Foreground(ConsoleColor.Yellow);
                        Console.WriteLine("      " + output);
                    }
                    SafeColor.Reset();
                }
                else
                {
                    SafeColor.Foreground(ConsoleColor.Green);
                    Console.WriteLine(status);
                    SafeColor.Reset();
                    // 命令类工具附带输出预览，方便直接看到构建/测试结果
                    if (tc.Name is "run_command" or "bash" or "powershell" && output.Length > 0)
                    {
                        SafeColor.Foreground(ConsoleColor.DarkGray);
                        var preview = string.Join('\n', output.Split('\n').Take(8));
                        Console.WriteLine("      " + TextUtil.Truncate(preview, 800));
                        SafeColor.Reset();
                    }
                }
            }
        }

        return new ProviderMessage
        {
            Role = MessageRole.Tool,
            ToolCallId = tc.Id,
            ToolName = tc.Name,
            Content = output,
            IsError = isError,
        };
    }

    /// <summary>从工具参数 JSON 中提取目标文件路径（用于检测同路径写冲突）。</summary>
    private static string ExtractPath(string argsJson)
    {
        try
        {
            return JsonNode.Parse(argsJson)?["path"]?.GetValue<string>() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 历史超限时：先截短工具结果，仍超限则尝试用 LLM 压缩最早对话；
    /// 压缩不可用时回退为丢弃最早的非锚定消息对（保留系统提示与首条用户消息）。
    /// </summary>
    private async Task TrimHistoryAsync(CancellationToken ct)
    {
        var limit = _ctx.Config.MaxHistoryChars;
        long total = 0;
        foreach (var m in _messages)
        {
            total += m.Content?.Length ?? 0;
            // 工具调用的参数字符串（如 write_file 的大段 content）也计入，否则历史可能远超上限
            if (m.ToolCalls is not null)
                foreach (var tc in m.ToolCalls)
                    total += tc.ArgumentsJson.Length;
        }
        if (total <= limit)
            return;

        // 1) 截短超长的工具结果
        for (int pass = 0; pass < 3 && total > limit; pass++)
        {
            for (int i = 1; i < _messages.Count && total > limit; i++)
            {
                var m = _messages[i];
                if (m.Role != MessageRole.Tool || (m.Content?.Length ?? 0) <= 1000)
                    continue;
                var keep = Math.Max(300, m.Content!.Length / 2);
                total -= m.Content.Length - keep;
                _messages[i] = new ProviderMessage
                {
                    Role = m.Role,
                    ToolCallId = m.ToolCallId,
                    ToolName = m.ToolName,
                    Content = m.Content[..keep] + "\n…[历史消息已裁剪]",
                    IsError = m.IsError,
                };
            }
        }

        // 2) 仍超限：尝试 LLM 压缩最早对话
        if (total > limit && _messages.Count > 6)
        {
            if (await TrySummarizeAsync(ct))
                return;
        }

        // 3) 兜底：按「完整回复块」丢弃最早的非锚定对话（system 与首条 user 保留）。
        //    整块移除 assistant 及其全部 tool 结果，避免拆散工具调用与结果
        //    （否则留下「孤儿」tool 结果，OpenAI/Anthropic 会拒绝请求）。
        while (total > limit && _messages.Count > 4)
        {
            // 最后一条若是「待执行工具调用的 assistant」（结果尚未回填），不可删除
            var pending = _messages[^1] is { Role: MessageRole.Assistant, ToolCalls.Count: > 0 };

            // 定位首条 user 消息（锚点，之后的消息才可移除）
            int u1 = 1;
            while (u1 < _messages.Count && _messages[u1].Role != MessageRole.User)
                u1++;
            if (u1 >= _messages.Count - 1)
                break; // 没有可移除的回复块

            // 下一条 user（或结尾，但不越过待执行消息）之前的区间 [u1+1, u2) 是 u1 的完整回复块
            int u2 = u1 + 1;
            while (u2 < _messages.Count && _messages[u2].Role != MessageRole.User)
                u2++;
            if (pending && u2 > _messages.Count - 1)
                u2 = _messages.Count - 1; // 保留待执行消息
            var removeCount = Math.Min(u2 - u1 - 1, _messages.Count - 4);
            if (removeCount <= 0)
                break;
            for (int i = 0; i < removeCount; i++)
            {
                total -= _messages[u1 + 1].Content?.Length ?? 0;
                // 被移除消息在本轮起点之前时，撤回索引需同步前移（ESC 撤回按 LastTurnStartCount 定位）
                if (u1 + 1 < LastTurnStartCount)
                    LastTurnStartCount--;
                _messages.RemoveAt(u1 + 1);
            }
        }
    }

    /// <summary>把最早的一部分对话交给 LLM 压缩成摘要；成功返回 true。</summary>
    private async Task<bool> TrySummarizeAsync(CancellationToken ct)
    {
        var keepFrom = Math.Max(2, _messages.Count * 2 / 3);

        // 避免把 tool_calls 与它的结果截断：分界前一条是带工具调用的 assistant 时，把它留在保留区
        while (keepFrom > 2 && _messages[keepFrom - 1] is { Role: MessageRole.Assistant } prev && prev.ToolCalls is { Count: > 0 })
            keepFrom--;

        var chunk = _messages.GetRange(1, keepFrom - 1);
        if (chunk.Count < 3)
            return false;

        var payload = string.Join("\n", chunk.Select(m =>
        {
            var body = m.Content ?? "";
            if (m.Role == MessageRole.Tool)
                body = $"[工具 {m.ToolName}] {body}";
            return $"<{m.Role.ToString().ToLowerInvariant()}> {body}";
        }));

        var prompt = new ProviderMessage
        {
            Role = MessageRole.User,
            Content = "请把下面的对话历史压缩成一份精炼的中文摘要（保留：用户需求、已做的文件改动、工具执行结论、未完成事项）。只输出摘要正文，不要任何前缀。\n\n" +
                      TextUtil.Truncate(payload, 60_000),
        };

        try
        {
            Console.WriteLine("📝 上下文超限，正在压缩历史…");
            var resp = await CallWithRetryAsync(() => _provider.ChatAsync([prompt], [], _ctx.Config.ThinkingEffort, ct), ct);
            var summary = resp.Text?.Trim();
            if (string.IsNullOrWhiteSpace(summary))
                return false;

            _messages.RemoveRange(1, keepFrom - 1);
            // 摘要移除最早消息后索引前移：撤回起点（ESC 用）同步前移，避免定位到错误消息
            LastTurnStartCount = Math.Max(1, LastTurnStartCount - (keepFrom - 1));
            _messages.Insert(1, new ProviderMessage
            {
                Role = MessageRole.System,
                Content = $"【历史摘要】{summary}",
            });
            Console.WriteLine("✔ 历史已压缩，继续执行。");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void LogMessage(ProviderMessage m)
    {
        if (_sessionLog is null)
            return;
        try
        {
            var entry = new
            {
                ts = DateTime.Now.ToString("HH:mm:ss"),
                role = m.Role.ToString().ToLowerInvariant(),
                tool = m.ToolName,
                toolCallId = m.ToolCallId,
                content = m.Content,
                toolCalls = m.ToolCalls?.Select(tc => new { tc.Id, tc.Name, tc.ArgumentsJson }).ToList(),
                error = m.IsError,
            };
            _sessionLog.WriteLine(JsonSerializer.Serialize(entry));
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
