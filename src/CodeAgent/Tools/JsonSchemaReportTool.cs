using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读汇总 JSON Schema 文件的标识、标题与 draft 版本，并标出无标题/无 $id 的 schema。</summary>
public sealed class JsonSchemaReportTool : ITool
{
    public string Name => "json_schema_report";
    public string Description =>
        "只读提取 JSON Schema 的 $id、标题、类型和 draft 版本，标出缺少 $id/标题的 schema。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_all_json"] = new JsonObject { ["type"] = "boolean", ["description"] = "检查所有 .json（默认 false，只看 *schema*.json）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示条目数（默认 100，最大 1000）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 8，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var includeAll = ToolArgs.GetBool(args, "include_all_json", false);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 1_000);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        var entries = new List<(string File, string? Id, string? Title, string? Draft, string? Type)>();
        var invalid = new List<string>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileName(file);
            if (!includeAll && !name.Contains("schema", StringComparison.OrdinalIgnoreCase))
                continue;
            JsonNode? node;
            try { node = JsonNode.Parse(await File.ReadAllTextAsync(file, ct)); }
            catch (OperationCanceledException) { throw; }
            catch (JsonException) { invalid.Add(Path.GetRelativePath(root, file).Replace('\\', '/')); continue; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (node is not JsonObject obj)
                continue;
            entries.Add((
                Path.GetRelativePath(root, file).Replace('\\', '/'),
                Str(obj, "$id") ?? Str(obj, "id"),
                Str(obj, "title"),
                DraftOf(Str(obj, "$schema")),
                Str(obj, "type")));
        }
        if (entries.Count == 0 && invalid.Count == 0)
            return "JSON Schema 报告: 未发现 schema 文件";
        var output = new StringBuilder();
        output.AppendLine($"JSON Schema 报告: {entries.Count:N0} 个 schema");
        var byDraft = entries.GroupBy(e => e.Draft ?? "(未声明)").OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in byDraft)
            output.AppendLine($"  {group.Key}: {group.Count():N0}");
        var noId = entries.Count(e => string.IsNullOrEmpty(e.Id));
        var noTitle = entries.Count(e => string.IsNullOrEmpty(e.Title));
        output.AppendLine($"  缺少 $id: {noId:N0}   缺少 title: {noTitle:N0}");
        output.AppendLine("明细:");
        foreach (var entry in entries.OrderBy(e => e.File, StringComparer.Ordinal).Take(maxResults))
            output.AppendLine($"  {entry.File} | {(entry.Id ?? "(无 $id)")} | {entry.Title ?? "(无标题)"} | {entry.Draft ?? "(未声明)"} | {entry.Type ?? "-"}");
        if (entries.Count > maxResults)
            output.AppendLine($"…（另有 {entries.Count - maxResults} 个未显示）");
        if (invalid.Count > 0)
        {
            output.AppendLine($"解析失败: {invalid.Count:N0} 个");
            foreach (var file in invalid.Take(maxResults))
                output.AppendLine($"  {file}");
        }
        return output.ToString().TrimEnd();
    }

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>从 $schema URL 里取出 draft 版本（draft-07 / 2020-12 等）；非 draft 声明返回 null。
    /// 不能取最后一个路径段：draft-07 的写法是 `.../draft-07/schema#`，末段恒为 `schema`。</summary>
    internal static string? DraftOf(string? schema)
    {
        if (string.IsNullOrEmpty(schema))
            return null;
        var match = Regex.Match(schema, @"(draft-\d+|\d{4}-\d{2})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
