using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查异常处理的粗糙之处：空的 catch、只写不读的异常变量、吞掉全部异常。</summary>
public sealed class ExceptionHandlingReportTool : ITool
{
    public string Name => "exception_handling_report";
    public string Description =>
        "只读审查异常处理：空 catch、捕获过宽（catch 全异常）、未使用或被丢弃的异常变量、缺少 await 的异步调用。";

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
        var emptyCatch = new List<string>();
        var broadCatch = new List<string>();
        var unusedVariable = new List<string>();
        var files = 0;
        var catches = 0;
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
                var line = lines[i];
                if (!line.Contains("catch", StringComparison.Ordinal))
                    continue;
                var match = Regex.Match(line, @"catch\s*(?:\(\s*(\w+)\s*(\w+)?\s*\))?");
                if (!match.Success)
                    continue;
                catches++;
                var lineNo = i + 1;
                var body = BodyOf(lines, i);
                if (match.Groups[1].Success && string.IsNullOrWhiteSpace(body))
                    emptyCatch.Add($"{relative}:{lineNo} 空 catch（异常被完全吞掉）");
                if (match.Groups[1].Success && IsBroadType(match.Groups[1].Value))
                    broadCatch.Add($"{relative}:{lineNo} 捕获过宽：{match.Groups[1].Value}（应至少区分取消/业务异常）");
                if (match.Groups[2].Success)
                {
                    var variable = match.Groups[2].Value;
                    // 变量在整个文件的后续内容里再没出现 → 捕获了却没用。
                    // 同一行的剩余部分也要算：单行 catch 的使用点正在那里。
                    var pattern = $@"\b{Regex.Escape(variable)}\b";
                    var used = Regex.IsMatch(line[(match.Index + match.Length)..], pattern);
                    for (var j = 0; !used && j < lines.Length; j++)
                    {
                        if (j == i)
                            continue;
                        if (Regex.IsMatch(lines[j], pattern))
                            used = true;
                    }
                    if (!used)
                        unusedVariable.Add($"{relative}:{lineNo} 捕获的 {variable} 从未被使用（应记录或重新抛出）");
                }
            }
        }
        if (files == 0)
            return "异常处理报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"异常处理报告: {files} 个文件，{catches} 个 catch");
        Report(output, "空 catch（异常被吞掉）", emptyCatch, maxResults);
        Report(output, "捕获过宽", broadCatch, maxResults);
        Report(output, "捕获变量未使用", unusedVariable, maxResults);
        if (emptyCatch.Count == 0 && broadCatch.Count == 0 && unusedVariable.Count == 0)
            output.AppendLine("未发现粗糙的异常处理");
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

    /// <summary>catch 块内去掉空行、纯注释与花括号后的有效内容。
    /// 必须同时考虑 catch 所在行的剩余部分：`catch (X) { Log(x); }` 这种单行写法很常见，
    /// 只看后续行会把有内容的 catch 误判成空 catch；而 `catch (X) { }` 去掉花括号后才是空。</summary>
    internal static string BodyOf(string[] lines, int catchIndex)
    {
        var sb = new StringBuilder();
        var catchLine = lines[catchIndex];
        var open = catchLine.IndexOf(')');
        if (open >= 0)
        {
            var rest = catchLine[(open + 1)..];
            sb.Append(StripBraces(rest)).Append('\n');
            // 单行 catch 的块在当行就闭合了：必须就此结束，
            // 否则会把紧随其后的下一条语句当成 catch 块的内容
            if (rest.Contains('}'))
                return sb.ToString();
        }
        for (var i = catchIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "{" || trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (trimmed == "}")
                break;
            sb.Append(trimmed).Append('\n');
        }
        return sb.ToString();
    }

    private static string StripBraces(string text) => text.Replace("{", string.Empty, StringComparison.Ordinal)
        .Replace("}", string.Empty, StringComparison.Ordinal)
        .Trim();

    /// <summary>判断是否属于"捕获过宽"的异常类型。</summary>
    internal static bool IsBroadType(string type) =>
        type is "Exception" or "System.Exception" or "object";
}
