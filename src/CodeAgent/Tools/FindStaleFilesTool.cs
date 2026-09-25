using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>按最后修改时间查找长期未更新的项目文件。</summary>
public sealed class FindStaleFilesTool : ITool
{
    private const int DefaultMaxFiles = 10_000;
    private const int MaxFiles = 50_000;

    public string Name => "find_stale_files";
    public string Description =>
        "查找最后修改时间早于指定天数的项目文件，按最旧时间排序，支持扩展名、隐藏文件、扫描数量和结果限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "扫描目录，默认工作区根目录" },
            ["older_than_days"] = new JsonObject { ["type"] = "integer", ["description"] = "最后修改距今至少多少天（默认 30）" },
            ["include_extensions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "只检查这些扩展名，如 [\".log\", \".tmp\"]",
            },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件/目录（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 10000，最大 50000）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示结果数（默认 100，最大 5000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var days = Math.Clamp(ToolArgs.GetInt(args, "older_than_days", 30), 0, 36_500);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 5_000);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var extensions = BuildExtensionFilter(args);
        var threshold = DateTime.UtcNow.AddDays(-days);
        var stale = new List<(string Path, DateTime LastWriteUtc)>();
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
            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(file);
                if (lastWrite <= threshold)
                    stale.Add((file, lastWrite));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var sorted = stale.OrderBy(x => x.LastWriteUtc).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var output = new StringBuilder();
        output.AppendLine($"陈旧文件（超过 {days} 天未修改）: {sorted.Count:N0} 个（扫描 {scanned:N0} 个）");
        if (capped)
            output.AppendLine($"达到 max_files={maxFiles} 上限，结果可能不完整");
        foreach (var item in sorted.Take(maxResults))
        {
            var age = Math.Max(0, (DateTime.UtcNow - item.LastWriteUtc).TotalDays);
            output.AppendLine($"{age:N0} 天  {ctx.Workspace.ToRelative(item.Path)}");
        }
        if (sorted.Count > maxResults)
            output.AppendLine($"…（另有 {sorted.Count - maxResults} 个文件未显示）");
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
