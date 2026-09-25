using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>统计工作区项目文件：按扩展名汇总大小，可选统计行数并列出最大文件。</summary>
public sealed class ProjectStatsTool : ITool
{
    private const int DefaultMaxFiles = 5_000;
    private const int MaxFiles = 50_000;

    public string Name => "project_stats";
    public string Description =>
        "统计项目文件数量、总大小和扩展名分布；可按扩展名筛选、统计文本行数并列出最大的文件。默认跳过构建缓存、版本控制目录和符号链接。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "要统计的目录，默认工作区根目录" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多统计的文件数（默认 5000，最大 50000）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 8，最大 32；0 只统计当前目录文件）" },
            ["include_extensions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "只统计这些扩展名，如 [\".cs\", \".md\"]；省略则统计全部",
            },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件/目录（默认 false）" },
            ["count_lines"] = new JsonObject { ["type"] = "boolean", ["description"] = "统计可读文本文件的行数（默认 false，较大型项目建议按需开启）" },
            ["top_files"] = new JsonObject { ["type"] = "integer", ["description"] = "列出最大的 N 个文件（默认 5，0=不列出，最大 20）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (File.Exists(root))
            throw new ToolException($"path 是文件: {requestedPath}（project_stats 需要目录）");
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");

        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 0, 32);
        var topCount = Math.Clamp(ToolArgs.GetInt(args, "top_files", 5), 0, 20);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var countLines = ToolArgs.GetBool(args, "count_lines", false);
        var extensionFilter = BuildExtensionFilter(args);

        var byExtension = new Dictionary<string, ExtensionStats>(StringComparer.OrdinalIgnoreCase);
        var largest = new List<(string Path, long Bytes)>();
        long totalBytes = 0;
        long totalLines = 0;
        var fileCount = 0;
        var capped = false;

        foreach (var file in SkipDirs.EnumerateFilesPruned(
                     root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            var extension = NormalizeExtension(Path.GetExtension(file));
            if (extensionFilter is not null && !extensionFilter.Contains(extension))
                continue;
            if (fileCount >= maxFiles)
            {
                capped = true;
                break;
            }

            long size;
            try { size = new FileInfo(file).Length; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            fileCount++;
            totalBytes += size;
            if (!byExtension.TryGetValue(extension, out var stats))
                byExtension[extension] = stats = new ExtensionStats();
            stats.Count++;
            stats.Bytes += size;
            largest.Add((file, size));

            if (countLines)
            {
                var lines = SkipDirs.CountFileLines(file, ct);
                if (lines is { } lineCount)
                {
                    stats.Lines += lineCount;
                    totalLines += lineCount;
                }
            }
        }

        var output = new StringBuilder();
        var displayRoot = string.IsNullOrWhiteSpace(requestedPath) ? "." : requestedPath;
        output.AppendLine($"项目统计: {displayRoot}");
        output.AppendLine($"文件: {fileCount:N0}{(capped ? $"（达到 max_files={maxFiles:N0} 上限）" : "")}");
        output.AppendLine($"总大小: {TextUtil.FormatBytes(totalBytes)}");
        if (countLines)
            output.AppendLine($"总行数: {totalLines:N0}");

        if (byExtension.Count == 0)
        {
            output.AppendLine("没有匹配的文件。");
            return output.ToString().TrimEnd();
        }

        output.AppendLine("扩展名分布:");
        foreach (var pair in byExtension.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var label = pair.Key.Length == 0 ? "(无扩展名)" : pair.Key;
            var line = $"  {label}: {pair.Value.Count:N0} 个，{TextUtil.FormatBytes(pair.Value.Bytes)}";
            if (countLines && pair.Value.Lines > 0)
                line += $"，{pair.Value.Lines:N0} 行";
            output.AppendLine(line);
        }

        if (topCount > 0 && largest.Count > 0)
        {
            output.AppendLine($"最大的 {Math.Min(topCount, largest.Count)} 个文件:");
            foreach (var (file, bytes) in largest.OrderByDescending(x => x.Bytes).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(topCount))
                output.AppendLine($"  {ctx.Workspace.ToRelative(file)} ({TextUtil.FormatBytes(bytes)})");
        }
        return output.ToString().TrimEnd();
    }

    private static HashSet<string>? BuildExtensionFilter(JsonObject? args)
    {
        var values = ToolArgs.GetStringList(args, "include_extensions");
        if (values is null)
            return null;
        return values
            .Select(v => NormalizeExtension(v.Trim()))
            .Where(v => v.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string extension)
    {
        if (extension.Length == 0)
            return "";
        return extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    }

    private sealed class ExtensionStats
    {
        public int Count { get; set; }
        public long Bytes { get; set; }
        public long Lines { get; set; }
    }
}
