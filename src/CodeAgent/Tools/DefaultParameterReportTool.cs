using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查默认参数值的陷阱：默认值是可变对象（所有调用共享同一个实例）、
/// 默认值是"现在"（每次调用都不同）、以及默认值是非编译期常量表达式。</summary>
public sealed class DefaultParameterReportTool : ITool
{
    public string Name => "default_parameter_report";
    public string Description =>
        "只读检查默认参数值陷阱：默认值是可变对象（所有调用共享一个实例）、默认值是 DateTime.Now 之类（每次调用都不同）、默认值是非常量表达式。";

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

    /// <summary>合法的编译期常量默认值——这些是安全的。</summary>
    internal static readonly string[] SafeDefaults = { "null", "true", "false" };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var sharedMutable = new List<string>();
        var perCallValue = new List<string>();
        var nonConstant = new List<string>();
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
                var lineNo = i + 1;
                // 方法/构造函数的参数列表，形如 `名字 = 默认值`。
                // 结尾要接受**行尾**：真实代码常把 `{` 放到下一行，
                // 只认 `{`/`=>`/`:` 会让整行签名匹配不到，规则对所有代码静默失效。
                var sig = Regex.Match(trimmed, @"^(?:public|private|internal|protected|static)\b[^=]*\((?<p>.*?)\)\s*(?:\{|=>|:|$)");
                if (!sig.Success)
                    continue;
                foreach (var p in SplitParams(sig.Groups["p"].Value))
                {
                    // 参数是"类型 名字 = 值"，类型也得吃掉。
                    // 只写 `名字 = 值` 时，真实参数（都带类型）一条都匹配不上。
                    var eq = Regex.Match(p.Trim(), @"^(?:[\w<>\[\],\.\?]+\s+)?(?<n>\w+)\s*=\s*(?<v>.+)$");
                    if (!eq.Success)
                        continue;
                    var name = eq.Groups["n"].Value;
                    var value = eq.Groups["v"].Value.Trim();
                    // new List<int>() / new byte[4] / new() / new Guid()
                    if (Regex.IsMatch(value, @"^new\s+\w*\s*[<\[\(]") || Regex.IsMatch(value, @"^new\s*\(\s*\)"))
                        sharedMutable.Add($"{relative}:{lineNo} 参数 {name} 的默认值是 new 出来的可变对象（默认值每次调用都会用同一个实例；一个调用改了它，后面所有调用都看得见）");
                    // DateTime.Now / Today / UtcNow
                    else if (Regex.IsMatch(value, @"^DateTime\.(?:Now|Today|UtcNow)\b"))
                        perCallValue.Add($"{relative}:{lineNo} 参数 {name} 默认取 DateTime.{Regex.Match(value, @"DateTime\.(Now|Today|UtcNow)").Groups[1].Value}（每次调用都是那一刻；想表达「没传就用今天」应让调用方显式传）");
                    else if (Regex.IsMatch(value, @"^(?:Guid\.NewGuid\s*\(|new\s+Guid\s*\()"))
                        perCallValue.Add($"{relative}:{lineNo} 参数 {name} 的默认值每次调用都不同（调用方无法依赖这个默认值做相等判断或缓存键）");
                    else if (Regex.IsMatch(value, @"^[A-Z]\w*\s*\(") || Regex.IsMatch(value, @"^\w+\.\w+\("))
                        nonConstant.Add($"{relative}:{lineNo} 参数 {name} 的默认值是非编译期常量表达式 {value}（接口成员用这种默认值会直接编译不过；具体方法里虽然能编，但语义容易和调用方预期不符）");
                }
            }
        }
        if (files == 0)
            return "默认参数报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"默认参数报告: {files} 个 .cs 文件");
        Report(output, "默认值是共享的可变对象", sharedMutable, maxResults);
        Report(output, "默认值每次调用都不同", perCallValue, maxResults);
        Report(output, "默认值不是常量表达式", nonConstant, maxResults);
        if (sharedMutable.Count == 0 && perCallValue.Count == 0 && nonConstant.Count == 0)
            output.AppendLine("未发现默认参数问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>按顶层逗号切分参数——泛型/数组内部的逗号不是分隔符。</summary>
    internal static List<string> SplitParams(string p)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c is '<' or '[' or '(') depth++;
            else if (c is '>' or ']' or ')') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(p[start..i]);
                start = i + 1;
            }
        }
        result.Add(p[start..]);
        return result;
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
