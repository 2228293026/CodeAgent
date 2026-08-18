using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAgent.Providers;

namespace CodeAgent.Agent;

/// <summary>Agent 的会话持久化部分：jsonl 逐条日志、命名快照、/export 导出（partial 拆分）。</summary>
public sealed partial class Agent
 {
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
        // 加载的会话没有「上一轮」可撤回：清空起点栈，否则 ESC 撤回会按过期索引删掉刚加载的消息
        _turnStarts.Clear();
        LastInputTokens = 0; // 上下文变为加载的历史：退回估算口径
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
                {
                    // 参数可能携带大段 content（write_file）：截断预览，导出文件不被单个调用撑爆
                    var argsPreview = tc.ArgumentsJson.Length > 200 ? TextUtil.TruncateLine(tc.ArgumentsJson, 200) : tc.ArgumentsJson;
                    sb.AppendLine($"- 调用工具 `{tc.Name}`：`{argsPreview}`");
                }
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

    /// <summary>从会话日志（.jsonl，每条消息自动写入）恢复对话：--continue / /resume 用。
    /// 恢复后滚动新日志并把已恢复的消息写进去，新日志自包含（可再次被恢复）；
    /// 开头的 system 若为日志旧值，会由随后的 SetMode 换成当前模式提示。</summary>
    public bool LoadSessionLog(string path)
    {
        if (!File.Exists(path))
            return false;
        var msgs = new List<ProviderMessage>();
        try
        {
            foreach (var line in ReadLogLines(path))
            {
                var m = ParseLogLine(line);
                if (m is not null)
                    msgs.Add(m);
            }
        }
        catch (IOException)
        {
            return false;
        }
        // 丢弃末尾未完成的工具轮（ESC 取消/进程中断时，assistant(toolCalls) 已写日志、
        // 工具结果没写全）：带着孤儿 tool_calls 恢复会让下次请求被 API 拒绝（400）。
        // 从尾向前删掉 tool 结果与带 toolCalls 的 assistant，停在完整边界
        while (msgs.Count > 0 &&
               (msgs[^1].Role == MessageRole.Tool ||
                msgs[^1] is { Role: MessageRole.Assistant, ToolCalls.Count: > 0 }))
            msgs.RemoveAt(msgs.Count - 1);
        if (msgs.Count == 0)
            return false;

        _messages.Clear();
        _messages.AddRange(msgs);
        if (_messages[0].Role != MessageRole.System)
            _messages.Insert(0, new ProviderMessage { Role = MessageRole.System, Content = _config.SystemPrompt });
        _turnStarts.Clear(); // 恢复的会话没有「上一轮」可撤回
        LastInputTokens = 0; // 上下文变为恢复的历史：退回估算口径

        RollSessionLog();
        foreach (var m in _messages)
            LogMessage(m);
        return true;
    }

    /// <summary>解析一行会话日志（与 LogMessage 写出的字段对应）；损坏行返回 null 跳过。</summary>
    private static ProviderMessage? ParseLogLine(string line)
    {
        try
        {
            var n = JsonNode.Parse(line) as JsonObject;
            if (n is null || !Enum.TryParse(n["role"]?.GetValue<string>(), true, out MessageRole role))
                return null;
            return new ProviderMessage
            {
                Role = role,
                Content = n["content"]?.GetValue<string>(),
                ToolCalls = (n["toolCalls"] as JsonArray)?.Select(tc => new ToolCall
                {
                    Id = tc?["Id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                    Name = tc?["Name"]?.GetValue<string>() ?? "unknown",
                    ArgumentsJson = tc?["ArgumentsJson"]?.GetValue<string>() ?? "{}",
                }).ToList(),
                ToolCallId = n["toolCallId"]?.GetValue<string>(),
                ToolName = n["tool"]?.GetValue<string>(),
                IsError = n["error"]?.GetValue<bool>() ?? false,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读会话日志行。用 FileShare.ReadWrite 打开：日志文件可能正被本进程的
    /// StreamWriter 追加持有，File.ReadLines 的 FileShare.Read 会与之共享冲突。</summary>
    private static IEnumerable<string> ReadLogLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) is not null)
            yield return line;
    }

    /// <summary>生成不重名的会话日志路径（同一秒内多次滚动时追加 -2/-3 序号）。</summary>
    private string NewSessionLogPath(string dir)
    {
        var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}";
        var path = Path.Combine(dir, stamp + ".jsonl");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{stamp}-{i}.jsonl");
        return path;
    }

    /// <summary>切换到新的会话日志文件（/clear 与恢复会话后用，使日志与新历史一一对应）。</summary>
    private void RollSessionLog()
    {
        if (_sessionLog is null)
            return;
        try { _sessionLog.Dispose(); } catch { /* 忽略 */ }
        try
        {
            var dir = Path.Combine(Environment.CurrentDirectory, _config.SessionDir);
            Directory.CreateDirectory(dir);
            SessionPath = NewSessionLogPath(dir);
            _sessionLog = new StreamWriter(SessionPath, append: true) { AutoFlush = true };
        }
        catch
        {
            _sessionLog = null;
            SessionPath = null;
        }
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
 }
