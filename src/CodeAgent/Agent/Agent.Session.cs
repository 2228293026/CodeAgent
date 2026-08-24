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

    /// <summary>命名会话是否存在（/export 的名/编号二义消解：同名快照优先于 /resume 编号）。</summary>
    public bool SessionExists(string name) => File.Exists(SessionFilePath(name));

    /// <summary>从命名会话恢复对话（替换当前历史）。</summary>
    public void LoadSession(string name)
    {
        var msgs = LoadMessages(name);
        _messages.Clear();
        _messages.AddRange(msgs);
        // 快照可能在别的模式下保存：system 换成当前模式提示（与 LoadSessionLog 后 SetMode 的语义一致）
        if (_messages.Count > 0 && _messages[0].Role == MessageRole.System)
            _messages[0] = new ProviderMessage { Role = MessageRole.System, Content = EffectivePrompt(CurrentMode) };
        // 加载的会话没有「上一轮」可撤回：清空起点栈，否则 ESC 撤回会按过期索引删掉刚加载的消息
        _turnStarts.Clear();
        LastInputTokens = 0; // 上下文变为加载的历史：退回估算口径
        LastPrompt = null;   // 加载前的「上一条请求」不应被 /retry 复活进加载的对话
        // 与 LoadSessionLog 一致：滚动新日志并重写，--continue 恢复的是加载后的对话而非旧日志
        RollSessionLog();
        foreach (var m in _messages)
            LogMessage(m);
    }

    /// <summary>把当前对话（或指定命名会话）导出为 Markdown 记录，返回文件路径。</summary>
    public string ExportMarkdown(string? name)
    {
        IReadOnlyList<ProviderMessage> msgs = name is null ? _messages : LoadMessages(name);
        var safeName = name is null ? $"chat-{DateTime.Now:yyyyMMdd-HHmmss}" : SanitizeName(name);
        return ExportMessages(msgs, safeName, name);
    }

    /// <summary>把指定会话日志（/resume 列表中的编号对应文件）导出为 Markdown，不动当前对话。</summary>
    public string ExportSessionLogMarkdown(string logPath)
    {
        var msgs = ReadSessionLogFile(logPath);
        if (msgs.Count == 0)
            throw new FileNotFoundException($"会话日志为空或损坏: {logPath}");
        return ExportMessages(msgs, SanitizeName(Path.GetFileNameWithoutExtension(logPath)), null);
    }

    private string ExportMessages(IReadOnlyList<ProviderMessage> msgs, string safeName, string? title)
    {
        var dir = Path.Combine(Environment.CurrentDirectory, _ctx.Config.ExportDir);
        Directory.CreateDirectory(dir);
        // name 同样需 sanitize：/export ../evil 曾写入 ExportDir 父目录（路径穿越）
        var file = Path.Combine(dir, safeName + ".md");

        var sb = new StringBuilder();
        sb.AppendLine($"# CodeAgent 会话{(title is null ? "" : $"：{title}")}");
        sb.AppendLine();
        // 元信息头：归档时不用翻内容就知道来源
        sb.AppendLine($"- 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var po = _config.Providers.TryGetValue(_config.Provider, out var o) ? o : null;
        sb.AppendLine($"- 模型：{_config.Provider}{(string.IsNullOrWhiteSpace(po?.Model) ? "" : $" / {po!.Model}")}");
        var branch = GitInfo.CurrentBranch(Environment.CurrentDirectory);
        if (branch is not null)
            sb.AppendLine($"- Git 分支：{branch}");
        sb.AppendLine($"- 消息数：{msgs.Count}（不含本头部）");
        sb.AppendLine();
        foreach (var m in msgs)
        {
            sb.AppendLine(m.Role switch
            {
                MessageRole.System => "## 系统",
                MessageRole.User => "## 用户",
                MessageRole.Assistant => "## 助手",
                _ => m.IsError ? $"## 工具（失败）：{m.ToolName}" : $"## 工具：{m.ToolName}",
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
            if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ThinkingText))
            {
                // 思考内容只入摘要不入全文（可能很长且属推理过程）
                var redactedNote = m.RedactedThinkingData is { Count: > 0 } r ? $"，另有 {r.Count} 个加密块" : "";
                sb.AppendLine($"- 思考：{TextUtil.TruncateLine(m.ThinkingText, 120)}{redactedNote}");
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
    /// <summary>解析会话日志文件为消息列表（不改当前对话）；IO 错误返回已解析部分。
    /// LoadSessionLog 与 /export &lt;编号&gt; 共用。</summary>
    internal static List<ProviderMessage> ReadSessionLogFile(string path)
    {
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
            // 读取中断：返回已解析部分（LoadSessionLog 视为空 → false）
        }
        catch (UnauthorizedAccessException)
        {
            // 受保护目录 / ACL 拒绝：按无法恢复处理，--continue 启动路径不至于抛异常崩溃
        }
        return msgs;
    }

    /// <summary>从会话日志（.jsonl，每条消息自动写入）恢复对话：--continue / /resume 用。
    /// 恢复后滚动新日志并把已恢复的消息写进去，新日志自包含（可再次被恢复）；
    /// 开头的 system 一律重盖为当前模式提示（启动路径随后的 SetMode 重盖为相同值）。</summary>
    public bool LoadSessionLog(string path)
    {
        if (!File.Exists(path))
            return false;
        var msgs = ReadSessionLogFile(path);
        if (msgs.Count == 0)
            return false;
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
        // 开头 system 重盖为当前模式提示（与 LoadSession 一致）：REPL 的 /resume 路径不会再
        // SetMode（只有启动 --continue 会），日志里的旧 system（别的模式或旧版提示）若不重盖
        // 会原样带进后续请求。启动路径随后 SetMode 重盖为相同值，无影响。
        if (_messages[0].Role != MessageRole.System)
            _messages.Insert(0, new ProviderMessage { Role = MessageRole.System, Content = EffectivePrompt(CurrentMode) });
        else
            _messages[0] = new ProviderMessage { Role = MessageRole.System, Content = EffectivePrompt(CurrentMode) };
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
                ThinkingText = n["thinkingText"]?.GetValue<string>(),
                ThinkingSignature = n["thinkingSignature"]?.GetValue<string>(),
                RedactedThinkingData = (n["redactedThinking"] as JsonArray)?
                    .Select(r => r?.GetValue<string>())
                    .Where(r => !string.IsNullOrEmpty(r))
                    .Cast<string>()
                    .ToList(),
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

    /// <summary>会话日志摘要（/resume 列表用）：首条用户消息预览（多行折叠为 ⏎）+ 消息条数。
    /// 文件名只是时间戳，看不出哪个会话是哪段对话——首条用户输入才是可辨识的标题。
    /// 流式读取且行数封顶 5000：超大日志不做完整解析，避免列表卡顿；Capped=true 表示
    /// 条数只是下限（实际更多），显示层应标「≥」而不是当成精确值。</summary>
    internal static (string? Preview, int Count, bool Capped) SessionLogSummary(string path)
    {
        try
        {
            string? preview = null;
            var count = 0;
            var capped = false;
            foreach (var line in ReadLogLines(path))
            {
                if (count >= 5000)
                {
                    capped = true; // 已读满 5000 行且还有更多：count 只是下限，不是精确值
                    break;
                }
                count++;
                if (preview is not null)
                    continue;
                try
                {
                    var n = JsonNode.Parse(line) as JsonObject;
                    // 斜杠命令（/model xxx 等）不是可辨识的对话标题：跳过，取首条真实用户输入
                    if (n?["role"]?.GetValue<string>() == "user" &&
                        n["content"]?.GetValue<string>() is { Length: > 0 } c &&
                        !c.TrimStart().StartsWith('/'))
                        preview = c.Replace("\r", "").Replace("\n", " ⏎ ").Trim();
                }
                catch
                {
                    // 损坏行跳过（与 ParseLogLine 一致）
                }
            }
            return (preview, count, capped);
        }
        catch
        {
            return (null, 0, false); // 读取失败：列表降级为仅显示时间戳
        }
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

    /// <summary>在会话日志里搜索关键字（忽略大小写）：返回最多 maxHits 条 (角色, 命中片段)。
    /// 片段取命中点前后窗口并折叠换行；损坏行/读取失败按无命中处理。/find 用。</summary>
    internal static List<(string Role, string Snippet)> SearchSessionLog(string path, string keyword, int maxHits = 3)
    {
        var hits = new List<(string, string)>();
        if (string.IsNullOrEmpty(keyword))
            return hits;
        try
        {
            foreach (var line in ReadLogLines(path))
            {
                if (hits.Count >= maxHits)
                    break;
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject n)
                        continue;
                    var content = n["content"]?.GetValue<string>();
                    if (content is null)
                        continue;
                    // 斜杠命令行（/model gpt 等）是操作记录不是对话内容：跳过不作为命中
                    var role = n["role"]?.GetValue<string>() ?? "?";
                    if (role == "user" && content.TrimStart().StartsWith('/'))
                        continue;
                    hits.AddRange(MatchWindow(content, keyword, role));
                }
                catch
                {
                    // 损坏行跳过（与 ParseLogLine 一致）
                }
            }
        }
        catch
        {
            // 读取失败按无命中
        }
        return hits;
    }

    /// <summary>在命名快照（/save 的 .json）里搜索关键字：返回最多 maxHits 条 (角色, 命中片段)。/find 用。</summary>
    internal static List<(string Role, string Snippet)> SearchSnapshot(string path, string keyword, int maxHits = 3)
    {
        var hits = new List<(string, string)>();
        if (string.IsNullOrEmpty(keyword))
            return hits;
        try
        {
            var dto = JsonSerializer.Deserialize<List<MessageDto>>(File.ReadAllText(path), JsonOpts);
            if (dto is null)
                return hits;
            foreach (var d in dto)
            {
                if (hits.Count >= maxHits)
                    break;
                var content = d.content;
                if (content is null)
                    continue;
                // 与日志搜索同口径：斜杠命令行不算命中
                if (d.role == "user" && content.TrimStart().StartsWith('/'))
                    continue;
                hits.AddRange(MatchWindow(content, keyword, d.role));
            }
        }
        catch
        {
            // 快照损坏/读取失败按无命中处理
        }
        return hits;
    }

    /// <summary>关键字的命中片段：前后窗口折叠换行，超出部分用省略号标记。日志与快照搜索共用。</summary>
    private static IEnumerable<(string Role, string Snippet)> MatchWindow(string content, string keyword, string role)
    {
        var idx = content.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            yield break;
        var start = Math.Max(0, idx - 40);
        var len = Math.Min(content.Length - start, keyword.Length + 80);
        var snippet = content.Substring(start, len).Replace("\r", "").Replace("\n", " ⏎ ");
        yield return (role, (start > 0 ? "…" : "") + snippet + (start + len < content.Length ? "…" : ""));
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
            PruneSessionLogs(dir, _ctx.Config.MaxSessionLogs, SessionPath);
        }
        catch
        {
            _sessionLog = null;
            SessionPath = null;
        }
    }

    /// <summary>删除超出保留数量的最旧会话日志（文件名 = 时间戳，字典序即时间序）。
    /// keep &lt;= 0 不清理；正在使用的当前日志跳过；单个删除失败忽略。</summary>
    internal static int PruneSessionLogs(string dir, int keep, string? exceptPath = null)
    {
        if (keep <= 0)
            return 0;
        try
        {
            var logs = Directory.GetFiles(dir, "*.jsonl")
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var extra = logs.Count - keep;
            if (extra <= 0)
                return 0;
            var deleted = 0;
            foreach (var p in logs.Take(extra))
            {
                if (string.Equals(p, exceptPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    File.Delete(p);
                    deleted++;
                }
                catch { /* 被占用/权限：跳过该文件 */ }
            }
            return deleted;
        }
        catch
        {
            return 0;
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
        thinkingText = m.ThinkingText,
        thinkingSignature = m.ThinkingSignature,
        redactedThinking = m.RedactedThinkingData?.ToList(),
        isError = m.IsError,
    };

    private static ProviderMessage FromDto(MessageDto d) => new()
    {
        Role = Enum.TryParse<MessageRole>(d.role, true, out var r) ? r : MessageRole.User,
        Content = d.content,
        ToolCalls = d.toolCalls?.Select(tc => new ToolCall { Id = tc.id, Name = tc.name, ArgumentsJson = tc.arguments }).ToList(),
        ToolCallId = d.toolCallId,
        ToolName = d.toolName,
        ThinkingText = d.thinkingText,
        ThinkingSignature = d.thinkingSignature,
        RedactedThinkingData = d.redactedThinking is { Count: > 0 } ? d.redactedThinking : null,
        IsError = d.isError,
    };

    private sealed class MessageDto
    {
        public string role { get; set; } = "";
        public string? content { get; set; }
        public List<ToolCallDto>? toolCalls { get; set; }
        public string? toolCallId { get; set; }
        public string? toolName { get; set; }
        public string? thinkingText { get; set; }
        public string? thinkingSignature { get; set; }
        public List<string>? redactedThinking { get; set; }
        public bool isError { get; set; }
    }

    private sealed class ToolCallDto
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string arguments { get; set; } = "";
    }
}
