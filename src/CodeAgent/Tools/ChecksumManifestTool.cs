using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>生成项目文件的 SHA256 校验清单，便于完整性校验和归档。</summary>
public sealed class ChecksumManifestTool : ITool
{
    private const int DefaultMaxFiles = 10_000;
    private const int MaxFiles = 50_000;

    public string Name => "checksum_manifest";
    public string Description =>
        "扫描项目文件并输出 SHA256 校验清单，支持扩展名/隐藏文件筛选、文件大小显示、扫描数量和输出限制。";

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
                ["description"] = "只检查这些扩展名，如 [\".dll\", \".json\"]",
            },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件/目录（默认 false）" },
            ["include_size"] = new JsonObject { ["type"] = "boolean", ["description"] = "在清单中显示字节大小（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 10000，最大 50000）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 100000，最大 500000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (File.Exists(root))
            throw new ToolException($"path 是文件: {requestedPath}（checksum_manifest 需要目录）");
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var includeSize = ToolArgs.GetBool(args, "include_size", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 100_000), 1_000, 500_000);
        var extensions = BuildExtensionFilter(args);
        var entries = new List<(string Path, long Size, string Hash)>();
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
            var hash = SkipDirs.ComputeFileSha256(file, ct);
            if (hash is null)
                continue;
            try { entries.Add((file, new FileInfo(file).Length, hash)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var output = new StringBuilder();
        output.AppendLine($"SHA256 清单: {entries.Count:N0} 个文件（扫描 {scanned:N0} 个）");
        if (capped)
            output.AppendLine($"达到 max_files={maxFiles} 上限，清单可能不完整");
        foreach (var entry in entries.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            var size = includeSize ? $"  {entry.Size:N0}" : "";
            output.AppendLine($"{entry.Hash.ToLowerInvariant()}  {size}  {ctx.Workspace.ToRelative(entry.Path)}".TrimEnd());
        }
        return Limit(output.ToString().TrimEnd(), maxOutput);
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

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（SHA256 清单输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
