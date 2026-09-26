using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查异常处理的反模式：只 catch 不处理、catch(Exception) 后吞掉、
/// catch 后没有重新抛出也没有记录、以及 catch 了却抛不出对应异常的类型。</summary>
public sealed class SwallowedExceptionReportTool : ITool
{
    public string Name => "swallowed_exception_report";
    public string Description =>
        "只读检查被吞掉的异常：空的 catch 块、catch 后既不 throw 也不记录、catch 了却无法对应抛出的异常类型。";

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
        var emptyCatches = new List<string>();
        var silentCatches = new List<string>();
        var mismatchedThrows = new List<string>();
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
                var c = Regex.Match(trimmed, @"^catch\s*(?:\((?<t>[^)]*)\))?");
                if (!c.Success)
                    continue;
                var caught = c.Groups["t"].Value.Trim();
                var lineNo = i + 1;
                // 找 catch 的大括号体：'{' 常在**下一行**，且必须用括号配平取真正的块内容。
                // 早先用 body.IndexOf('{') + 切片，空 catch 会切出只剩 "}" 的"块体"，
                // 于是"完全吞掉"被误报成"既不抛也不记录"。
                var bodyStart = -1;
                var end = -1;
                var depth = 0;
                for (var j = i; j < lines.Length && j < i + 60; j++)
                {
                    for (var k = 0; k < lines[j].Length; k++)
                    {
                        if (lines[j][k] == '{')
                        {
                            depth++;
                            if (bodyStart < 0) bodyStart = j * 100000 + k;   // 行号*大常数+列号，便于还原
                        }
                        else if (lines[j][k] == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                end = j * 100000 + k;
                                break;
                            }
                        }
                    }
                    if (end >= 0) break;
                }
                if (bodyStart < 0 || end < 0)
                    continue;
                var startLine = bodyStart / 100000;
                var startCol = bodyStart % 100000;
                var endLine = end / 100000;
                var endCol = end % 100000;
                var real = endLine == startLine
                    ? lines[startLine][(startCol + 1)..endCol]
                    : string.Join("\n", lines.Skip(startLine).Take(endLine - startLine + 1))[..^1];
                real = Regex.Replace(real, @"(?s)//.*|/\*.*?\*/", "").Trim();
                if (real.Length == 0)
                    emptyCatches.Add($"{relative}:{lineNo} catch ({caught}) 块是空的（异常被完全吞掉）");
                else if (!Regex.IsMatch(real, @"\bthrow\b")
                         && !Regex.IsMatch(real, @"(Console\.(Error|Write)|Log|Trace|Debug|logger|_log)"))
                    // 判据只有两条：**没有重新抛出、没有留下记录**。
                    // 早先还要求"块里一句有效语句都没有"，结果 `catch (IOException e) { e.GetType(); }`
                    // 这种纯空转被放过——恰恰是最该报的一类。
                    silentCatches.Add($"{relative}:{lineNo} catch ({caught}) 既不重新抛出也不记录（异常就此消失）");
                // catch 了却抛出与捕获类型无关的异常。
                // 判断"是否宽泛基类"必须用**完整名**比较：`FileNotFoundException` 里
                // 也含 "Exception" 子串，早先的 Contains 判断让它整个跳过了检查。
                if (thr2(caught)) continue;
                var thr = Regex.Match(real, @"\bthrow\s+new\s+(?<t2>\w+)");
                if (thr.Success && caught.Length > 0)
                {
                    var t2 = thr.Groups["t2"].Value;
                    if (!t2.Contains(caught, StringComparison.Ordinal) && !caught.Contains(t2, StringComparison.Ordinal))
                        mismatchedThrows.Add($"{relative}:{lineNo} catch ({caught}) 却抛出 {t2}（原本捕获的异常类型会被丢失）");
                }
            }
        }
        if (files == 0)
            return "异常吞噬报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"异常吞噬报告: {files} 个 .cs 文件");
        Report(output, "空 catch 块", emptyCatches, maxResults);
        Report(output, "既不抛也不记录", silentCatches, maxResults);
        Report(output, "catch 类型与抛出类型不符", mismatchedThrows, maxResults);
        if (emptyCatches.Count == 0 && silentCatches.Count == 0 && mismatchedThrows.Count == 0)
            output.AppendLine("未发现被吞掉的异常");
        return output.ToString().TrimEnd();
    }

    /// <summary>catch 的类型是否"宽泛到不需要检查重抛一致性"。
    /// 必须**完整名**比较：<c>FileNotFoundException</c> 里也含 "Exception" 子串，
    /// 用 Contains 判断会让整项检查对所有具体异常类型静默失效。</summary>
    private static bool thr2(string caught) =>
        caught is "Exception" or "System.Exception" or "SystemException"
            or "System.SystemException" or "ApplicationException" or "System.ApplicationException"
            or "" || caught.Contains('|', StringComparison.Ordinal);

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
