using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查重复方法体：同一类里出现内容完全相同的方法、
/// 同一方法名在同一类里被重复声明，以及整段重复的注释（复制粘贴残留）。</summary>
public sealed class DuplicateMethodReportTool : ITool
{
    public string Name => "duplicate_method_report";
    public string Description =>
        "只读检查重复实现：同一类型内方法体逐字重复、同名方法重复声明、大段重复注释（复制粘贴残留）。";

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

    /// <summary>方法体去重的最少行数。短于此的方法体逐字重合多半是巧合。</summary>
    internal const int MinDuplicateLines = 4;

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var duplicateBodies = new List<string>();
        var duplicateNames = new List<string>();
        var duplicateComments = new List<string>();
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

            // 1) 同一类型内逐字重复的方法体（按方法体内容归一化后分组）
            var byBody = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            // 2) 同一类型内重复的方法名
            var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                // 修饰符是**可选**的：`void First()` 这种无修饰符的内部方法最常见，
                // 用 `+` 要求至少一个修饰符会把它们全部漏掉。
                var m = Regex.Match(trimmed, @"^(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|new|partial|\s)*[\w\.<>\[\],\?\s]+?\s+(?<name>\w+)\s*\(");
                if (!m.Success || Regex.IsMatch(trimmed, @"\b(if|for|foreach|while|switch|catch|using|lock|return|new|typeof|class|struct|record|interface|enum|namespace)\b"))
                    continue;
                var name = m.Groups["name"].Value;
                if (!byName.TryGetValue(name, out var ats))
                {
                    ats = new List<int>();
                    byName[name] = ats;
                }
                ats.Add(i + 1);

                var body = Body(lines, i, trimmed);
                if (body.Count < MinDuplicateLines)
                    continue;
                // 归一化时**去掉声明行**：`void First()` 与 `void Second()` 只有名字不同，
                // 把声明行算进 key 的话两个方法永远匹配不上——而「语句体逐字重复」
                // 才是我们要报的。
                var key = string.Join('\n', body.Skip(1).Select(l => Regex.Replace(l.Trim(), @"\s+", " ")));
                // 长度门槛只用来挡掉「空方法体」这类退化情形，行数门槛才是主判据。
                // 阈值定太高会把「三条短语句」这种真实的复制粘贴漏掉。
                if (key.Length < 20)
                    continue;
                if (!byBody.TryGetValue(key, out var locs))
                {
                    locs = new List<string>();
                    byBody[key] = locs;
                }
                locs.Add($"{relative}:{i + 1} {name}");
            }
            foreach (var (name, ats) in byName)
            {
                if (ats.Count > 1)
                    duplicateNames.Add($"{relative} 方法名 {name} 出现 {ats.Count} 次（行 {string.Join(", ", ats)}）");
            }
            foreach (var (key, locs) in byBody)
            {
                if (locs.Count > 1)
                    duplicateBodies.Add($"{locs.Count} 处方法体逐字重复：{string.Join(" / ", locs)}");
            }

            // 3) 复制粘贴残留：同一段连续注释（≥3 行）出现两次。
            // 注意判据是**整段重复**而不是「连续几行完全相同」——后者几乎不会发生，
            // 真实残留是「三行步骤注释被原样贴了第二遍」，行与行本身并不相同。
            var commentBlocks = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var i2 = 0;
            while (i2 < lines.Length)
            {
                if (!lines[i2].TrimStart().StartsWith("//"))
                {
                    i2++;
                    continue;
                }
                var run = 1;
                while (i2 + run < lines.Length && lines[i2 + run].TrimStart().StartsWith("//"))
                    run++;
                if (run >= 3)
                {
                    var key = string.Join('\n', lines.Skip(i2).Take(run).Select(l => l.Trim()));
                    if (!commentBlocks.TryGetValue(key, out var locs))
                    {
                        locs = new List<int>();
                        commentBlocks[key] = locs;
                    }
                    locs.Add(i2 + 1);
                }
                i2 += run;
            }
            foreach (var (_, locs) in commentBlocks)
            {
                if (locs.Count > 1)
                    duplicateComments.Add($"{relative} 同一段 {locs.Count} 行的注释出现多次（行 {string.Join(", ", locs)}，多半是复制粘贴后忘了改）");
            }
        }
        if (files == 0)
            return "重复实现报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"重复实现报告: {files} 个 .cs 文件（阈值 {MinDuplicateLines} 行）");
        Report(output, "方法体逐字重复", duplicateBodies, maxResults);
        Report(output, "方法名重复", duplicateNames, maxResults);
        Report(output, "重复注释块", duplicateComments, maxResults);
        if (duplicateBodies.Count == 0 && duplicateNames.Count == 0 && duplicateComments.Count == 0)
            output.AppendLine("未发现重复实现");
        return output.ToString().TrimEnd();
    }

    private static List<string> Body(string[] lines, int declLine, string decl)
    {
        if (decl.Contains("=>", StringComparison.Ordinal))
            return new List<string> { decl };
        var depth = 0;
        var started = false;
        for (var i = declLine; i < lines.Length && i < declLine + 600; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') { depth++; started = true; }
                else if (ch == '}') depth--;
            }
            if (started && depth <= 0)
                return lines.Skip(declLine).Take(i - declLine + 1).ToList();
        }
        return started ? lines.Skip(declLine).ToList() : new List<string>();
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
