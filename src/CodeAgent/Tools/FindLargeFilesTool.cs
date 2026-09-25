using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>扫描项目中的大文件，按大小排序并报告最占空间的文件。</summary>
public sealed class FindLargeFilesTool : ITool
{
    private const int DefaultMaxFiles = 10_000;
    private const int MaxFiles = 50_000;

    public string Name => "find_large_files";
    public string Description =>
        "扫描项目文件并按大小列出最大的文件，支持最小大小、扩展名、隐藏文件、扫描数量和输出数量限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "扫描目录，默认工作区根目录" },
            ["min_size_kb"] = new JsonObject { ["type"] = "integer", ["description"] = "最小文件大小（KB，默认 100）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示文件数（默认 20，最大 200）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 10000，最大 50000）" },
            ["include_extensions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "只检查这些扩展名，如 [\".log\", \".bin\"]",
            },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件/目录（默认 false）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (File.Exists(root))
            throw new ToolException($"path 是文件: {requestedPath}（find_large_files 需要目录）");
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");

        var minBytes = Math.Max(1, ToolArgs.GetInt(args, "min_size_kb", 100)) * 1024L;
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 20), 1, 200);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var extensions = BuildExtensionFilter(args);
        var candidates = new List<(string Path, long Size)>();
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
                var size = new FileInfo(file).Length;
                if (size >= minBytes)
                    candidates.Add((file, size));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var sorted = candidates.OrderByDescending(c => c.Size).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var output = new StringBuilder();
        output.AppendLine($"扫描文件: {scanned:N0}，符合条件: {sorted.Count:N0}");
        if (capped)
            output.AppendLine($"已达到 max_files={maxFiles:N0} 上限，结果可能不完整");
        if (sorted.Count == 0)
            return output.Append("没有符合条件的大文件。").ToString();
        output.AppendLine($"显示前 {Math.Min(maxResults, sorted.Count)} 个文件：");
        foreach (var candidate in sorted.Take(maxResults))
            output.AppendLine($"  {TextUtil.FormatBytes(candidate.Size),12}  {ctx.Workspace.ToRelative(candidate.Path)}");
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
