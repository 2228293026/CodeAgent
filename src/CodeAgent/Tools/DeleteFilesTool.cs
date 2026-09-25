using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>批量删除工作区内的多个文件，复用 delete_file 的安全和撤销检查。</summary>
public sealed class DeleteFilesTool : ITool
{
    private const int DefaultMaxFiles = 100;
    private const int MaxFiles = 500;
    private readonly DeleteFileTool _deleter = new();

    public string Name => "delete_files";
    public string Description =>
        "批量删除多个工作区文件。支持 dry_run、missing_ok、可选 .bak 备份、错误继续和最大文件数限制。";

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
                ["description"] = "要删除的文件路径数组",
            },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览全部删除，不写盘（默认 false）" },
            ["missing_ok"] = new JsonObject { ["type"] = "boolean", ["description"] = "文件不存在时跳过（默认 false）" },
            ["backup"] = new JsonObject { ["type"] = "boolean", ["description"] = "删除前为每个文件创建 .bak 备份（默认 false）" },
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
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var dryRun = ToolArgs.GetBool(args, "dry_run", false);
        var missingOk = ToolArgs.GetBool(args, "missing_ok", false);
        var backup = ToolArgs.GetBool(args, "backup", false);
        var stopOnError = ToolArgs.GetBool(args, "stop_on_error", false);
        var output = new StringBuilder();
        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        var capped = paths.Count > maxFiles;

        for (var i = 0; i < Math.Min(paths.Count, maxFiles); i++)
        {
            ct.ThrowIfCancellationRequested();
            if (paths[i] is not JsonValue value || !value.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path))
            {
                failed++;
                var message = $"路径[{i}] 不是有效字符串";
                if (stopOnError)
                    throw new ToolException(message);
                Append(output, message);
                continue;
            }
            try
            {
                var result = await _deleter.ExecuteAsync(new JsonObject
                {
                    ["path"] = path,
                    ["dry_run"] = dryRun,
                    ["missing_ok"] = missingOk,
                    ["backup"] = backup,
                }, ctx, ct);
                if (missingOk && result.Contains("已跳过", StringComparison.Ordinal))
                    skipped++;
                else
                    deleted++;
                Append(output, $"[{i}] {path}: {result}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                failed++;
                if (stopOnError)
                    throw new ToolException($"路径[{i}] 删除失败: {ex.Message}");
                Append(output, $"[{i}] {path} 失败: {ex.Message}");
            }
        }

        var header = $"批量删除: 已处理 {deleted + skipped + failed} 个，跳过 {skipped} 个，失败 {failed} 个" +
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
