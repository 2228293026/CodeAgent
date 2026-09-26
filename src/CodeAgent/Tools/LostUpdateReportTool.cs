using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查共享计数器的丢失更新：对静态/实例共享字段做 ++、--、+= 或
/// 读改写（x = x + 1）却既没有 lock 也没有 Interlocked。</summary>
public sealed class LostUpdateReportTool : ITool
{
    public string Name => "lost_update_report";
    public string Description =>
        "只读检查共享计数器的丢失更新：对共享字段做 ++/--/+=/读改写却既没 lock 也没 Interlocked（多线程下会静默丢计数）。";

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

    /// <summary>证明该字段确实是"共享"的——要么 static，要么以下划线开头的实例字段。</summary>
    internal static bool IsShared(string typeDecl, string name) =>
        typeDecl.Contains("static", StringComparison.Ordinal) || name.StartsWith('_');

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var lostUpdate = new List<string>();
        var readModifyWrite = new List<string>();
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
            var text = string.Join("\n", lines);
            // 字段名 → 声明（含 static 标记）
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match f in Regex.Matches(text, @"(?<mods>(?:(?:public|private|protected|internal|static|readonly|volatile|const)\s+)+)(?<t>[\w<>\[\],\.]+)\s+(?<n>\w+)\s*(?:=|;)"))
            {
                if (f.Groups["mods"].Value.Contains("const", StringComparison.Ordinal))
                    continue;
                fields.TryAdd(f.Groups["n"].Value, f.Groups["mods"].Value);
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 上下几行里有没有 lock / Interlocked / Volatile —— 有就说明作者已经处理了
                var window = string.Join("\n", lines.Skip(Math.Max(0, i - 3)).Take(Math.Min(5, i + 1)));
                if (Regex.IsMatch(window, @"\block\s*[\(\{]|Interlocked\.|Volatile\.|Monitor\.") || Regex.IsMatch(lines[i], @"\block\s*[\(\{]|Interlocked\.|Volatile\.|Monitor\."))
                    continue;
                // ++ / -- / += / -=
                var inc = Regex.Match(trimmed, @"\b(?<n>\w+)\s*(?<op>\+\+|--|\+=|-=)");
                if (inc.Success && fields.TryGetValue(inc.Groups["n"].Value, out var decl) && IsShared(decl, inc.Groups["n"].Value)
                    && !Regex.IsMatch(trimmed, @"^\s*(?:int|long|var|var)\s"))
                    lostUpdate.Add($"{relative}:{lineNo} 共享字段 {inc.Groups["n"].Value} 直接 {inc.Groups["op"].Value}，附近既没有 lock 也没有 Interlocked（多线程下会丢更新，计数比实际少）");
                // 读改写 x = x + 1
                var rmw = Regex.Match(trimmed, @"\b(?<a>\w+)\s*=\s*(?<b>\w+)\s*[+\-]");
                if (rmw.Success
                    && rmw.Groups["a"].Value == rmw.Groups["b"].Value
                    && fields.TryGetValue(rmw.Groups["a"].Value, out var decl2)
                    && IsShared(decl2, rmw.Groups["a"].Value))
                    readModifyWrite.Add($"{relative}:{lineNo} 共享字段 {rmw.Groups["a"].Value} 做了\"读-改-写\"（{trimmed}）——读和写之间可能被打断，应改用 Interlocked 或 lock");
            }
        }
        if (files == 0)
            return "丢失更新报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"丢失更新报告: {files} 个 .cs 文件");
        Report(output, "共享字段自增无同步", lostUpdate, maxResults);
        Report(output, "共享字段读改写无同步", readModifyWrite, maxResults);
        if (lostUpdate.Count == 0 && readModifyWrite.Count == 0)
            output.AppendLine("未发现丢失更新问题");
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
