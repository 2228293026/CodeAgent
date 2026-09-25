using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>在 glob 匹配的一组文件中执行精确文本替换，支持预览、逐文件报告和撤销栈。</summary>
public sealed class ReplaceInFilesTool : ITool
{
    private const int DefaultMaxFiles = 50;
    private const int MaxFiles = 500;
    private const int DefaultOutputChars = 100_000;
    private const int MaxOutputChars = 500_000;
    private readonly EditFileTool _editor = new();

    public string Name => "replace_in_files";
    public string Description =>
        "按 glob 模式在多个文件中做精确文本替换。默认替换每个文件中的全部匹配；支持 dry_run 预览、忽略大小写、错误继续处理和输出上限。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["patterns"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["minItems"] = 1,
                ["description"] = "文件 glob 模式数组，如 [\"**/*.cs\"]；无斜杠的模式会匹配任意目录层级",
            },
            ["old_string"] = new JsonObject { ["type"] = "string", ["description"] = "要替换的原文（必须精确匹配）" },
            ["new_string"] = new JsonObject { ["type"] = "string", ["description"] = "替换后的文本" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "搜索根目录，默认工作区根目录" },
            ["replace_all"] = new JsonObject { ["type"] = "boolean", ["description"] = "每个文件替换全部匹配（默认 true）；false 时每个文件最多替换一处" },
            ["case_insensitive"] = new JsonObject { ["type"] = "boolean", ["description"] = "忽略大小写匹配（默认 false）" },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览不写盘（默认 false）" },
            ["show_diff"] = new JsonObject { ["type"] = "boolean", ["description"] = "在结果中显示每个文件的差异（默认 false）" },
            ["stop_on_error"] = new JsonObject { ["type"] = "boolean", ["description"] = "某个文件未命中或写入失败时立即停止（默认 false，继续处理其他文件）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多处理的文件数（默认 50，最大 500）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 8，最大 32）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "结果最大字符数（默认 100000，最大 500000）" },
        },
        ["required"] = new JsonArray("patterns", "old_string", "new_string"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var patterns = ToolArgs.GetStringList(args, "patterns");
        if (patterns is null || patterns.Count == 0)
            throw new ToolException("缺少必填参数 patterns（非空 glob 数组）");
        var oldString = ToolArgs.GetString(args, "old_string");
        var newString = ToolArgs.GetString(args, "new_string");
        if (string.IsNullOrEmpty(oldString))
            throw new ToolException("缺少必填参数 old_string");
        if (oldString == newString)
            throw new ToolException("old_string 与 new_string 相同，无需修改");

        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (File.Exists(root))
            throw new ToolException($"path 是文件: {requestedPath}（请使用目录路径）");
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");

        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 0, 32);
        var outputLimit = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", DefaultOutputChars), 1_000, MaxOutputChars);
        var replaceAll = ToolArgs.GetBool(args, "replace_all", true);
        var caseInsensitive = ToolArgs.GetBool(args, "case_insensitive", false);
        var dryRun = ToolArgs.GetBool(args, "dry_run", false);
        var showDiff = ToolArgs.GetBool(args, "show_diff", false);
        var stopOnError = ToolArgs.GetBool(args, "stop_on_error", false);
        var regexes = patterns
            .Select(p => Glob.ToRegex(p.Contains('/') || p.Contains('\\') ? p : "**/" + p))
            .ToList();

        var output = new StringBuilder();
        var matched = 0;
        var succeeded = 0;
        var failed = 0;
        var capped = false;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!regexes.Any(r => r.IsMatch(relative)))
                continue;
            if (matched >= maxFiles)
            {
                capped = true;
                break;
            }
            matched++;
            try
            {
                // 预检查可读路径；真正写入仍由 EditFileTool 再次经过 Resolve + 沙箱检查。
                ctx.Workspace.ResolveRead(file);
                var one = new JsonObject
                {
                    ["path"] = relative,
                    ["old_string"] = oldString,
                    ["new_string"] = newString,
                    ["replace_all"] = replaceAll,
                    ["case_insensitive"] = caseInsensitive,
                    ["dry_run"] = dryRun,
                    ["show_diff"] = showDiff,
                };
                var result = await _editor.ExecuteAsync(one, ctx, ct);
                AppendLimited(output, $"{relative}: {result}", outputLimit);
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                failed++;
                if (stopOnError)
                    throw new ToolException($"处理文件 '{relative}' 失败: {ex.Message}");
                AppendLimited(output, $"{relative}: 失败 - {ex.Message}", outputLimit);
            }
        }

        var header = $"批量替换: 匹配 {matched} 个文件，成功 {succeeded} 个，失败 {failed} 个" +
                     (capped ? $"，达到 max_files={maxFiles} 上限" : "") + (dryRun ? "（dry-run，未写盘）" : "");
        return header + (output.Length == 0 ? "" : "\n" + output.ToString().TrimEnd());
    }

    private static void AppendLimited(StringBuilder output, string text, int limit)
    {
        if (output.Length >= limit)
            return;
        var remaining = limit - output.Length;
        if (text.Length + 1 <= remaining)
        {
            output.AppendLine(text);
            return;
        }
        const string marker = "…（结果已达到 max_output_chars）";
        var room = remaining >= marker.Length ? remaining - marker.Length : remaining;
        output.Append(text, 0, room);
        if (remaining >= marker.Length)
            output.AppendLine(marker);
        else
            output.Append(marker, 0, remaining);
    }
}
