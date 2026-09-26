using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 switch 的穿透：case 分支没写 break/return 就落到下一个 case、
/// 以及 default 不在最后导致阅读顺序反直觉。</summary>
public sealed class SwitchFallthroughReportTool : ITool
{
    public string Name => "switch_fallthrough_report";
    public string Description =>
        "只读检查 switch 穿透：case 分支没写 break/return/throw 就落进下一个 case、default 不在最后造成阅读顺序反直觉。";

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

    /// <summary>会终止一个 case 分支的结尾。缺了它们就会穿透到下一个 case。</summary>
    internal static readonly string[] Terminators =
    {
        "break;", "return", "throw", "continue;", "goto ", "yield break", "yield return", "};",
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
        var fallthrough = new List<string>();
        var defaultNotLast = new List<string>();
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
            var marks = new List<(int Line, string Text)>();
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal))
                    continue;
                if (Regex.IsMatch(t, @"^(?:case\s+.+|default)\s*:\s*$"))
                    marks.Add((i + 1, t));
            }
            if (marks.Count < 2)
                continue;
            for (var k = 0; k < marks.Count; k++)
            {
                var (markLine, markText) = marks[k];
                var isLast = k == marks.Count - 1;
                // 下一个 case 就是这一段的末尾。
                // markLine/endLine 都是 **1 基**，而 lines[] 是 0 基：
                // 必须用 `j < endLine - 1`，否则下一个 `case X:` 自己会被算进
                // 上一段的分支体里，于是**每个** case 都被判成"缺 break"。
                var endLine = isLast ? int.MaxValue : marks[k + 1].Line;
                var lastStatement = string.Empty;
                for (var j = markLine; j < endLine - 1 && j < lines.Length; j++)
                {
                    var t = lines[j].Trim();
                    if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal))
                        continue;
                    if (t.StartsWith("{", StringComparison.Ordinal) && lastStatement.Length == 0)
                        continue;
                    lastStatement = t;
                }
                // 空的 case（`case 1:` 紧接 `case 2:`）是有意的分组，不算穿透
                if (lastStatement.Length == 0)
                    continue;
                if (!isLast && !EndsWithTerminator(lastStatement))
                    fallthrough.Add($"{relative}:{markLine} {markText} 之后是 {lastStatement}，没有 break/return/throw——会继续执行下一个 {marks[k + 1].Text}（多数情况下是漏写 break）");
                if (markText == "default:" && !isLast)
                    defaultNotLast.Add($"{relative}:{markLine} default 出现在 switch 中间（第 {k + 1}/{marks.Count} 段）——阅读时容易误以为它兜底，实际只在前面的 case 都不匹配时才轮到");
            }
        }
        if (files == 0)
            return "switch 穿透报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"switch 穿透报告: {files} 个 .cs 文件");
        Report(output, "case 分支穿透", fallthrough, maxResults);
        Report(output, "default 不在最后", defaultNotLast, maxResults);
        if (fallthrough.Count == 0 && defaultNotLast.Count == 0)
            output.AppendLine("未发现 switch 穿透问题");
        return output.ToString().TrimEnd();
    }

    internal static bool EndsWithTerminator(string statement) =>
        Terminators.Any(t => statement.StartsWith(t, StringComparison.Ordinal));

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
