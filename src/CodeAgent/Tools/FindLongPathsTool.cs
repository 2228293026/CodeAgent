using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>扫描可能触及 Windows 传统路径长度限制的项目路径。</summary>
public sealed class FindLongPathsTool : ITool
{
    private const int DefaultMaxFiles = 10_000;
    private const int MaxFiles = 50_000;

    public string Name => "find_long_paths";
    public string Description =>
        "扫描超过指定字符数的项目路径，辅助发现 Windows 传统路径限制和跨平台兼容性问题，支持扩展名和数量限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "扫描目录，默认工作区根目录" },
            ["min_length"] = new JsonObject { ["type"] = "integer", ["description"] = "最小路径字符数（默认 200，最大 10000）" },
            ["include_extensions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "只检查这些扩展名，如 [\".cs\", \".json\"]",
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
        var minLength = Math.Clamp(ToolArgs.GetInt(args, "min_length", 200), 1, 10_000);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 5_000);
        var extensions = BuildExtensionFilter(args);
        var matches = new List<(string Path, int Length)>();
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
            var length = file.Length;
            if (length >= minLength)
                matches.Add((file, length));
        }
        var output = new StringBuilder();
        output.AppendLine($"长路径扫描（至少 {minLength} 字符）: 找到 {matches.Count:N0} 个（扫描 {scanned:N0} 个）");
        if (capped)
            output.AppendLine($"达到 max_files={maxFiles} 上限，结果可能不完整");
        foreach (var item in matches.OrderByDescending(x => x.Length).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(maxResults))
            output.AppendLine($"{item.Length:N0} 字符  {ctx.Workspace.ToRelative(item.Path)}");
        if (matches.Count > maxResults)
            output.AppendLine($"…（另有 {matches.Count - maxResults} 个路径未显示）");
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
