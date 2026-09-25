using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>批量查看多个文件的元数据，复用 file_info 的编码、大小和哈希统计。</summary>
public sealed class FileInfosTool : ITool
{
    private const int DefaultMaxFiles = 100;
    private const int MaxFiles = 500;
    private readonly FileInfoTool _info = new();

    public string Name => "file_infos";
    public string Description =>
        "批量查看多个文件的大小、时间、编码、行数/单词数和可选 SHA256，支持 missing_ok、错误汇总及输出限制。";

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
                ["description"] = "要查看的文件路径数组",
            },
            ["count_lines"] = new JsonObject { ["type"] = "boolean", ["description"] = "统计文本行数（默认 true）" },
            ["count_words"] = new JsonObject { ["type"] = "boolean", ["description"] = "统计文本单词数（默认 false）" },
            ["hash"] = new JsonObject { ["type"] = "boolean", ["description"] = "计算并显示 SHA256（默认 false）" },
            ["missing_ok"] = new JsonObject { ["type"] = "boolean", ["description"] = "文件不存在时跳过（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多处理文件数（默认 100，最大 500）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 100000，最大 500000）" },
        },
        ["required"] = new JsonArray("paths"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (args?["paths"] is not JsonArray paths || paths.Count == 0)
            throw new ToolException("缺少必填参数 paths（非空字符串数组）");
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 100_000), 1_000, 500_000);
        var countLines = ToolArgs.GetBool(args, "count_lines", true);
        var countWords = ToolArgs.GetBool(args, "count_words", false);
        var hash = ToolArgs.GetBool(args, "hash", false);
        var missingOk = ToolArgs.GetBool(args, "missing_ok", false);
        var output = new StringBuilder();
        var success = 0;
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
                var result = await _info.ExecuteAsync(new JsonObject
                {
                    ["path"] = path,
                    ["count_lines"] = countLines,
                    ["count_words"] = countWords,
                    ["hash"] = hash,
                }, ctx, ct);
                success++;
                if (output.Length > 0)
                    output.AppendLine();
                output.AppendLine($"=== {path} ===");
                output.Append(result);
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
                Append(output, $"路径[{i}] {path} 失败: {ex.Message}");
            }
        }
        var header = $"批量文件信息: 成功 {success} 个，跳过 {skipped} 个，失败 {failed} 个" +
                     (capped ? $"，达到 max_files={maxFiles} 上限" : "");
        return Limit(header + (output.Length == 0 ? "" : "\n" + output.ToString().TrimEnd()), maxOutput);
    }

    private static void Append(StringBuilder output, string text)
    {
        if (output.Length > 0)
            output.AppendLine();
        output.Append(text);
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（批量文件信息输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
