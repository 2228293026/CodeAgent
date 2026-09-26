using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查浮点/金额精度陷阱：浮点字面量参与金额计算、
/// 浮点用 == 比较、Math.Round 没指定舍入模式、浮点 Parse 没给固定区域。</summary>
public sealed class FloatPrecisionReportTool : ITool
{
    public string Name => "float_precision_report";
    public string Description =>
        "只读检查浮点/金额精度陷阱：浮点字面量参与算术、浮点用 == 比较、Math.Round 未指定舍入模式、浮点 Parse 未固定区域。";

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

    /// <summary>浮点字面量：1.1 / 0.085 这类。整数 2 不在其内。</summary>
    internal const string FloatLiteral = @"(?<![\w.])\d+\.\d+(?![\w.])";

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var moneyLiteral = new List<string>();
        var floatEquality = new List<string>();
        var roundNoMode = new List<string>();
        var parseNoCulture = new List<string>();
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
            var text = string.Join("\n", lines);
            // 浮点类型的变量名——用来判断 == 两侧到底是不是浮点，
            // 否则 `name == "x"` 会被误报成浮点相等比较。
            var floatVars = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match f in Regex.Matches(text, @"\b(?:double|float)\s+(?<n>\w+)"))
                floatVars.Add(f.Groups["n"].Value);
            foreach (Match f in Regex.Matches(text, @"\bvar\s+(?<n>\w+)\s*=\s*[^;\n]*\b(?:double|float)\."))
                floatVars.Add(f.Groups["n"].Value);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 浮点字面量参与算术
                var lit = Regex.Match(trimmed, FloatLiteral);
                if (lit.Success && Regex.IsMatch(trimmed, @"[-+*/]\s*" + FloatLiteral + @"|" + FloatLiteral + @"\s*[-+*/]"))
                    moneyLiteral.Add($"{relative}:{lineNo} 用浮点字面量 {lit.Value} 参与算术（金额/累计用 double 会出现分位误差，应改用 decimal）");
                // 浮点用 == / != 比较
                if (Regex.IsMatch(trimmed, @"(?<![=!<>])==(?!=)|!=(?!=)"))
                {
                    foreach (var f in floatVars)
                    {
                        var e = Regex.Escape(f);
                        if (Regex.IsMatch(trimmed, $@"\b{e}\b\s*(?:==|!=)\s*[-\w.]|(?:==|!=)\s*[-]?\s*\b{e}\b"))
                        {
                            floatEquality.Add($"{relative}:{lineNo} 对浮点变量 {f} 用 ==/!= 比较（0.1+0.2 != 0.3，永远为 false；应比较差值或用容差）");
                            break;
                        }
                    }
                }
                // Math.Round 没有指定舍入模式
                if (Regex.IsMatch(trimmed, @"Math\.Round\s*\([^,()]+,\s*\d+\s*\)"))
                    roundNoMode.Add($"{relative}:{lineNo} Math.Round 没指定 MidpointRounding（默认是银行家舍入，.5 的结果与直觉相反）");
                // 浮点 Parse 没给固定区域
                if (Regex.IsMatch(trimmed, @"\b(?:double|float|decimal)\.Parse\s*\(\s*\w+\s*\)"))
                    parseNoCulture.Add($"{relative}:{lineNo} 浮点 Parse 没给 CultureInfo（逗号当小数点的机器上会直接抛异常或解析错）");
            }
        }
        if (files == 0)
            return "浮点精度报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"浮点精度报告: {files} 个 .cs 文件");
        Report(output, "浮点字面量参与算术", moneyLiteral, maxResults);
        Report(output, "浮点用 == 比较", floatEquality, maxResults);
        Report(output, "Math.Round 未指定舍入模式", roundNoMode, maxResults);
        Report(output, "浮点 Parse 未固定区域", parseNoCulture, maxResults);
        if (moneyLiteral.Count == 0 && floatEquality.Count == 0 && roundNoMode.Count == 0 && parseNoCulture.Count == 0)
            output.AppendLine("未发现浮点精度问题");
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
