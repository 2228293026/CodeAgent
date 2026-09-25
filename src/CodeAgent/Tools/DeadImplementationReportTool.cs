using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查空实现：无逻辑的 public 方法、恒返回 null/0/false/空集合的方法、只调用 base 的重写。</summary>
public sealed class DeadImplementationReportTool : ITool
{
    public string Name => "dead_implementation_report";
    public string Description =>
        "只读检查空实现：无逻辑的 public 方法、恒返回 null/0/false/空集合的方法，以及只调用 base. 的重写。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 40，最大 400）" },
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
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 40), 1, 400);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var emptyBodies = new List<string>();
        var constantReturns = new List<string>();
        var baseOnly = new List<string>();
        var files = 0;
        var methods = 0;
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
                if (!Regex.IsMatch(line, @"^\s*(public|protected|internal)\s+.*\("))
                    continue;
                if (line.Contains('=') || line.Contains("=>", StringComparison.Ordinal)
                    && !Regex.IsMatch(line, @"\{"))
                    continue; // 属性/已带表达式体的成员
                if (Regex.IsMatch(line, @"\b(class|record|struct|interface|enum|new)\b"))
                    continue;
                var name = Regex.Match(line, @"([A-Za-z_][A-Za-z0-9_]*)\s*\(").Groups[1].Value;
                if (name.Length == 0)
                    continue;
                var body = BodyOf(lines, i);
                if (body.Length == 0)
                    continue;
                methods++;
                var lineNo = i + 1;
                if (Regex.IsMatch(body, @"^\s*base\.[A-Za-z_][A-Za-z0-9_]*\s*\([^;]*\)\s*;\s*$"))
                    baseOnly.Add($"{relative}:{lineNo} {name}() 只调用 base（继承链上的空转发）");
                else if (Regex.IsMatch(body, @"^\s*(return\s+)?(null|0|false|true|string\.Empty|default)\s*;\s*$"))
                    constantReturns.Add($"{relative}:{lineNo} {name}() 恒返回常量（未实现的桩）");
                else if (body.TrimEnd() == "throw new NotImplementedException();")
                    emptyBodies.Add($"{relative}:{lineNo} {name}() 未实现（NotImplementedException）");
            }
        }
        if (files == 0)
            return "空实现报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"空实现报告: {files} 个文件，{methods} 个方法");
        Report(output, "未实现", emptyBodies, maxResults);
        Report(output, "恒返回常量", constantReturns, maxResults);
        Report(output, "只调用 base", baseOnly, maxResults);
        if (emptyBodies.Count == 0 && constantReturns.Count == 0 && baseOnly.Count == 0)
            output.AppendLine("未发现空实现");
        return output.ToString().TrimEnd();
    }

    /// <summary>方法体内容（单行 {...} 或到配对闭括号为止）。</summary>
    internal static string BodyOf(string[] lines, int methodIndex)
    {
        var sb = new StringBuilder();
        var openOnSameLine = lines[methodIndex].TrimEnd().EndsWith('{');
        if (openOnSameLine)
        {
            var rest = lines[methodIndex].TrimEnd()[..^1];
            var close = rest.IndexOf('}');
            sb.Append(close >= 0 ? rest[..close] : rest).Append('\n');
        }
        var depth = openOnSameLine ? 1 : 0;
        for (var i = methodIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (trimmed == "}")
            {
                if (depth <= 1)
                    return sb.ToString();
                depth--;
            }
            else if (trimmed == "{")
            {
                depth++;
                continue;
            }
            sb.Append(trimmed).Append('\n');
        }
        return sb.ToString();
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
