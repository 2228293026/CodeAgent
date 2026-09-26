using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查枚举/标志误用：枚举与数字字面量比较、
/// [Flags] 枚举用 == 判断、以及与 true/false 做 == 比较。</summary>
public sealed class EnumFlagReportTool : ITool
{
    public string Name => "enum_flag_report";
    public string Description =>
        "只读检查枚举/标志误用：枚举与数字字面量比较、[Flags] 枚举用 == 判断、布尔值与 true/false 比较。";

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

    /// <summary>不是枚举的普通类型——用来把 int/string/bool 排除出"枚举与数字比较"。</summary>
    internal static readonly string[] Primitives =
    {
        "int", "long", "short", "byte", "uint", "ulong", "sbyte", "ushort",
        "string", "bool", "double", "float", "decimal", "char", "object", "var", "void", "nint", "nuint",
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
        var enumVsLiteral = new List<string>();
        var flagsWithEquals = new List<string>();
        var boolCompare = new List<string>();
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
            // 带 [Flags] 的枚举
            var flagsEnums = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match f in Regex.Matches(text, @"\[Flags\]\s*(?:\[[^\]]*\]\s*)*(?:public\s+|internal\s+)?enum\s+(?<n>\w+)"))
                flagsEnums.Add(f.Groups["n"].Value);
            // 变量名 → 声明类型。只有"类型不是原始类型"的变量才可能是枚举，
            // 否则 `count == 1`（int）会被误报成枚举与数字比较。
            var varTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match v in Regex.Matches(text, @"\b(?<t>[A-Z]\w*)\s+(?<n>\w+)\s*(?:=|;)"))
            {
                if (Primitives.Contains(v.Groups["t"].Value))
                    continue;
                varTypes[v.Groups["n"].Value] = v.Groups["t"].Value;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                var isEquality = Regex.IsMatch(trimmed, @"(?<![=!<>])==(?!=)|!=(?!=)");
                // 枚举变量和数字字面量比较
                if (Regex.IsMatch(trimmed, @"(?<![=!<>])==\s*[0-9]+\b"))
                {
                    foreach (var (name, type) in varTypes)
                    {
                        if (Regex.IsMatch(trimmed, $@"\b{Regex.Escape(name)}\b\s*(?:==|!=)\s*[0-9]+\b"))
                        {
                            enumVsLiteral.Add($"{relative}:{lineNo} 枚举变量 {name}({type}) 和数字字面量比较（枚举顺序一改就静默变错；应写 {type}.成员名）");
                            break;
                        }
                    }
                }
                // [Flags] 枚举用 == 判断
                if (isEquality && !Regex.IsMatch(trimmed, @"[&|]"))
                {
                    foreach (var (name, type) in varTypes)
                    {
                        if (flagsEnums.Contains(type) && Regex.IsMatch(trimmed, $@"\b{Regex.Escape(name)}\b"))
                        {
                            flagsWithEquals.Add($"{relative}:{lineNo} [Flags] 枚举 {name} 用 ==/!= 判断（同时置了两个位时 == 为 false；应改用 & 位运算）");
                            break;
                        }
                    }
                }
                // 布尔与 true/false 比较
                if (Regex.IsMatch(trimmed, @"==\s*true\b|!=\s*false\b|false\s*==|true\s*!="))
                    boolCompare.Add($"{relative}:{lineNo} 布尔值与 true/false 直接比较（应直接 if (x)；且 x == true 对可空布尔会漏掉 null）");
            }
        }
        if (files == 0)
            return "枚举/标志报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"枚举/标志报告: {files} 个 .cs 文件");
        Report(output, "枚举与数字字面量比较", enumVsLiteral, maxResults);
        Report(output, "[Flags] 用 == 判断", flagsWithEquals, maxResults);
        Report(output, "布尔与 true/false 比较", boolCompare, maxResults);
        if (enumVsLiteral.Count == 0 && flagsWithEquals.Count == 0 && boolCompare.Count == 0)
            output.AppendLine("未发现枚举/标志误用");
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
