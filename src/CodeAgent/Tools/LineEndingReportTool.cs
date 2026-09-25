using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读统计工作区文本文件的换行风格和 BOM 状态。</summary>
public sealed class LineEndingReportTool : ITool
{
    public string Name => "line_ending_report";
    public string Description =>
        "统计工作区文件的 LF、CRLF、混合换行、BOM 和二进制状态，不修改任何文件。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 5，最大 20）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 5000，最大 100000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var depth = Math.Clamp(ToolArgs.GetInt(args, "depth", 5), 0, 20);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 5_000), 1, 100_000);
        long lf = 0, crlf = 0, cr = 0, bom = 0, binary = 0, files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (files >= maxFiles)
                break;
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            var stats = await ScanAsync(file, ct);
            files++;
            if (stats.Binary) { binary++; continue; }
            lf += stats.Lf;
            crlf += stats.Crlf;
            cr += stats.Cr;
            if (stats.Bom) bom++;
        }
        var totalNewlines = lf + crlf + cr;
        var output = new StringBuilder();
        output.AppendLine($"换行报告: {files:N0} 个文件，{totalNewlines:N0} 个换行符");
        output.AppendLine($"  LF: {lf:N0}（{(totalNewlines == 0 ? 0 : lf * 100.0 / totalNewlines):F1}%）");
        output.AppendLine($"  CRLF: {crlf:N0}（{(totalNewlines == 0 ? 0 : crlf * 100.0 / totalNewlines):F1}%）");
        output.AppendLine($"  CR: {cr:N0}");
        output.AppendLine($"  UTF-8 BOM: {bom:N0}");
        output.AppendLine($"  二进制文件: {binary:N0}");
        if (totalNewlines > 0 && lf > 0 && crlf + cr > 0)
            output.AppendLine("  检测到混合换行风格。");
        return output.ToString().TrimEnd();
    }

    private static async Task<(long Lf, long Crlf, long Cr, bool Bom, bool Binary)> ScanAsync(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var buffer = new byte[64 * 1024];
        var first = new byte[3];
        var firstCount = 0;
        long lf = 0, crlf = 0, cr = 0;
        var previousCr = false;
        var binary = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (firstCount < first.Length)
                    first[firstCount++] = value;
                if (value == 0)
                    binary = true;
                if (value == (byte)'\n')
                {
                    if (previousCr)
                    {
                        crlf++;
                        cr--;
                    }
                    else
                        lf++;
                    previousCr = false;
                }
                else if (value == (byte)'\r')
                {
                    cr++;
                    previousCr = true;
                }
                else
                {
                    previousCr = false;
                }
            }
        }
        return (lf, crlf, cr, firstCount == 3 && first[0] == 0xEF && first[1] == 0xBB && first[2] == 0xBF, binary);
    }
}
