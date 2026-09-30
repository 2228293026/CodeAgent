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
            ["keyword"] = new JsonObject { ["type"] = "string", ["description"] = "搜索关键字（默认忽略大小写）" },
            ["case_sensitive"] = new JsonObject { ["type"] = "boolean", ["description"] = "区分大小写（默认 false；设为 true 时进行精确大小写匹配）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多列出的命中会话数（默认 3，最大 10）" },
        },
        ["required"] = new JsonArray("keyword"),
    };

    internal static bool HasNonEmptyFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.LinkTarget is null && fi.Length > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var keyword = ToolArgs.GetString(args, "keyword");
        if (string.IsNullOrWhiteSpace(keyword))
            throw new ToolException("缺少必填参数 keyword");
        var caseSensitive = ToolArgs.GetBool(args, "case_sensitive", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 3), 1, 10);
        var sessionDir = Path.Combine(Environment.CurrentDirectory, ctx.Config.SessionDir);
        if (!Directory.Exists(sessionDir))
            return Task.FromResult("(还没有任何会话记录)");
        try
        {
            if (new DirectoryInfo(sessionDir).LinkTarget is not null)
                return Task.FromResult("(还没有可搜索的会话记录)");
        }
        catch (IOException) { return Task.FromResult("(会话记录目录不可用)"); }
        catch (UnauthorizedAccessException) { return Task.FromResult("(会话记录目录不可用)"); }

        var sb = new StringBuilder();
        var printed = 0;
        // **命中总数**与**已显示数**必须分开记：原来只有 printed（受 max_files 封顶），
        // 于是 20 个会话命中、只显示 3 个时，模型被告知"匹配 3 个会话"——
        // 它会照着这个数向用户复述，于是少报。REPL 那侧早有 moreAvailable 处理，这里没有。
        var matched = 0;
        void Emit(string label, string restoreHint, List<(string Role, string Snippet)> hits)
        {
            if (hits.Count == 0)
                return;
            matched++;
            if (printed >= maxFiles)
                return;
            sb.AppendLine($"{label}（{restoreHint}）:");
            foreach (var (role, snippet) in hits)
            {
                ct.ThrowIfCancellationRequested();
                sb.AppendLine($"  [{role}] {TextUtil.TruncateLine(snippet, 110)}");
            }
            printed++;
        }

        var nameComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        // 会话日志（.jsonl，新 → 旧）
        ct.ThrowIfCancellationRequested();
        var logs = Directory.GetFiles(sessionDir, "*.jsonl")
            .Where(HasNonEmptyFile)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(Path.GetFileName, nameComparer)
            .ToList();
        ct.ThrowIfCancellationRequested();
        foreach (var log in logs)
        {
            ct.ThrowIfCancellationRequested();
            if (printed >= maxFiles)
                break;
            var age = TextUtil.RelativeTime(File.GetLastWriteTimeUtc(log), DateTime.UtcNow);
            Emit(Path.GetFileNameWithoutExtension(log) + $" · {age}",
                "/resume 可恢复", AgentClass.SearchSessionLog(log, keyword, caseSensitive, ct: ct));
        }
        // 命名快照（/save 的 .json）
        ct.ThrowIfCancellationRequested();
        var snapshots = Directory.GetFiles(sessionDir, "*.json")
            .Where(HasNonEmptyFile)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(Path.GetFileName, nameComparer)
            .ToList();
        ct.ThrowIfCancellationRequested();
        foreach (var snap in snapshots)
        {
            ct.ThrowIfCancellationRequested();
            if (printed >= maxFiles)
                break;
            var name = Path.GetFileNameWithoutExtension(snap);
            Emit($"快照 {name}", $"/load {name} 可恢复", AgentClass.SearchSnapshot(snap, keyword, caseSensitive, ct: ct));
        }

        ct.ThrowIfCancellationRequested();
        if (printed == 0)
        {
            // 与 REPL 的 /find 同一件事：0 命中**不等于**没有这个字串。
            // ASCII 关键字只出现在更长的词内部时会被词边界规则滤掉，
            // 此时回一句「没有匹配」是**假话**——模型会照着它告诉用户"没找到"。
            var interiorOnly = AgentClass.NeedsSearchWordBoundary(keyword)
                && Directory.GetFiles(sessionDir, "*.jsonl").Where(HasNonEmptyFile)
                    .Any(l => AgentClass.LogContainsSubstring(l, keyword, caseSensitive, ct));
            return Task.FromResult(interiorOnly
                ? $"(历史会话中有 \"{keyword}\"，但它只出现在更长的词内部（如 ColorThemeTests）——请改搜完整词)"
                : $"(历史会话中没有匹配 \"{keyword}\" 的内容)");
        }
        // 封顶与否看的是**有没有用满 max_files**，不是 matched > printed：
        // 没封顶时（printed < maxFiles）说明每个日志都读过了，matched 就是精确总数。
        // 封顶时循环已经 break，后面的日志没读，matched 恒等于 printed——
        // 此时报「匹配 3 个」是**少报**，模型会照着复述给用户。
        // 为拿精确数字把整个会话目录读完代价不值；「≥N（显示前 N 个）」既诚实，
        // 又与 REPL 的 moreAvailable 口径一致。
        var head = printed >= maxFiles
            ? $"匹配 ≥{printed} 个会话（显示前 {printed} 个）:"
            : $"匹配 {matched} 个会话:";
        return Task.FromResult(head + "\n" + sb.ToString().TrimEnd());
    }
}
