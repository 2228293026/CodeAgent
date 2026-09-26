using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查分支可达性：if/else 两侧都会返回、else 缺失但前面已 return、
/// 以及条件恒真/恒假（直接可静态判定）。</summary>
public sealed class DeadBranchReportTool : ITool
{
    public string Name => "dead_branch_report";
    public string Description =>
        "只读检查分支可达性：if 两个分支都 return（后面的 else 永远进不去）、缺 else 的悬空 return、条件恒真或恒假。";

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
        var bothReturn = new List<string>();
        var danglingReturn = new List<string>();
        var constantCondition = new List<string>();
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
            var Returns = new Regex(@"^\s*(return|throw)\b");
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                if (!Regex.IsMatch(trimmed, @"^if\s*\("))
                    continue;
                // 恒真/恒假条件：字面量比较、new 后立即判空、.Length >= 0
                if (Regex.IsMatch(trimmed, @"^\s*if\s*\(\s*(true|false)\s*\)")
                    || Regex.IsMatch(trimmed, @"\w+\s*!=\s*null\s*\)\s*$") && Regex.IsMatch(trimmed, @"\w+\s*!=\s*null\s*&&")
                    || Regex.IsMatch(trimmed, @"\w+\.Length\s*>=\s*0\s*\)"))
                    constantCondition.Add($"{relative}:{lineNo} 条件恒真/恒假（{TextUtil.TruncateLine(trimmed, 40)}）");
                // 从 if 起的整个 if/else：先定位 **if 体**的闭合花括号，再看紧跟的是不是 else。
                // 早先的写法一遇到 depth 归零就 break——那正是 if 体的收尾，
                // 于是永远看不到后面的 else，两分支都 return 一次都检测不出来。
                var depth = 0;
                var started = false;
                var ifBodyHasReturn = false;
                var ifBodyEnd = -1;
                for (var j = i; j < lines.Length && j < i + 200; j++)
                {
                    foreach (var ch in lines[j])
                    {
                        if (ch == '{') { depth++; started = true; }
                        else if (ch == '}') depth--;
                    }
                    if (!started)
                    {
                        if (j > i && depth == 0 && !lines[j].Trim().StartsWith("if", StringComparison.Ordinal))
                            break; // 同行结束的表达式体 if，没有块可分析
                        continue;
                    }
                    if (Returns.IsMatch(lines[j]))
                        ifBodyHasReturn = true;
                    if (depth <= 0)
                    {
                        ifBodyEnd = j;
                        break;
                    }
                }
                if (ifBodyEnd < 0)
                    continue;
                // 紧跟的 else（允许 `}` 与 `else` 之间有空行/注释）
                var elseAt = -1;
                for (var j = ifBodyEnd + 1; j < Math.Min(lines.Length, ifBodyEnd + 4); j++)
                {
                    var t = lines[j].Trim();
                    if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal))
                        continue;
                    if (t.StartsWith("else", StringComparison.Ordinal))
                        elseAt = j;
                    break;
                }
                if (elseAt < 0)
                {
                    // 无 else：if 分支 return 之后紧跟的语句只在条件为假时执行
                    if (ifBodyHasReturn)
                    {
                        var after = ifBodyEnd + 1 < lines.Length ? lines[ifBodyEnd + 1].Trim() : "";
                        if (after.Length > 0
                            && !after.StartsWith("//", StringComparison.Ordinal)
                            && !after.StartsWith("}", StringComparison.Ordinal)
                            && !after.StartsWith("catch", StringComparison.Ordinal)
                            && !after.StartsWith("finally", StringComparison.Ordinal))
                            danglingReturn.Add($"{relative}:{lineNo} if 缺少 else 分支，条件为真时下面这行不可达：{TextUtil.TruncateLine(after, 30)}");
                    }
                    continue;
                }
                // else 体是否 return/throw。
                // `else` 自成一行、块体在下一行时（elseInline 为空）不能就此判否——
                // 必须从 else 行往后扫花括号深度，块体里的 return 才算数。
                var elseReturns = false;
                var elseDepth = 0;
                var elseStarted = false;
                for (var j = elseAt; j < Math.Min(lines.Length, elseAt + 60); j++)
                {
                    var t = lines[j].Trim();
                    if (j == elseAt)
                    {
                        var inline = t.StartsWith("else", StringComparison.Ordinal) ? t[4..].TrimStart() : t;
                        if (Returns.IsMatch(inline))
                            elseReturns = true;
                    }
                    else if (Returns.IsMatch(t) && (elseStarted || elseDepth > 0))
                        elseReturns = true;
                    foreach (var ch in lines[j])
                    {
                        if (ch == '{') { elseDepth++; elseStarted = true; }
                        else if (ch == '}') elseDepth--;
                    }
                    if (elseStarted && elseDepth <= 0)
                        break;
                }
                if (ifBodyHasReturn && elseReturns)
                    bothReturn.Add($"{relative}:{lineNo} if 与 else 都 return，其后的代码不可达");
            }
        }
        if (files == 0)
            return "分支可达性报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"分支可达性报告: {files} 个 .cs 文件");
        Report(output, "两分支都 return", bothReturn, maxResults);
        Report(output, "悬空 return", danglingReturn, maxResults);
        Report(output, "恒真/恒假条件", constantCondition, maxResults);
        if (bothReturn.Count == 0 && danglingReturn.Count == 0 && constantCondition.Count == 0)
            output.AppendLine("未发现分支可达性问题");
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
