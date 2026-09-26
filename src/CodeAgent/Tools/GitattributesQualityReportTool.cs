using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 .gitattributes：缺少 text=auto / 换行与编码声明、
/// 规则重复或互相矛盾，以及把二进制文件误当文本处理。</summary>
public sealed class GitattributesQualityReportTool : ITool
{
    public string Name => "gitattributes_quality_report";
    public string Description =>
        "只读检查 .gitattributes：缺少 * text=auto、LF/CRLF 声明缺失或矛盾、重复规则、误处理二进制文件。";

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
        var file = Path.Combine(root, ".gitattributes");
        if (!File.Exists(file))
            return "gitattributes 质量报告: 仓库根目录没有 .gitattributes（换行/编码在不同机器上会不一致）";
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(file, ct); }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return "gitattributes 质量报告: 无法读取 .gitattributes"; }
        catch (UnauthorizedAccessException) { return "gitattributes 质量报告: 无权读取 .gitattributes"; }

        var missingTextAuto = new List<string>();
        var noEol = new List<string>();
        var contradictory = new List<string>();
        var duplicates = new List<string>();
        var binaryMistake = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasTextAuto = false;
        var hasEol = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var lineNo = i + 1;
            if (!seen.Add(line))
                duplicates.Add($"第 {lineNo} 行 {line}（与前面某行完全重复，后写的会覆盖前面的）");
            if (Regex.IsMatch(line, @"^\*\s+.*\btext\s*=\s*auto\b"))
                hasTextAuto = true;
            if (Regex.IsMatch(line, @"\b(eol\s*=\s*lf|eol\s*=\s*crlf)\b"))
                hasEol = true;
            // 同一模式上既有 eol=lf 又有 eol=crlf：取决于规则顺序，行为不可预期
            if (Regex.IsMatch(line, @"\btext\s*=\s*auto\b") && Regex.IsMatch(line, @"-text\b"))
                contradictory.Add($"第 {lineNo} 行 {line}（同时 text=auto 与 -text，结果取决于规则顺序）");
            // 扩展名像二进制却被声明成文本
            if (Regex.IsMatch(line, @"\*\.(png|jpg|jpeg|gif|zip|gz|exe|dll|pdf|ico|woff2?)\b") && Regex.IsMatch(line, @"\btext\b(?!.*\bbinary\b)"))
                binaryMistake.Add($"第 {lineNo} 行 {line}（二进制扩展名被当作文本处理，diff 会变成乱码）");
        }
        if (!hasTextAuto)
            missingTextAuto.Add("缺少 `* text=auto`（Git 会逐文件猜文本/二进制，猜错就出现 ^M 或整文件被当二进制）");
        if (!hasEol)
            noEol.Add("缺少 eol 声明（Windows 与 Linux 上换行会不一致，diff 整文件变色）");

        var output = new StringBuilder();
        output.AppendLine($"gitattributes 质量报告: {lines.Length} 行，{seen.Count} 条有效规则");
        Report(output, "缺少 text=auto", missingTextAuto, maxResults);
        Report(output, "缺少 eol 声明", noEol, maxResults);
        Report(output, "规则矛盾", contradictory, maxResults);
        Report(output, "重复规则", duplicates, maxResults);
        Report(output, "二进制误当文本", binaryMistake, maxResults);
        if (missingTextAuto.Count == 0 && noEol.Count == 0 && contradictory.Count == 0 && duplicates.Count == 0 && binaryMistake.Count == 0)
            output.AppendLine("未发现 .gitattributes 质量问题");
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
