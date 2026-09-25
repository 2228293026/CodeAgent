using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>扫描项目中的零字节文件，辅助发现误生成或未完成文件。</summary>
public sealed class FindEmptyFilesTool : ITool
{
    private const int DefaultMaxFiles = 10_000;
    private const int MaxFiles = 50_000;

    public string Name => "find_empty_files";
    public string Description =>
        "扫描项目中的零字节文件，支持扩展名、隐藏文件、扫描数量和结果限制，默认跳过构建/缓存目录和符号链接。";

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
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 5_000);
        var extensions = BuildExtensionFilter(args);
        var matches = new List<string>();
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
                if (new FileInfo(file).Length == 0)
                    matches.Add(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var output = new StringBuilder();
        output.AppendLine($"空文件扫描: 找到 {matches.Count:N0} 个（扫描 {scanned:N0} 个）");
        if (capped)
            output.AppendLine($"达到 max_files={maxFiles} 上限，结果可能不完整");
        foreach (var file in matches.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(maxResults))
            output.AppendLine(ctx.Workspace.ToRelative(file));
        if (matches.Count > maxResults)
            output.AppendLine($"…（另有 {matches.Count - maxResults} 个文件未显示）");
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
