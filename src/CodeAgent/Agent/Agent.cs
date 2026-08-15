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
    private readonly List<ProviderMessage> _messages = [];
    private readonly StreamWriter? _sessionLog;

    public Agent(AgentConfig config, IAgentProvider provider, ToolRegistry tools)
    {
        _provider = provider;
        _tools = tools;
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

    /// <summary>本轮会话的 Provider 调用次数与 token 用量统计（/stats 用）。</summary>
    public int ProviderCalls { get; private set; }
    public long TotalInputTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }

    /// <summary>切换 Provider（/model 命令用）。</summary>
    public void SetProvider(IAgentProvider provider) => _provider = provider;

    /// <summary>清空对话历史（保留系统提示），/clear 命令用。</summary>
    public void Reset()
    {
        _messages.Clear();
        _messages.Add(new ProviderMessage { Role = MessageRole.System, Content = _ctx.Config.SystemPrompt });
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
    }

    /// <summary>把当前对话（或指定命名会话）导出为 Markdown 记录，返回文件路径。</summary>
    public string ExportMarkdown(string? name)
    {
        IReadOnlyList<ProviderMessage> msgs = name is null ? _messages : LoadMessages(name);
        var dir = Path.Combine(Environment.CurrentDirectory, ".codeagent", "exports");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, (name ?? $"chat-{DateTime.Now:yyyyMMdd-HHmmss}") + ".md");

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

    private string SessionFilePath(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        var safe = sb.Length == 0 ? "session" : sb.ToString();
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
        _messages.Add(new ProviderMessage { Role = MessageRole.User, Content = userPrompt });
        LogMessage(_messages[^1]);

        for (int i = 0; i < _ctx.Config.MaxToolIterations; i++)
        {
            var resp = await CallProviderAsync(ct);
            ProviderCalls++;
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
            TrimHistory();

            // 无工具调用：模型给出最终答复
            if (resp.ToolCalls.Count == 0)
            {
                _ctx.StopRequested = false;
                StreamedLastRun = _streamedThisCall;
                return resp.Text ?? "(模型未返回内容)";
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
            TrimHistory();

            if (_ctx.StopRequested)
                return "⏹ 已按 stop 工具请求结束本轮任务。";
        }

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
                var resp = await _provider.ChatAsync(_messages, _tools.ToToolSpecs(), ct);
                ClearSpinner();
                return resp;
            }

            // 流式：文本增量实时打印到控制台，首次增量到达时先清掉思考指示器
            var result = await _provider.ChatStreamAsync(_messages, _tools.ToToolSpecs(), delta =>
            {
                if (!_streamedThisCall)
                {
                    ClearSpinner();
                    _streamedThisCall = true;
                }
                Console.Write(delta);
            }, ct);

            if (!_streamedThisCall)
                ClearSpinner();
            return result;
        }
        catch
        {
            ClearSpinner();
            throw;
        }
    }

    private void ShowSpinner() => Console.Write("\r⏳ 思考中…");

    private void ClearSpinner() => Console.Write("\r" + new string(' ', 24) + "\r");

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
        var summary = SummarizeCall(tc.Name, tc.ArgumentsJson);
        var showLog = _ctx.Config.ShowToolCalls;

        if (showLog)
        {
            lock (ConsoleLock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  🔧 {summary} …");
                Console.ResetColor();
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
                var status = $"  {(isError ? "⚠" : "✔")} {summary} ({sw.Elapsed.TotalSeconds:F1}s)";
                if (isError)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(status);
                    if (output.Length > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("      " + output);
                    }
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine(status);
                    Console.ResetColor();
                    // run_command 附带输出预览，方便直接看到构建/测试结果
                    if (tc.Name == "run_command" && output.Length > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        var preview = string.Join('\n', output.Split('\n').Take(8));
                        Console.WriteLine("      " + TextUtil.Truncate(preview, 800));
                        Console.ResetColor();
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

    /// <summary>历史超限时先截短工具结果，仍超限则丢弃最早的对话对（保留系统提示与首条用户消息）。</summary>
    private void TrimHistory()
    {
        var limit = _ctx.Config.MaxHistoryChars;
        long total = 0;
        foreach (var m in _messages)
            total += m.Content?.Length ?? 0;
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

        // 2) 仍超限：丢弃最早的非锚定消息对（system 与首条 user 保留）
        while (total > limit && _messages.Count > 4)
        {
            total -= (_messages[2].Content?.Length ?? 0) + (_messages[3].Content?.Length ?? 0);
            _messages.RemoveAt(2);
            _messages.RemoveAt(2);
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
