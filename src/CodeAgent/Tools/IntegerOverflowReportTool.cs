using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查整数运算的静默错误：C# 默认 unchecked，整数溢出**静默回绕**不抛异常；
/// 整数除法向零截断（不像数学取整）；以及窄化强制转换在大值上直接丢精度。</summary>
public sealed class IntegerOverflowReportTool : ITool
{
    public string Name => "integer_overflow_report";
    public string Description =>
        "只读检查整数运算静默错误：unchecked 下的溢出回绕（不抛异常）、整数除法向零截断、窄化强制转换丢精度。";

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

    /// <summary>比目标更宽的类型——从它们窄化过去可能丢精度。</summary>
    internal static readonly string[] WideTypes = { "long", "ulong", "int", "uint", "short", "ushort", "double", "float", "decimal" };

    /// <summary>窄化目标。</summary>
    internal static readonly string[] NarrowTypes = { "byte", "sbyte", "short", "ushort" };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var uncheckedArith = new List<string>();
        var truncatingDivide = new List<string>();
        var narrowingCast = new List<string>();
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
            // 项目是否显式开了 checked —— 开了就不用再提醒
            var checkedProject = false;
            var projectFile = Path.ChangeExtension(file, ".csproj");
            if (File.Exists(projectFile))
            {
                try
                {
                    var proj = await File.ReadAllTextAsync(projectFile, ct);
                    checkedProject = Regex.IsMatch(proj, @"<CheckForOverflowUnderflow>\s*true\s*</CheckForOverflowUnderflow>", RegexOptions.IgnoreCase);
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException) { }
            }
            if (checkedProject)
                continue;
            // `unchecked` 开的是一个**区域**，运算语句往往在它下面若干行。
            // 只看单行的话，`unchecked` 独占一行、下一行才是 `int c = a + b;`，
            // 这条规则对真实代码永远匹配不到。记录进入 unchecked 的括号深度。
            var uncheckedDepth = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                if (uncheckedDepth < 0 && Regex.IsMatch(trimmed, @"\bunchecked\b"))
                    uncheckedDepth = 0;
                var inUnchecked = uncheckedDepth >= 0;
                // 处于 unchecked 区域里的整数运算
                if (inUnchecked
                    && Regex.IsMatch(trimmed, @"\b(?:int|long|short|byte|sbyte|ushort)\s+\w+\s*=\s*[^;]*[-+*/%]"))
                    uncheckedArith.Add($"{relative}:{lineNo} unchecked 区域里做整数运算（溢出静默回绕：int.MaxValue + 1 会变成 int.MinValue，不抛异常）");
                // 整数除法：向零截断，和数学"向下取整"不同
                var div = Regex.Match(trimmed, @"\b(?:int|long|short)\s+(?<v>\w+)\s*=\s*(?<a>\w+)\s*/\s*(?<b>\w+)\s*;");
                if (div.Success && !Regex.IsMatch(trimmed, @"\bchecked\b"))
                    truncatingDivide.Add($"{relative}:{lineNo} 整数除法 {div.Groups["a"].Value} / {div.Groups["b"].Value} 向零截断（-7/2 == -3，不是 -4；分页、偏移量算错会静默少一页）");
                // 窄化强制转换
                var narrow = Regex.Match(trimmed, @"\(\s*(?<t>byte|sbyte|short|ushort)\s*\)\s*(?<v>[\w\.\(\)]+)\s*(?<op>[-+*/%]|\+\+|--)?");
                if (narrow.Success)
                {
                    var target = narrow.Groups["t"].Value;
                    var src = narrow.Groups["v"].Value;
                    // 源是字面量或已知的窄类型就不是问题
                    var isLiteral = int.TryParse(src, out _) || Regex.IsMatch(src, @"^\d+[uUlL]*$");
                    if (!isLiteral && !NarrowTypes.Contains(src))
                        narrowingCast.Add($"{relative}:{lineNo} 窄化成 {target}：{src}（超出 {target} 范围时按 unchecked 静默截断，值会变得完全不对）");
                }
                // 维护 unchecked 区域：按大括号深度进出
                if (uncheckedDepth >= 0)
                {
                    foreach (var ch in trimmed)
                    {
                        if (ch == '{') uncheckedDepth++;
                        else if (ch == '}') uncheckedDepth--;
                    }
                    if (uncheckedDepth < 0)
                        uncheckedDepth = -1;
                }
            }
        }
        if (files == 0)
            return "整数溢出报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"整数溢出报告: {files} 个 .cs 文件");
        Report(output, "unchecked 下的整数运算", uncheckedArith, maxResults);
        Report(output, "整数除法向零截断", truncatingDivide, maxResults);
        Report(output, "窄化强制转换", narrowingCast, maxResults);
        if (uncheckedArith.Count == 0 && truncatingDivide.Count == 0 && narrowingCast.Count == 0)
            output.AppendLine("未发现整数溢出问题");
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
