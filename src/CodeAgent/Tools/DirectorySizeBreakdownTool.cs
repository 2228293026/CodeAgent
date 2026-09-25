using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读按顶层子目录汇总工作区文件数量和大小。</summary>
public sealed class DirectorySizeBreakdownTool : ITool
{
    public string Name => "directory_size_breakdown";
    public string Description =>
        "按顶层子目录汇总文件数量和总大小，支持最小大小、最大结果数和隐藏文件开关。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["min_bytes"] = new JsonObject { ["type"] = "integer", ["description"] = "仅显示总大小至少为该值的子目录（默认 0）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示子目录数（默认 100，最大 5000）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件和目录（默认 false）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var minBytes = Math.Max(0, ToolArgs.GetInt(args, "min_bytes", 0));
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 5_000);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var totals = new Dictionary<string, (long Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        long rootFiles = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            var relative = Path.GetRelativePath(root, file);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            var key = relative.Length == first.Length ? "(根目录文件)" : first;
            var size = new FileInfo(file).Length;
            totals[key] = totals.TryGetValue(key, out var current)
                ? (current.Count + 1, current.Bytes + size)
                : (1, size);
            if (key == "(根目录文件)")
                rootFiles++;
        }
        var ordered = totals.Where(pair => pair.Value.Bytes >= minBytes)
            .OrderByDescending(pair => pair.Value.Bytes).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var output = new StringBuilder();
        output.AppendLine($"目录大小分布: {ordered.Count:N0} 个分组，根目录文件 {rootFiles:N0} 个");
        foreach (var pair in ordered.Take(maxResults))
            output.AppendLine($"{pair.Key}: {pair.Value.Count:N0} 个文件，{pair.Value.Bytes:N0} 字节");
        if (ordered.Count > maxResults)
            output.AppendLine($"…（另有 {ordered.Count - maxResults} 个分组未显示）");
        return output.ToString().TrimEnd();
    }
}
