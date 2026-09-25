using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>在项目文件中按大小和 SHA256 查找完全重复的内容，并估算可回收空间。</summary>
public sealed class FindDuplicatesTool : ITool
{
    private const int DefaultMaxFiles = 5_000;
    private const int MaxFiles = 50_000;
    private const int MaxHashSizeMb = 500;

    public string Name => "find_duplicates";
    public string Description =>
        "扫描项目文件，按大小和 SHA256 查找完全重复文件，输出每组路径和可回收空间。默认跳过缓存/构建目录、隐藏文件和符号链接。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "扫描目录，默认工作区根目录" },
            ["min_size"] = new JsonObject { ["type"] = "integer", ["description"] = "只检查至少这么大的文件（字节，默认 1）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多检查文件数（默认 5000，最大 50000）" },
            ["max_groups"] = new JsonObject { ["type"] = "integer", ["description"] = "最多输出重复组数（默认 20，最大 100）" },
            ["include_extensions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "只检查这些扩展名，如 [\".png\", \".mp4\"]；省略则检查全部",
            },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件/目录（默认 false）" },
            ["hash_limit_mb"] = new JsonObject { ["type"] = "integer", ["description"] = "单文件哈希大小上限 MB（默认 100，最大 500）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (File.Exists(root))
            throw new ToolException($"path 是文件: {requestedPath}（find_duplicates 需要目录）");
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");

        var minSize = Math.Max(1, ToolArgs.GetInt(args, "min_size", 1));
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxGroups = Math.Clamp(ToolArgs.GetInt(args, "max_groups", 20), 1, 100);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var hashLimit = Math.Clamp(ToolArgs.GetInt(args, "hash_limit_mb", 100), 1, MaxHashSizeMb) * 1024L * 1024L;
        var extensionFilter = BuildExtensionFilter(args);
        var groups = new Dictionary<(long Size, string Hash), List<string>>();
        var scanned = 0;
        var skippedLarge = 0;
        var capped = false;

        foreach (var file in SkipDirs.EnumerateFilesPruned(root, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            if (extensionFilter is not null && !extensionFilter.Contains(NormalizeExtension(Path.GetExtension(file))))
                continue;
            if (scanned >= maxFiles)
            {
                capped = true;
                break;
            }
            scanned++;
            long size;
            try { size = new FileInfo(file).Length; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (size < minSize)
                continue;
            if (size > hashLimit)
            {
                skippedLarge++;
                continue;
            }
            var hash = SkipDirs.ComputeFileSha256(file, ct);
            if (hash is null)
                continue;
            var key = (size, hash);
            if (!groups.TryGetValue(key, out var paths))
                groups[key] = paths = new List<string>();
            paths.Add(file);
        }

        var duplicates = groups
            .Where(g => g.Value.Count > 1)
            .OrderByDescending(g => g.Key.Size * (g.Value.Count - 1))
            .ThenByDescending(g => g.Value.Count)
            .ThenBy(g => g.Key.Hash, StringComparer.Ordinal)
            .ToList();
        var duplicateFiles = duplicates.Sum(g => g.Value.Count);
        var wasted = duplicates.Sum(g => g.Key.Size * (g.Value.Count - 1));
        var output = new StringBuilder();
        output.AppendLine($"扫描文件: {scanned:N0}");
        if (capped)
            output.AppendLine($"已达到 max_files={maxFiles:N0} 上限，结果可能不完整");
        if (skippedLarge > 0)
            output.AppendLine($"跳过超过哈希大小上限的文件: {skippedLarge:N0}");
        if (duplicates.Count == 0)
        {
            output.AppendLine("没有发现完全重复的文件。");
            return output.ToString().TrimEnd();
        }

        output.AppendLine($"重复组: {duplicates.Count:N0}（{duplicateFiles:N0} 个文件）");
        output.AppendLine($"潜在可回收空间: {TextUtil.FormatBytes(wasted)}");
        output.AppendLine($"显示前 {Math.Min(maxGroups, duplicates.Count)} 组:");
        foreach (var group in duplicates.Take(maxGroups))
        {
            output.AppendLine($"  {group.Value.Count} 个文件，每个 {TextUtil.FormatBytes(group.Key.Size)}，可回收 {TextUtil.FormatBytes(group.Key.Size * (group.Value.Count - 1))}");
            foreach (var file in group.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                output.AppendLine($"    {ctx.Workspace.ToRelative(file)}");
        }
        if (duplicates.Count > maxGroups)
            output.AppendLine($"…（另有 {duplicates.Count - maxGroups} 组未显示）");
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
