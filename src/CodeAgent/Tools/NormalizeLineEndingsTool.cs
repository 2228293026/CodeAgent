using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>批量规范化文本文件换行符，复用 edit_file 的编码保留、备份和撤销能力。</summary>
public sealed class NormalizeLineEndingsTool : ITool
{
    private const int DefaultMaxFiles = 100;
    private const int MaxFiles = 500;
    private const int MaxTextBytes = 4 * 1024 * 1024;
    private readonly EditFileTool _editor = new();

    public string Name => "normalize_line_endings";
    public string Description =>
        "批量将文本文件换行符规范为 LF 或 CRLF，支持 dry-run、missing_ok、备份、错误继续和撤销。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["paths"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["minItems"] = 1,
                ["description"] = "要规范化的文件路径数组",
            },
            ["line_ending"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("lf", "crlf"),
                ["description"] = "目标换行风格，默认 lf",
            },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览，不写盘（默认 false）" },
            ["missing_ok"] = new JsonObject { ["type"] = "boolean", ["description"] = "文件不存在时跳过（默认 false）" },
            ["backup"] = new JsonObject { ["type"] = "boolean", ["description"] = "写入前创建 .bak 备份（默认 false）" },
            ["stop_on_error"] = new JsonObject { ["type"] = "boolean", ["description"] = "某个文件失败时立即停止（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多处理文件数（默认 100，最大 500）" },
        },
        ["required"] = new JsonArray("paths"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (args?["paths"] is not JsonArray paths || paths.Count == 0)
            throw new ToolException("缺少必填参数 paths（非空字符串数组）");
        var requestedLineEnding = ToolArgs.GetString(args, "line_ending");
        var lineEnding = string.IsNullOrWhiteSpace(requestedLineEnding)
            ? "lf"
            : requestedLineEnding.ToLowerInvariant();
        if (lineEnding is not ("lf" or "crlf"))
            throw new ToolException("line_ending 只能是 lf 或 crlf");
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var dryRun = ToolArgs.GetBool(args, "dry_run", false);
        var missingOk = ToolArgs.GetBool(args, "missing_ok", false);
        var backup = ToolArgs.GetBool(args, "backup", false);
        var stopOnError = ToolArgs.GetBool(args, "stop_on_error", false);
        var output = new StringBuilder();
        var changed = 0;
        var unchanged = 0;
        var skipped = 0;
        var failed = 0;
        var capped = paths.Count > maxFiles;
        for (var i = 0; i < Math.Min(paths.Count, maxFiles); i++)
        {
            ct.ThrowIfCancellationRequested();
            if (paths[i] is not JsonValue value || !value.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path))
            {
                failed++;
                Append(output, $"路径[{i}] 不是有效字符串");
                continue;
            }
            try
            {
                var full = ctx.Workspace.ResolveRead(path);
                if (!File.Exists(full))
                {
                    if (!missingOk)
                        throw new ToolException($"文件不存在: {path}");
                    skipped++;
                    Append(output, $"路径[{i}] {path}: 已跳过（文件不存在）");
                    continue;
                }
                var info = new FileInfo(full);
                if (info.Length > MaxTextBytes)
                    throw new ToolException($"文件超过 {MaxTextBytes / 1024 / 1024} MB，未规范化: {path}");
                var text = await TextUtil.ReadTextSmartAsync(full, ct);
                if (SkipDirs.LooksBinary(text))
                    throw new ToolException($"跳过二进制文件: {path}");
                var normalized = Normalize(text, lineEnding);
                if (string.Equals(text, normalized, StringComparison.Ordinal))
                {
                    unchanged++;
                    Append(output, $"路径[{i}] {path}: 已是目标换行风格");
                    continue;
                }
                var editArgs = new JsonObject
                {
                    ["path"] = path,
                    ["old_string"] = text,
                    ["new_string"] = normalized,
                    ["replace_all"] = true,
                    ["dry_run"] = dryRun,
                    ["backup"] = backup,
                    ["preserve_timestamp"] = true,
                };
                var result = await _editor.ExecuteAsync(editArgs, ctx, ct);
                changed++;
                Append(output, $"路径[{i}] {path}: {result}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ToolException ex) when (missingOk && ex.Message.StartsWith("文件不存在", StringComparison.Ordinal))
            {
                skipped++;
                Append(output, $"路径[{i}] {path}: 已跳过（文件不存在）");
            }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                failed++;
                if (stopOnError)
                    throw new ToolException($"路径[{i}] 规范化失败: {ex.Message}");
                Append(output, $"路径[{i}] {path} 失败: {ex.Message}");
            }
        }
        var header = $"换行规范化（{lineEnding.ToUpperInvariant()}）: 已变更 {changed} 个，无需变更 {unchanged} 个，跳过 {skipped} 个，失败 {failed} 个" +
                     (capped ? $"，达到 max_files={maxFiles} 上限" : "") + (dryRun ? "（dry-run，未写盘）" : "");
        return header + (output.Length == 0 ? "" : "\n" + output.ToString().TrimEnd());
    }

    private static string Normalize(string text, string lineEnding)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return lineEnding == "crlf" ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
    }

    private static void Append(StringBuilder output, string text)
    {
        if (output.Length > 0)
            output.AppendLine();
        output.Append(text);
    }
}
