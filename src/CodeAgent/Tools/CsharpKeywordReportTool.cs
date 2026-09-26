using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 C# 关键字误用：标识符用了 C# 关键字同形的变体、
/// 上下文关键字（value/var/async…）被当普通名字遮蔽语义，以及语言版本相关写法。</summary>
public sealed class CsharpKeywordReportTool : ITool
{
    public string Name => "csharp_keyword_report";
    public string Description =>
        "只读检查 C# 关键字：标识符与关键字仅大小写不同（value/Value）、上下文关键字遮蔽、@verbatim 用在非关键字标识符上。";

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
        // 保留关键字（含上下文关键字）：与它们仅大小写不同的标识符最容易误读
        var reserved = new List<string>
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const",
            "continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern",
            "false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface",
            "internal","is","lock","long","namespace","new","null","object","operator","out","override",
            "params","private","protected","public","readonly","ref","return","sbyte","sealed","short",
            "sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof",
            "uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
        };
        var contextList = new List<string>
        {
            "value","var","async","await","yield","record","where","from","group","join","into","let",
            "orderby","select","nameof","when","partial","global","alias",
        };
        // 两套比较器缺一不可：**精确**匹配 = 写了保留字（编译不过）；
        // 不分大小写匹配 = 只是**人**容易看错。混用会把 `String` 误判成编译错误。
        var keywords = new HashSet<string>(reserved, StringComparer.Ordinal);
        var keywordsAnyCase = new HashSet<string>(reserved.Concat(contextList), StringComparer.OrdinalIgnoreCase);
        var contextKeywords = new HashSet<string>(contextList, StringComparer.Ordinal);
        var caseCollision = new List<string>();
        var verbatimOnNonKeyword = new List<string>();
        var shadowing = new List<string>();
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
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                // 声明处出现关键字同形标识符
                foreach (Match m in Regex.Matches(trimmed, @"\b(?:class|struct|interface|enum|record|delegate|void|int|bool|string|long|double|float|decimal|char|byte|short|uint|ulong|var)\s+(?<n>[A-Za-z_]\w*)"))
                {
                    var name = m.Groups["n"].Value;
                    // 先按**精确大小写**匹配保留关键字：写成 `class int` 是编译错误。
                    // 用不分大小写匹配会把 `String`（完全合法）也判成编译错误，
                    // 那是误报——`String` 的真实风险只是**人**容易看错。
                    if (keywords.Contains(name))
                    {
                        caseCollision.Add($"{relative}:{i + 1} 声明名 {name} 就是 C# 保留关键字（无法编译）");
                        continue;
                    }
                    // 上下文关键字（value/var/async…）能编译，但会遮蔽语义
                    if (contextKeywords.Contains(name))
                    {
                        shadowing.Add($"{relative}:{i + 1} 声明名 {name} 与上下文关键字同形（能编译但极易误读）");
                        continue;
                    }
                    // 与关键字仅大小写不同：Value / STRING / String…
                    foreach (var kw in keywordsAnyCase)
                    {
                        if (kw.Length == name.Length && kw.Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            caseCollision.Add($"{relative}:{i + 1} 声明名 {name} 与关键字 {kw} 仅大小写不同");
                            break;
                        }
                    }
                }
                // @verbatim 用在非关键字标识符上：多余的 @
                foreach (Match m in Regex.Matches(trimmed, @"\bvar\s+@(?<n>[A-Za-z_]\w*)"))
                {
                    var name = m.Groups["n"].Value;
                    if (!keywords.Contains(name) && !contextKeywords.Contains(name))
                        verbatimOnNonKeyword.Add($"{relative}:{i + 1} @{name} 并不需要 @ 前缀（非关键字）");
                }
            }
        }
        if (files == 0)
            return "C# 关键字报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"C# 关键字报告: {files} 个 .cs 文件");
        Report(output, "与关键字同形/仅差大小写", caseCollision, maxResults);
        Report(output, "多余的 @ 前缀", verbatimOnNonKeyword, maxResults);
        Report(output, "可能被关键字遮蔽", shadowing, maxResults);
        if (caseCollision.Count == 0 && verbatimOnNonKeyword.Count == 0 && shadowing.Count == 0)
            output.AppendLine("未发现 C# 关键字问题");
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
