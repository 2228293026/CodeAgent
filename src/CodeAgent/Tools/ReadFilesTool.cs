using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>批量读取多个文件：把常见的 read_file 参数复用到一组路径，减少模型往返次数。</summary>
public sealed class ReadFilesTool : ITool
{
    private const int MaxFiles = 20;
    private const int MaxTotalChars = 500_000;
    private readonly ReadFileTool _reader = new();

    public string Name => "read_files";
    public string Description =>
        "批量读取多个文本文件。最多 20 个路径；每个文件复用 read_file 的 offset/limit/tail/range 等参数，输出按路径分段并限制总字符数。单个文件失败默认记录错误并继续处理其他文件。";

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
                ["maxItems"] = MaxFiles,
                ["description"] = "要读取的文件路径数组，相对工作区根目录；最多 20 个",
            },
            ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "每个文件的起始行号（1 起）" },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "每个文件最多读取的行数（默认 300，最大 5000）" },
            ["tail"] = new JsonObject { ["type"] = "integer", ["description"] = "每个文件读取末尾 N 行（1-5000，优先于 offset/limit）" },
            ["head"] = new JsonObject { ["type"] = "integer", ["description"] = "每个文件读取开头 N 行（等价于 offset=1）" },
            ["range"] = new JsonObject { ["type"] = "string", ["description"] = "每个文件的行号范围，如 \"10-20\" 或 \"10:20\"" },
            ["no_line_numbers"] = new JsonObject { ["type"] = "boolean", ["description"] = "不带行号输出原文" },
            ["max_line_length"] = new JsonObject { ["type"] = "integer", ["description"] = "单行截断阈值（默认 2000，最大 50000）" },
            ["max_total_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "所有文件合计最大输出字符数（默认 100000，0=不限，最大 500000）" },
            ["stop_on_error"] = new JsonObject { ["type"] = "boolean", ["description"] = "遇到不存在/不可读文件时立即停止；默认 false，继续读取其他文件" },
        },
        ["required"] = new JsonArray("paths"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var paths = ToolArgs.GetStringList(args, "paths");
        if (paths is null || paths.Count == 0)
            throw new ToolException("缺少必填参数 paths（非空字符串数组）");

        var normalized = paths
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalized.Count == 0)
            throw new ToolException("paths 不能只包含空字符串");
        if (normalized.Count > MaxFiles)
            throw new ToolException($"paths 最多支持 {MaxFiles} 个文件（收到 {normalized.Count} 个）");

        var requestedTotal = ToolArgs.GetInt(args, "max_total_chars", 100_000);
        var totalLimit = requestedTotal == 0
            ? int.MaxValue
            : Math.Clamp(requestedTotal, 1_000, MaxTotalChars);
        var stopOnError = ToolArgs.GetBool(args, "stop_on_error", false);
        var output = new StringBuilder();

        foreach (var path in normalized)
        {
            ct.ThrowIfCancellationRequested();
            var header = $"===== {path} =====";
            if (output.Length > 0)
                output.AppendLine();
            if (output.Length + header.Length + 2 > totalLimit)
                break;
            output.AppendLine(header);

            try
            {
                var oneArgs = CloneReadArguments(args, path);
                var content = await _reader.ExecuteAsync(oneArgs, ctx, ct);
                ct.ThrowIfCancellationRequested();
                var remaining = totalLimit - output.Length;
                if (content.Length > remaining)
                {
                    const string marker = "\n…（批量读取输出已达到 max_total_chars）";
                    var room = remaining >= marker.Length ? remaining - marker.Length : remaining;
                    output.Append(content, 0, Math.Min(room, content.Length));
                    if (remaining >= marker.Length)
                        output.Append(marker);
                    else if (remaining > 0)
                        output.Append(marker, 0, remaining);
                    break;
                }
                output.AppendLine(content);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                if (stopOnError)
                    throw new ToolException($"读取文件 '{path}' 失败: {ex.Message}");
                var error = $"读取失败: {ex.Message}";
                if (output.Length + error.Length + 1 > totalLimit)
                {
                    output.Append("…（批量读取输出已达上限）");
                    break;
                }
                output.AppendLine(error);
            }
        }

        return output.ToString();
    }

    private static JsonObject CloneReadArguments(JsonObject? source, string path)
    {
        var result = new JsonObject { ["path"] = path };
        if (source is null)
            return result;
        foreach (var (key, value) in source)
        {
            if (key is "paths" or "max_total_chars" or "stop_on_error")
                continue;
            result[key] = value?.DeepClone();
        }
        return result;
    }
}
