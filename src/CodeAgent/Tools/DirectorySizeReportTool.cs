using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>按目录汇总项目文件数量和占用空间，便于定位空间热点。</summary>
public sealed class DirectorySizeReportTool : ITool
{
    private const int DefaultMaxFiles = 10_000;
    private const int MaxFiles = 50_000;

    public string Name => "directory_size_report";
    public string Description =>
        "递归扫描项目并按目录汇总文件数量和总字节数，按空间占用排序，支持扩展名、隐藏文件和扫描限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "扫描目录，默认工作区根目录" },
            ["include_extensions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "只统计这些扩展名，如 [\".dll\", \".json\"]",
            },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件/目录（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 10000，最大 50000）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示目录数（默认 50，最大 5000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 50), 1, 5_000);
        var extensions = BuildExtensionFilter(args);
        var groups = new Dictionary<string, (long Bytes, int Count)>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var capped = false;
        long totalBytes = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            if (extensions is not null && !extensions.Contains(NormalizeExtension(Path.GetExtension(file))))
                continue;
            if (scanned >= maxFiles)
            {
                capped = true;
                break;
            }
            scanned++;
            try
            {
                var size = new FileInfo(file).Length;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var directory = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? ".";
                if (directory.Length == 0)
                    directory = ".";
                groups.TryGetValue(directory, out var current);
                groups[directory] = (current.Bytes + size, current.Count + 1);
                totalBytes += size;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var output = new StringBuilder();
        output.AppendLine($"目录空间报告: 扫描 {scanned:N0} 个文件，总计 {TextUtil.FormatBytes(totalBytes)}");
        if (capped)
            output.AppendLine($"达到 max_files={maxFiles} 上限，结果可能不完整");
        if (groups.Count == 0)
            return output.Append("没有可统计的文件。").ToString();
        output.AppendLine($"目录数: {groups.Count:N0}");
        var shown = Math.Min(groups.Count, maxResults);
        foreach (var group in groups.OrderByDescending(g => g.Value.Bytes).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(shown))
            output.AppendLine($"{TextUtil.FormatBytes(group.Value.Bytes),12}  {group.Value.Count,8:N0} 个文件  {group.Key}");
        if (groups.Count > shown)
            output.AppendLine($"…（另有 {groups.Count - shown} 个目录未显示）");
        return output.ToString().TrimEnd();
    }

    private static HashSet<string>? BuildExtensionFilter(JsonObject? args)
    {
        var values = ToolArgs.GetStringList(args, "include_extensions");
        return values?.Select(v => NormalizeExtension(v.Trim()))
            .Where(v => v.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string extension)
    {
        if (extension.Length == 0)
            return "";
        return extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    }
}
