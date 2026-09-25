using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读统计工作区文件扩展名的数量和总大小。</summary>
public sealed class FileExtensionReportTool : ITool
{
    public string Name => "file_extension_report";
    public string Description =>
        "统计工作区中各文件扩展名的数量和总大小，可控制递归深度、隐藏文件和结果数量。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 5，最大 20）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件（默认 false）" },
            ["max_extensions"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示扩展名数量（默认 100，最大 5000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var depth = Math.Clamp(ToolArgs.GetInt(args, "depth", 5), 0, 20);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxExtensions = Math.Clamp(ToolArgs.GetInt(args, "max_extensions", 100), 1, 5_000);
        var counts = new Dictionary<string, (long Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        long totalFiles = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            var extension = Path.GetExtension(file);
            var key = string.IsNullOrWhiteSpace(extension) ? "(无扩展名)" : extension.ToLowerInvariant();
            var size = new FileInfo(file).Length;
            counts[key] = counts.TryGetValue(key, out var current)
                ? (current.Count + 1, current.Bytes + size)
                : (1, size);
            totalFiles++;
        }
        var ordered = counts.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var output = new StringBuilder();
        output.AppendLine($"文件扩展名报告: {totalFiles:N0} 个文件，{ordered.Count:N0} 种扩展名");
        foreach (var pair in ordered.Take(maxExtensions))
            output.AppendLine($"{pair.Key}: {pair.Value.Count:N0} 个，{pair.Value.Bytes:N0} 字节");
        if (ordered.Count > maxExtensions)
            output.AppendLine($"…（另有 {ordered.Count - maxExtensions} 种扩展名未显示）");
        return output.ToString().TrimEnd();
    }
}
