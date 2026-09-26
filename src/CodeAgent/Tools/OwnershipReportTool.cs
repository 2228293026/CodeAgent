using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查资源所有权：方法参数是 IDisposable 却没被释放、
/// 实现了 IDisposable 却没有 Dispose、以及依赖注入的字段由谁负责释放没写清。</summary>
public sealed class OwnershipReportTool : ITool
{
    public string Name => "ownership_report";
    public string Description =>
        "只读检查资源所有权：接收 IDisposable 参数却不释放、实现了 IDisposable 却没 Dispose、依赖注入的字段释放责任不明。";

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

    /// <summary>需要 Dispose 的类型。<c>MemoryStream</c> 不在内——它不占系统句柄。</summary>
    internal static readonly string[] Owned =
    {
        "FileStream", "StreamReader", "StreamWriter", "BinaryReader", "BinaryWriter",
        "SqlConnection", "HttpClient", "Process", "ZipArchive", "Timer",
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
        var unreleasedParam = new List<string>();
        var missingDispose = new List<string>();
        var ambiguousInjection = new List<string>();
        var files = 0;
        var ownedPattern = string.Join('|', Owned);
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
            // 实现了 IDisposable 却没有 Dispose 方法。
            // 按**类块**（大括号配对）判断，而不是"Dispose 后面跟着类名"——
            // 类名出现在声明处时那个顺序匹配永远不成立，于是对**任何**类都判成缺 Dispose。
            foreach (Match d in Regex.Matches(text, @"\bclass\s+(?<n>\w+)\s*:\s*[^\{{\n]*\bIDisposable\b"))
            {
                var cls = d.Groups["n"].Value;
                var declLine = text[..d.Index].Count(c => c == '\n') + 1;
                var body = ClassBody(lines, declLine - 1);
                if (body is null)
                    continue;
                if (!Regex.IsMatch(body, @"\b(?:public|protected|internal)\s+(?:override\s+|virtual\s+)?void\s+Dispose\s*\("))
                    missingDispose.Add($"{relative}:{declLine} 类 {cls} 实现了 IDisposable 却没有 Dispose（编译器不会报错，资源会静默泄漏）");
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 方法参数是可释放类型，方法体里却没有 using / Dispose / 所有权声明。
                // 修饰符**可选**（`void M(FileStream fs)` 就一个都没有），
                // 但必须整行是声明形态（以 `)` / `{` / `=>` 结尾），
                // 否则 `return Foo(x);` 这种调用会被当成方法声明。
                var sig = Regex.Match(trimmed, $@"^(?:(?:public|private|internal|protected|static|async|override|virtual|sealed|partial|extern|unsafe|new)\s+)*[\w\.<>\[\],\?]+\s+(?<m>\w+)\s*\((?<p>[^)]*)\)\s*(?:\{{|=>|$)");
                if (sig.Success)
                {
                    var ownedParam = Regex.Match(sig.Groups["p"].Value, $@"\b(?<t>{ownedPattern})\s+(?<v>\w+)");
                    if (ownedParam.Success)
                    {
                        var after = string.Join("\n", lines.Skip(i).Take(25));
                        var takesOwnership = Regex.IsMatch(after, $@"using\s*\([^)]*\b{Regex.Escape(ownedParam.Groups["v"].Value)}\b")
                            || Regex.IsMatch(after, $@"\b{Regex.Escape(ownedParam.Groups["v"].Value)}\s*\.\s*(?:Dispose|Close)\s*\(")
                            || Regex.IsMatch(after, $@"\b{Regex.Escape(ownedParam.Groups["v"].Value)}\s*=");
                        var handsOff = Regex.IsMatch(after, $@"\busing\s*\(\s*(?:var\s+)?{Regex.Escape(ownedParam.Groups["v"].Value)}\b")
                            || Regex.IsMatch(after, @"(?i)(does not own|not disposed|caller owns|调用方负责|由调用方释放)");
                        if (!takesOwnership && !handsOff)
                            unreleasedParam.Add($"{relative}:{lineNo} {sig.Groups["m"].Value}(…) 收到 {ownedParam.Groups["t"].Value} {ownedParam.Groups["v"].Value}，方法体里既没 using 也没 Dispose（所有权没说清）");
                    }
                }
                // 注入的可释放字段：这个类型自己到底该不该释放它？
                var fld = Regex.Match(trimmed, $@"\bprivate\s+readonly\s+(?<t>{ownedPattern})\s+(?<v>_\w+)\s*=");
                if (fld.Success && !Regex.IsMatch(text, $@"\bDispose[\s\S]{{0,200}}?{Regex.Escape(fld.Groups["v"].Value)}\s*\.Dispose\s*\("))
                    ambiguousInjection.Add($"{relative}:{lineNo} 注入的 {fld.Groups["t"].Value} {fld.Groups["v"].Value} 没写明由谁释放（容器注入的通常该由容器管，手工 new 的该由本类管）");
            }
        }
        if (files == 0)
            return "资源所有权报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"资源所有权报告: {files} 个 .cs 文件");
        Report(output, "IDisposable 却没 Dispose", missingDispose, maxResults);
        Report(output, "收到可释放参数但没说清所有权", unreleasedParam, maxResults);
        Report(output, "注入字段释放责任不明", ambiguousInjection, maxResults);
        if (missingDispose.Count == 0 && unreleasedParam.Count == 0 && ambiguousInjection.Count == 0)
            output.AppendLine("未发现资源所有权问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>取某个类型声明所在的大括号块（从声明行往后配对）。找不到返回 null。</summary>
    private static string? ClassBody(string[] lines, int declIndex)
    {
        var body = new List<string>();
        var depth = 0;
        var started = false;
        for (var j = declIndex; j < lines.Length && j < declIndex + 400; j++)
        {
            foreach (var ch in lines[j])
            {
                if (ch == '{') { depth++; started = true; }
                else if (ch == '}') depth--;
            }
            body.Add(lines[j]);
            if (started && depth <= 0)
                return string.Join("\n", body);
        }
        return started ? string.Join("\n", body) : null;
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
