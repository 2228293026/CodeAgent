using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>检测在不同平台或目录中可能造成覆盖的同名文件（不区分大小写）。</summary>
public sealed class FindFileNameConflictsTool : ITool
{
    private const int DefaultMaxFiles = 10_000;
    private const int MaxFiles = 50_000;

    public string Name => "find_filename_conflicts";
    public string Description =>
        "按不区分大小写的文件名扫描项目，找出可能跨目录冲突的同名文件，支持扩展名、隐藏文件和数量限制。";

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
                ["description"] = "只检查这些扩展名，如 [\".cs\", \".json\"]",
            },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件/目录（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 10000，最大 50000）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示冲突组数（默认 100，最大 5000）" },
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
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 5_000);
        var extensions = BuildExtensionFilter(args);
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var capped = false;
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
            var name = Path.GetFileName(file);
            if (!groups.TryGetValue(name, out var paths))
                groups[name] = paths = new List<string>();
            paths.Add(file);
        }
        var conflicts = groups.Where(g => g.Value.Count > 1)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var output = new StringBuilder();
        output.AppendLine($"文件名冲突（忽略大小写）: {conflicts.Count:N0} 组（扫描 {scanned:N0} 个文件）");
        if (capped)
            output.AppendLine($"达到 max_files={maxFiles} 上限，结果可能不完整");
        foreach (var group in conflicts.Take(maxResults))
        {
            output.AppendLine($"{group.Key}（{group.Value.Count} 个）");
            foreach (var file in group.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                output.AppendLine($"  {ctx.Workspace.ToRelative(file)}");
        }
        if (conflicts.Count > maxResults)
            output.AppendLine($"…（另有 {conflicts.Count - maxResults} 组未显示）");
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
