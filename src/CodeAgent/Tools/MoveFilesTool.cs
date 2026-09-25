using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>批量移动/重命名多个文件，复用 move_file 的安全检查并汇总结果。</summary>
public sealed class MoveFilesTool : ITool
{
    private const int DefaultMaxFiles = 100;
    private const int MaxFiles = 500;
    private readonly MoveFileTool _mover = new();

    public string Name => "move_files";
    public string Description =>
        "批量移动或重命名文件。每个映射包含 source 和 destination，可单独设置 overwrite；支持全局 dry_run、错误继续和最大映射数。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["mappings"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["source"] = new JsonObject { ["type"] = "string", ["description"] = "源文件路径" },
                        ["destination"] = new JsonObject { ["type"] = "string", ["description"] = "目标文件路径" },
                        ["overwrite"] = new JsonObject { ["type"] = "boolean", ["description"] = "覆盖已存在目标" },
                    },
                    ["required"] = new JsonArray("source", "destination"),
                },
                ["minItems"] = 1,
                ["description"] = "移动映射数组，例如 [{\"source\":\"a.txt\",\"destination\":\"archive/a.txt\"}]",
            },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览全部移动，不写盘（默认 false）" },
            ["stop_on_error"] = new JsonObject { ["type"] = "boolean", ["description"] = "某个映射失败时立即停止（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多处理映射数（默认 100，最大 500）" },
        },
        ["required"] = new JsonArray("mappings"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (args?["mappings"] is not JsonArray mappings || mappings.Count == 0)
            throw new ToolException("缺少必填参数 mappings（非空对象数组）");
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var dryRun = ToolArgs.GetBool(args, "dry_run", false);
        var stopOnError = ToolArgs.GetBool(args, "stop_on_error", false);
        var output = new StringBuilder();
        var success = 0;
        var failed = 0;
        var capped = mappings.Count > maxFiles;

        for (var i = 0; i < Math.Min(mappings.Count, maxFiles); i++)
        {
            ct.ThrowIfCancellationRequested();
            if (mappings[i] is not JsonObject mapping)
            {
                failed++;
                var message = $"映射[{i}] 不是对象";
                if (stopOnError)
                    throw new ToolException(message);
                Append(output, message);
                continue;
            }
            var source = ToolArgs.GetString(mapping, "source");
            var destination = ToolArgs.GetString(mapping, "destination");
            try
            {
                var one = new JsonObject
                {
                    ["source"] = source,
                    ["destination"] = destination,
                    ["overwrite"] = ToolArgs.GetBool(mapping, "overwrite", false),
                    ["dry_run"] = dryRun,
                };
                var result = await _mover.ExecuteAsync(one, ctx, ct);
                success++;
                Append(output, $"映射[{i}] {source} → {destination}: {result}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                failed++;
                if (stopOnError)
                    throw new ToolException($"映射[{i}] 移动失败: {ex.Message}");
                Append(output, $"映射[{i}] 失败: {ex.Message}");
            }
        }

        var header = $"批量移动: 成功 {success} 个，失败 {failed} 个" +
                     (capped ? $"，达到 max_files={maxFiles} 上限" : "") + (dryRun ? "（dry-run，未写盘）" : "");
        return header + (output.Length == 0 ? "" : "\n" + output.ToString().TrimEnd());
    }

    private static void Append(StringBuilder output, string text)
    {
        const int limit = 100_000;
        if (output.Length >= limit)
            return;
        var remaining = limit - output.Length;
        if (text.Length + 1 <= remaining)
        {
            output.AppendLine(text);
            return;
        }
        const string marker = "…（结果已达到输出上限）";
        var room = remaining >= marker.Length ? remaining - marker.Length : remaining;
        output.Append(text, 0, Math.Min(room, text.Length));
        if (remaining >= marker.Length)
            output.AppendLine(marker);
        else
            output.Append(marker, 0, remaining);
    }
}
