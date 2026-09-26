using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 [Obsolete] 标注：标注了却仍在同程序集内被调用、
/// 带 error: true 的成员仍被使用，以及标注未说明替代方案。</summary>
public sealed class ObsoleteUsageReportTool : ITool
{
    public string Name => "obsolete_usage_report";
    public string Description =>
        "只读检查 [Obsolete]：标注了却仍被本仓库调用、带 error: true 的成员仍在使用、标注里没有替代方案。";

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
        var stillCalled = new List<string>();
        var stillCalledError = new List<string>();
        var noReplacement = new List<string>();
        var files = 0;
        // 标记的成员名 → 是否 error、是否有替代说明；以及它的**声明位置**。
        // 声明位置必须排除：否则 `public void OldThing() { }` 这一行自己就
        // 会被当成「仍在使用」，把每个已标注成员都报一遍。
        var obsolete = new Dictionary<string, (bool IsError, bool HasReplacement)>(StringComparer.Ordinal);
        var declarations = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<(string File, int Line, string Member)>();
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
                if (!trimmed.Contains("Obsolete", StringComparison.Ordinal))
                    continue;
                var isError = Regex.IsMatch(trimmed, @"error\s*:\s*true");
                // 替代方案：标注字符串里带「改用 / 用 X / →」之类指向
                var text = Regex.Match(trimmed, @"Obsolete\s*\(\s*""(?<msg>[^""]*)""");
                var hasReplacement = text.Success &&
                    Regex.IsMatch(text.Groups["msg"].Value, @"改用|换成|使用|请用|→|->|instead|use\s", RegexOptions.IgnoreCase);
                // 紧随其后的成员声明行
                var memberLine = NextMemberLine(lines, i);
                if (memberLine.Length == 0)
                    continue;
                var name = Regex.Match(memberLine, @"(?:class|struct|record|interface|enum|void|int|string|bool|long|double|float|var)\s+(?<n>\w+)");
                if (!name.Success)
                    continue;
                var member = name.Groups["n"].Value;
                if (!obsolete.ContainsKey(member))
                    obsolete[member] = (isError, hasReplacement);
                pending.Add((relative, i + 2, member));
                if (!hasReplacement)
                    noReplacement.Add($"{relative}:{i + 1} [Obsolete] {member} 没写替代方案（调用方无从判断该换成什么）");
            }
        }
        // 第二遍：查找仍在使用
        foreach (var (file, line, member) in pending)
            declarations.Add($"{file}:{line}");
        if (obsolete.Count > 0)
        {
            foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
            {
                ct.ThrowIfCancellationRequested();
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                string[] lines;
                try { lines = await File.ReadAllLinesAsync(file, ct); }
                catch (OperationCanceledException) { throw; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                for (var i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Contains("Obsolete", StringComparison.Ordinal))
                        continue;
                    foreach (var (member, info) in obsolete)
                    {
                        if (!Regex.IsMatch(trimmed, @"\b" + Regex.Escape(member) + @"\b"))
                            continue;
                        // 跳过它自己的声明行：声明不算「使用」
                        if (declarations.Contains($"{relative}:{i + 1}"))
                            continue;
                        var report = $"{relative}:{i + 1} 仍在使用已标注 [Obsolete] 的 {member}";
                        if (info.IsError)
                            stillCalledError.Add(report + "（error: true，本应编译不过）");
                        else
                            stillCalled.Add(report);
                    }
                }
            }
        }
        if (files == 0)
            return "Obsolete 使用报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"Obsolete 使用报告: {files} 个 .cs 文件，{obsolete.Count} 个已标注成员");
        Report(output, "标注了却仍在用", stillCalled, maxResults);
        Report(output, "error:true 仍在用", stillCalledError, maxResults);
        Report(output, "未写替代方案", noReplacement, maxResults);
        if (stillCalled.Count == 0 && stillCalledError.Count == 0 && noReplacement.Count == 0)
            output.AppendLine("未发现 Obsolete 使用问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>取 [Obsolete] 之后的第一条非空、非属性行（成员声明行）。
    /// [Obsolete] 常见写法是属性独占一行，声明在下一行。</summary>
    private static string NextMemberLine(string[] lines, int attrLine)
    {
        for (var i = attrLine + 1; i < lines.Length && i < attrLine + 6; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith('[') || t.StartsWith("//", StringComparison.Ordinal)) continue;
            return t;
        }
        return string.Empty;
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
