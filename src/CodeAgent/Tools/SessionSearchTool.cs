using System.Text;
using System.Text.Json.Nodes;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tools;

/// <summary>
/// 搜索历史会话（.jsonl 日志与 /save 命名快照）：模型可用它回顾之前对话的结论、
/// 已做改动或未完成事项——跨会话连续性的关键能力（人侧对应 /find）。
/// </summary>
public sealed class SessionSearchTool : ITool
{
    public string Name => "session_search";
    public string Description => "在历史会话记录中搜索关键字（忽略大小写，最新在前）。用于回顾之前对话的结论、修改过的文件或未完成事项。斜杠命令行不算命中。";
    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["keyword"] = new JsonObject { ["type"] = "string", ["description"] = "搜索关键字（忽略大小写）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多列出的命中会话数（默认 3，最大 10）" },
        },
        ["required"] = new JsonArray("keyword"),
    };

    public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var keyword = ToolArgs.GetString(args, "keyword");
        if (string.IsNullOrWhiteSpace(keyword))
            throw new ToolException("缺少必填参数 keyword");
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 3), 1, 10);
        var sessionDir = Path.Combine(Environment.CurrentDirectory, ctx.Config.SessionDir);
        if (!Directory.Exists(sessionDir))
            return Task.FromResult("(还没有任何会话记录)");

        var sb = new StringBuilder();
        var printed = 0;
        void Emit(string label, string restoreHint, List<(string Role, string Snippet)> hits)
        {
            if (hits.Count == 0 || printed >= maxFiles)
                return;
            sb.AppendLine($"{label}（{restoreHint}）:");
            foreach (var (role, snippet) in hits)
                sb.AppendLine($"  [{role}] {TextUtil.TruncateLine(snippet, 110)}");
            printed++;
        }

        // 会话日志（.jsonl，新 → 旧）
        foreach (var log in Directory.GetFiles(sessionDir, "*.jsonl")
                     .Where(f => new FileInfo(f).Length > 0)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (printed >= maxFiles)
                break;
            var age = TextUtil.RelativeTime(File.GetLastWriteTimeUtc(log), DateTime.UtcNow);
            Emit(Path.GetFileNameWithoutExtension(log) + $" · {age}",
                "/resume 可恢复", AgentClass.SearchSessionLog(log, keyword));
        }
        // 命名快照（/save 的 .json）
        foreach (var snap in Directory.GetFiles(sessionDir, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (printed >= maxFiles)
                break;
            var name = Path.GetFileNameWithoutExtension(snap);
            Emit($"快照 {name}", $"/load {name} 可恢复", AgentClass.SearchSnapshot(snap, keyword));
        }

        return Task.FromResult(printed == 0
            ? $"(历史会话中没有匹配 \"{keyword}\" 的内容)"
            : $"匹配 {printed} 个会话:\n" + sb.ToString().TrimEnd());
    }
}
