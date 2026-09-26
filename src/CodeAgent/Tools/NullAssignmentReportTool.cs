using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查可空性注解的陷阱：给非空参数赋 null、给非空字段赋 null、
/// 以及用 == null 判引用相等而不是走 ?. / ??。</summary>
public sealed class NullAssignmentReportTool : ITool
{
    public string Name => "null_assignment_report";
    public string Description =>
        "只读检查可空性陷阱：把 null 赋给非空参数/字段/返回值、catch 里吞掉后再解引用、以及非空返回路径里出现可能为 null 的值。";

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
        var nullToNonNull = new List<string>();
        var derefAfterNullCheck = new List<string>();
        var nonNullReturningNull = new List<string>();
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
            // 非空字段（string/int/bool，不带 ?）的名字
            var nonNullFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(string.Join("\n", lines), @"\b(?:string|int|long|bool|double|DateTime|List<[^>]+>)\s+(?<n>\w+)\s*(?:;|=)"))
            {
                var name = m.Groups["n"].Value;
                if (!name.StartsWith('?')) nonNullFields.Add(name);
            }
            // 非空返回值的方法名
            var nonNullMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(string.Join("\n", lines), @"\b(?:string|int|long|bool|double|DateTime)\s+(?<n>\w+)\s*\([^)]*\)\s*(?:=>|\{)"))
            {
                var name = m.Groups["n"].Value;
                if (!name.StartsWith("get", StringComparison.Ordinal) && !name.StartsWith("Is", StringComparison.Ordinal))
                    nonNullMethods.Add(name);
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;

                // 给非空字段/局部赋 null
                var assign = Regex.Match(trimmed, @"\b(?<n>\w+)\s*=\s*null\s*;");
                if (assign.Success && nonNullFields.Contains(assign.Groups["n"].Value) && !nonNullMethods.Contains(assign.Groups["n"].Value))
                    nullToNonNull.Add($"{relative}:{lineNo} {assign.Groups["n"].Value} 声明为非空却被赋 null（调用方拿到的是永远用不了的契约）");

                // if (x == null) { ... x.Method() ... } 忘了 null 判断返回
                var check = Regex.Match(trimmed, @"^if\s*\(\s*(?<v>\w+)\s*(?:==|!=)\s*null\s*\)");
                if (check.Success)
                {
                    var v = check.Groups["v"].Value;
                    var body = string.Join("\n", lines.Skip(i).Take(6));
                    if (Regex.IsMatch(body, $@"\b{Regex.Escape(v)}\s*==\s*null\s*\)\s*return\b") == false
                        && Regex.IsMatch(body, $@"\b{Regex.Escape(v)}\s*\.\s*\w"))
                        derefAfterNullCheck.Add($"{relative}:{lineNo} 判了 {v} == null 但分支里既没 return 也没 ?. 就直接 {v}.xxx");
                }

                // 非空返回类型的方法里直接 return null
                var ret = Regex.Match(trimmed, @"^return\s+null\s*;");
                if (ret.Success && nonNullMethods.Count > 0)
                {
                    var name = Regex.Match(Regex.Replace(lines[Math.Max(0, i - 1)], @"\s+", " "), @"\b(?<n>\w+)\s*\(").Groups["n"].Value;
                    if (nonNullMethods.Contains(name))
                        nonNullReturningNull.Add($"{relative}:{lineNo} {name}() 声明返回非空却在第 {lineNo} 行 return null");
                }
            }
        }
        if (files == 0)
            return "可空性报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"可空性报告: {files} 个 .cs 文件");
        Report(output, "非空变量被赋 null", nullToNonNull, maxResults);
        Report(output, "判空后仍直接解引用", derefAfterNullCheck, maxResults);
        Report(output, "非空方法 return null", nonNullReturningNull, maxResults);
        if (nullToNonNull.Count == 0 && derefAfterNullCheck.Count == 0 && nonNullReturningNull.Count == 0)
            output.AppendLine("未发现可空性陷阱");
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
