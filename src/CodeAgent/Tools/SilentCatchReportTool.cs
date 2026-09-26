using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查异常吞噬的另一半：catch 之后不处理直接返回空、
/// catch 了却用返回默认值掩盖失败、以及 catch 变量从未被使用。</summary>
public sealed class SilentCatchReportTool : ITool
{
    public string Name => "silent_catch_report";
    public string Description =>
        "只读检查静默失败：catch 块里什么都不做就返回默认值、catch 变量没被使用、catch 后继续执行却没记日志也没抛。";

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
        var defaultReturn = new List<string>();
        var unusedVariable = new List<string>();
        var swallowAndContinue = new List<string>();
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
                var head = Regex.Match(trimmed, @"^catch\s*(?:\([^)]*\))?");
                if (!head.Success)
                    continue;
                var varName = Regex.Match(head.Value, @"\(\s*(?:[\w\.<>]+\s+)?(?<v>\w+)\s*\)").Groups["v"].Value;
                var body = new List<string>();
                var depth = 0;
                var started = false;
                // 从**本行**开始扫：单行写法 `catch (Exception) { }` 的花括号就在本行，
                // 只从下一行找会整个漏掉——而那恰恰是最该报的形态。
                for (var j = i; j < lines.Length && j < i + 20; j++)
                {
                    foreach (var ch in lines[j])
                    {
                        if (ch == '{') { depth++; started = true; }
                        else if (ch == '}') depth--;
                    }
                    body.Add(lines[j].Trim());
                    if (started && depth <= 0)
                        break;
                }
                if (!started)
                    continue;
                // 只看花括号**里面**的内容：连 catch 头一起判空会把单行写法漏掉
                var text = string.Join("\n", body);
                var open = text.IndexOf('{', StringComparison.Ordinal);
                var inner = open >= 0 ? text[(open + 1)..].TrimEnd('}').Trim() : text;
                // 真的什么也没做
                if (inner.Length == 0)
                    swallowAndContinue.Add($"{relative}:{lineNo} catch 块是空的（异常被完全丢弃，调用方以为成功）");
                // catch 变量没用到。必须在 **花括号内部**找：
                // 连 catch 头一起搜时 `catch (Exception ex)` 里的 `ex` 永远"被用到"，
                // 整条规则会静默失效。
                if (varName.Length > 0 && !Regex.IsMatch(varName, @"^_") && !Regex.IsMatch(inner, $@"\b{Regex.Escape(varName)}\b"))
                    unusedVariable.Add($"{relative}:{lineNo} catch 变量 {varName} 声明了却没被使用");
                // 静默返回默认值
                var ret = Regex.Match(inner, @"^return\s+(?:default|null|false|true|0|-1|string\.Empty|\[\]|Array\.Empty|new\s+\w+\[\])", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (ret.Success && !Regex.IsMatch(inner, @"(?i)(throw|log|warn|error|trace|debug)"))
                    defaultReturn.Add($"{relative}:{lineNo} catch 之后直接 return {ret.Value["return ".Length..]}，既不抛也不记（失败被伪装成成功）");
            }
        }
        if (files == 0)
            return "静默失败报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"静默失败报告: {files} 个 .cs 文件");
        Report(output, "空 catch", swallowAndContinue, maxResults);
        Report(output, "catch 变量未使用", unusedVariable, maxResults);
        Report(output, "catch 后返回默认值", defaultReturn, maxResults);
        if (swallowAndContinue.Count == 0 && unusedVariable.Count == 0 && defaultReturn.Count == 0)
            output.AppendLine("未发现静默失败");
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
