using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读检查工作区里的换行符是否混杂：同一仓库中既有 CRLF 又有 LF，
/// 会让整文件 diff 变色、让「我明明只改了一行」的判断失效。</summary>
public sealed class LineEndingMixedReportTool : ITool
{
    public string Name => "line_ending_mixed_report";
    public string Description =>
        "只读检查换行符混杂：同一仓库里 CRLF 与 LF 混用、单个文件内混用、以及无换行结尾的文件。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 30，最大 300）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 10，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var crlfFiles = new List<string>();
        var lfFiles = new List<string>();
        var mixedInFile = new List<string>();
        var noTrailingNewline = new List<string>();
        var unreadable = 0;
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file);
            // 只看文本类文件：二进制里的 0D 0A 毫无意义
            if (ext is not (".cs" or ".md" or ".json" or ".yml" or ".yaml" or ".txt" or ".props" or ".targets" or ".editorconfig" or ".gitignore"))
                continue;
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { unreadable++; continue; }
            catch (UnauthorizedAccessException) { unreadable++; continue; }
            files++;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            // UTF-8 BOM 跳过；剩余字节里的 0D 一定来自 CRLF
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var cr = 0;
            var lf = 0;
            var bareCr = 0;
            for (var i = start; i < bytes.Length; i++)
            {
                if (bytes[i] == 0x0D)
                {
                    if (i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
                    {
                        cr++;
                        i++;
                    }
                    else
                    {
                        bareCr++;
                    }
                }
                else if (bytes[i] == 0x0A)
                {
                    lf++;
                }
            }
            var crlfTotal = cr;
            var lfTotal = lf + bareCr;
            if (crlfTotal > 0 && lfTotal > 0)
                mixedInFile.Add($"{relative} 同一个文件里既有 CRLF({crlfTotal}) 又有 LF({lfTotal})");
            else if (crlfTotal > 0)
                crlfFiles.Add(relative);
            else if (lfTotal > 0)
                lfFiles.Add(relative);
            if (bytes.Length > 0 && bytes[^1] != 0x0A)
                noTrailingNewline.Add($"{relative} 末尾没有换行（POSIX 约定该有，会让 diff 显示 \\ No newline at end of file）");
        }
        if (files == 0)
            return "换行符报告: 未找到可检查的文本文件";
        var output = new StringBuilder();
        output.AppendLine($"换行符报告: {files} 个文本文件（LF {lfFiles.Count}，CRLF {crlfFiles.Count}，另有 {unreadable} 个读不了）");
        if (crlfFiles.Count > 0 && lfFiles.Count > 0)
            output.AppendLine($"  仓库换行混杂：{lfFiles.Count} 个 LF 与 {crlfFiles.Count} 个 CRLF 并存，diff 会整文件变色");
        Report(output, "文件内混用", mixedInFile, maxResults);
        Report(output, "CRLF 文件", crlfFiles, maxResults);
        Report(output, "LF 文件", lfFiles, maxResults);
        Report(output, "末尾无换行", noTrailingNewline, maxResults);
        if (mixedInFile.Count == 0 && noTrailingNewline.Count == 0
            && (crlfFiles.Count == 0 || lfFiles.Count == 0))
            output.AppendLine("未发现换行符问题");
        return output.ToString().TrimEnd();
    }

    private static void Report(StringBuilder output, string title, List<string> items, int maxResults)
    {
        if (items.Count == 0)
            return;
        output.AppendLine($"{title}: {items.Count}");
        foreach (var item in items.OrderBy(x => x, StringComparer.Ordinal).Take(maxResults))
            output.AppendLine($"  {item}");
        if (items.Count > maxResults)
            output.AppendLine($"  …（另有 {items.Count - maxResults} 处未显示）");
    }
}
