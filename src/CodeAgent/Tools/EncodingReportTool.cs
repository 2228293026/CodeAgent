using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>扫描项目文件编码，汇总 UTF-8、带 BOM UTF-8 和旧式编码分布。</summary>
public sealed class EncodingReportTool : ITool
{
    private const int DefaultMaxFiles = 10_000;
    private const int MaxFiles = 50_000;

    public string Name => "encoding_report";
    public string Description =>
        "扫描项目文件编码并按 UTF-8、UTF-8 BOM、GB18030/GBK 汇总，可筛选扩展名、隐藏文件并限制扫描数量。";

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
                ["description"] = "只检查这些扩展名，如 [\".cs\", \".txt\"]",
            },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件/目录（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 10000，最大 50000）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 50000，最大 200000）" },
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
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 50_000), 1_000, 200_000);
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
            var encoding = TextUtil.DetectFileEncoding(file) ?? "utf8";
            if (!groups.TryGetValue(encoding, out var files))
                groups[encoding] = files = new List<string>();
            files.Add(file);
        }
        var output = new StringBuilder();
        output.AppendLine($"编码报告: 扫描 {scanned:N0} 个文件，{groups.Count} 种编码");
        if (capped)
            output.AppendLine($"达到 max_files={maxFiles} 上限，结果可能不完整");
        foreach (var group in groups.OrderByDescending(g => g.Value.Count).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            output.AppendLine($"{DisplayEncoding(group.Key)}: {group.Value.Count:N0}");
            foreach (var file in group.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(100))
                output.AppendLine($"  {ctx.Workspace.ToRelative(file)}");
            if (group.Value.Count > 100)
                output.AppendLine($"  …（另有 {group.Value.Count - 100} 个文件未显示）");
        }
        if (groups.Count == 0)
            output.AppendLine("没有可统计的文件。");
        return Limit(output.ToString().TrimEnd(), maxOutput);
    }

    private static string DisplayEncoding(string value) => value switch
    {
        "utf8" => "UTF-8（无 BOM）",
        "utf8-bom" => "UTF-8 BOM",
        "gb18030" => "GB18030/GBK",
        _ => value,
    };

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
        const string marker = "\n…（编码报告输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
