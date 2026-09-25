using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>扫描项目中的常见临时文件和备份残留，辅助安全清理。</summary>
public sealed class FindTemporaryFilesTool : ITool
{
    private const int DefaultMaxFiles = 10_000;
    private const int MaxFiles = 50_000;

    public string Name => "find_temporary_files";
    public string Description =>
        "扫描项目中的 .bak、.tmp、.orig、.swp 等临时/备份文件，支持自定义后缀、隐藏文件、扫描和结果限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "扫描目录，默认工作区根目录" },
            ["suffixes"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "要匹配的文件后缀，默认 [\".bak\", \".tmp\", \".orig\", \".swp\"]",
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
        var suffixes = BuildSuffixFilter(args);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 5_000);
        var matches = new List<string>();
        var scanned = 0;
        var capped = false;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            if (scanned >= maxFiles)
            {
                capped = true;
                break;
            }
            scanned++;
            var name = Path.GetFileName(file);
            if (suffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                matches.Add(file);
        }
        var output = new StringBuilder();
        output.AppendLine($"临时文件扫描: 找到 {matches.Count:N0} 个（扫描 {scanned:N0} 个）");
        if (capped)
            output.AppendLine($"达到 max_files={maxFiles} 上限，结果可能不完整");
        foreach (var file in matches.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(maxResults))
        {
            try
            {
                output.AppendLine($"{ctx.Workspace.ToRelative(file)} ({TextUtil.FormatBytes(new FileInfo(file).Length)})");
            }
            catch (IOException) { output.AppendLine(ctx.Workspace.ToRelative(file)); }
        }
        if (matches.Count > maxResults)
            output.AppendLine($"…（另有 {matches.Count - maxResults} 个文件未显示）");
        return output.ToString().TrimEnd();
    }

    private static HashSet<string> BuildSuffixFilter(JsonObject? args)
    {
        var values = ToolArgs.GetStringList(args, "suffixes")?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (values is null || values.Count == 0)
            values = new List<string> { ".bak", ".tmp", ".orig", ".swp" };
        return values.Select(v => v.StartsWith('.') ? v.ToLowerInvariant() : "." + v.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
