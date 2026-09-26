using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查数值与单位：疑似应为常量的魔法数字、浮点直接比较（不用容差）、
/// 以及把字节数当字符数用这类单位混用。</summary>
public sealed class NumericLiteralReportTool : ITool
{
    public string Name => "numeric_literal_report";
    public string Description =>
        "只读检查数值与单位：条件里的魔法数字、浮点直接相等比较（应留容差）、以及字节/字符单位混用。";

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
        var magicNumbers = new List<string>();
        var floatEquality = new List<string>();
        var unitMixup = new List<string>();
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            files++;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var magic = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // 条件判断里的裸数字阈值：0/1/-1 与三位以内的小值不算魔法数
                foreach (Match m in Regex.Matches(trimmed, @"(?:<=|>=|==|!=|<|>)\s*(\d{2,})(?!\.\d)(?![fFdDmM])\b"))
                {
                    var value = m.Groups[1].Value.TrimStart('0');
                    if (value.Length is 0 or 1 or 2)
                        continue;
                    magic.Add(m.Groups[1].Value);
                }
                // 浮点直接相等比较：累加误差下几乎必然失败。
                // 要覆盖「字面量 == 变量」和「变量 == 字面量」两种写法。
                if (Regex.IsMatch(trimmed, @"\b(double|float|decimal)\b[^;]*[=!]=\s*[\d.]+[fFdDmM]?\s*;?")
                    || Regex.IsMatch(trimmed, @"[\d.]+[fFdDmM]?\s*[=!]=\s*\w+(?:\.\w+)*\s*;")
                    || Regex.IsMatch(trimmed, @"\w+\s*[=!]=\s*\d*\.?\d+[fFdDmM]?\s*;")
                    || Regex.IsMatch(trimmed, @"if\s*\([^)]*\w+\s*[=!]=\s*[\d.]+[fFdDmM]"))
                    floatEquality.Add($"{relative}:{lineNo} 浮点直接相等比较（应改用容差比较）");
                // 字节/字符单位混用：.Length 之后当字节用，或拿字节数当字符数截断
                if (Regex.IsMatch(trimmed, @"\w+\.Length\s*[*/+-]\s*\d+")
                    && Regex.IsMatch(trimmed, @"(?i)\b(byte|bytes|utf8|encoding|buffer|stream)\b"))
                    unitMixup.Add($"{relative}:{lineNo} 字符长度与字节单位混用（{TextUtil.TruncateLine(trimmed, 40)}）");
            }
            if (magic.Count > 0)
                magicNumbers.Add($"{relative} 条件里出现魔法数字 {string.Join("、", magic.OrderBy(x => x, StringComparer.Ordinal))}（应提为命名常量）");
        }
        if (files == 0)
            return "数值字面量报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"数值字面量报告: {files} 个 .cs 文件");
        Report(output, "魔法数字", magicNumbers, maxResults);
        Report(output, "浮点相等比较", floatEquality, maxResults);
        Report(output, "字节/字符混用", unitMixup, maxResults);
        if (magicNumbers.Count == 0 && floatEquality.Count == 0 && unitMixup.Count == 0)
            output.AppendLine("未发现数值字面量问题");
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
