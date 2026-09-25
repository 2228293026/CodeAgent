using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读审计 codeagent.json 的键：类型、未知键和疑似拼写错误，便于发现配置漂移。</summary>
public sealed class ConfigKeyReportTool : ITool
{
    public string Name => "config_key_report";
    public string Description =>
        "只读审计 codeagent.json：列出每个键及其类型，标出未知键和疑似拼写错误的键（附带最近似的合法键）。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "配置文件路径或目录，默认 codeagent.json" },
            ["include_nested"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含嵌套对象（默认 true）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "嵌套深度上限（默认 3，最大 8）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 3), 1, 8);
        var includeNested = ToolArgs.GetBool(args, "include_nested", true);
        var file = ResolveFile(requestedPath);
        JsonNode? root;
        try { root = JsonNode.Parse(await File.ReadAllTextAsync(file, ct)); }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex) { throw new ToolException($"配置文件不是合法 JSON: {ex.Message}"); }
        if (root is not JsonObject config)
            throw new ToolException("配置文件根节点不是对象");
        var known = KnownKeys();
        var knownList = known.Keys.ToList();
        var entries = new List<(string Key, string Type, bool IsKnown, string Nearest)>();
        Walk(config, prefix: string.Empty, depth: 0);
        if (entries.Count == 0)
            return $"配置键报告: {Path.GetFileName(file)} 中没有键";
        var unknown = entries.Where(e => !e.IsKnown).ToList();
        var output = new StringBuilder();
        output.AppendLine($"配置键报告: {Path.GetFileName(file)} 共 {entries.Count} 个键，未知 {unknown.Count} 个");
        output.AppendLine("已知键:");
        foreach (var entry in entries.Where(e => e.IsKnown))
            output.AppendLine($"  {entry.Key}: {entry.Type}");
        if (unknown.Count > 0)
        {
            output.AppendLine("未知键（可能是拼写错误或版本不兼容的遗留项）:");
            foreach (var entry in unknown)
                output.AppendLine(entry.Nearest.Length == 0
                    ? $"  {entry.Key}: {entry.Type} → 无相近的合法键"
                    : $"  {entry.Key}: {entry.Type} → 疑似应为 {entry.Nearest}");
        }
        return output.ToString().TrimEnd();

        void Walk(JsonObject obj, string prefix, int depth)
        {
            foreach (var pair in obj)
            {
                ct.ThrowIfCancellationRequested();
                var key = prefix.Length == 0 ? pair.Key : $"{prefix}.{pair.Key}";
                var leaf = pair.Key.ToLowerInvariant();
                // 顶层键或嵌套键的最后一段命中已知集合即视为合法；否则是未知键
                var isKnown = known.ContainsKey(leaf) || leaf.Split('.').Any(part => known.ContainsKey(part));
                entries.Add((key, Describe(pair.Value), isKnown, isKnown ? string.Empty : Nearest(leaf, knownList)));
                if (includeNested && pair.Value is JsonObject child && depth < maxDepth)
                    Walk(child, key, depth + 1);
            }
        }
    }

    private static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "array",
        JsonObject => "object",
        JsonValue value when value.TryGetValue<bool>(out _) => "bool",
        JsonValue value when value.TryGetValue<double>(out _) => "number",
        JsonValue value when value.TryGetValue<string>(out _) => "string",
        _ => "value",
    };

    /// <summary>已知配置键（取自 AgentConfig 的属性名，转小写比较）。</summary>
    internal static Dictionary<string, string> KnownKeys() =>
        typeof(AgentConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .GroupBy(p => p.Name.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

    /// <summary>Levenshtein 距离最近似的已知键；距离超过 3 视为无建议。</summary>
    internal static string Nearest(string leaf, List<string> knownList)
    {
        var best = string.Empty;
        var bestDistance = 4;
        foreach (var candidate in knownList)
        {
            var distance = Distance(leaf, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static string ResolveFile(string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            var full = Path.GetFullPath(requestedPath);
            if (Directory.Exists(full))
                full = Path.Combine(full, "codeagent.json");
            if (!File.Exists(full))
                throw new ToolException($"配置文件不存在: {requestedPath}");
            return full;
        }
        var candidate = Path.Combine(Environment.CurrentDirectory, "codeagent.json");
        if (File.Exists(candidate))
            return candidate;
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeagent", "config.json");
        if (File.Exists(home))
            return home;
        throw new ToolException("未找到 codeagent.json");
    }
}
