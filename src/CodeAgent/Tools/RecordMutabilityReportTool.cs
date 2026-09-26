using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 record/类的可变性：record 用可变集合字段、
/// 名为 Mutable/Patch/Update 的 setter 破坏不变性，以及 record struct 的可变集合字段。</summary>
public sealed class RecordMutabilityReportTool : ITool
{
    public string Name => "record_mutability_report";
    public string Description =>
        "只读检查 record 不变性：record 含可变集合字段、Mutable/Patch/Update 命名破坏值语义、record struct 可变字段。";

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
        var mutableCollection = new List<string>();
        var mutatingSetter = new List<string>();
        var recordStructField = new List<string>();
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
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                var type = Regex.Match(trimmed, @"^(?<mods>(?:public|internal|private|protected|sealed|abstract|partial|new|\s)*)\b(?<kind>record\s+struct|record|class|struct)\s+(?<name>\w+)");
                if (!type.Success)
                    continue;
                var kind = type.Groups["kind"].Value;
                var typeName = type.Groups["name"].Value;
                if (kind != "record" && kind != "record struct")
                    continue;
                var body = Body(lines, i);
                // 可变集合字段。分两步走：先找集合类型标记，再往后读标识符并看终止符。
                // 一步写完的正则（类型 + 名字 + `=`/`;`/`{`）回溯太重，
                // 而且现代代码的主流写法是自动属性 `List<int> Values { get; init; } = new();`，
                // 终止符是 `{` 不是 `=`。
                foreach (Match t in Regex.Matches(body, @"\b(?:List|Dictionary|HashSet|Queue|Stack|Collection|IList|IDictionary|ICollection|IReadOnlyList|IReadOnlyDictionary)<[^>]*>|\b[A-Za-z_]\w*\[\]"))
                {
                    var rest = body[(t.Index + t.Length)..];
                    var nm = Regex.Match(rest, @"^\s+(?<f>\w+)\s*(?<term>[=;{])");
                    if (!nm.Success)
                        continue;
                    // 只看字段/属性：构造函数调用 `= new()` 已经被终止符 `=` 排除了
                    mutableCollection.Add($"{relative} record {typeName} 持有可变集合字段 {nm.Groups["f"].Value}（with 表达式复制的是引用，值语义失效）");
                }
                // 破坏不变性的命名成员。必须允许参数列表：`public void Add(int n) { }`
                // 是最常见的形态，光匹配 `名字 + {` 会漏掉带参数的方法。
                foreach (Match m in Regex.Matches(body, @"(?:public|internal)\s+[\w\.<>\[\],\?]+\s+(?<n>Mutate\w*|Patch\w*|Update\w*|Append\w*|Add\w*|Remove\w*|Clear\w*|Reset\w*)\s*(\(|\{|=>)"))
                    mutatingSetter.Add($"{relative} record {typeName} 有会改状态的成员 {m.Groups["n"].Value}（record 应当是不可变值）");
                if (kind == "record struct")
                    recordStructField.Add($"{relative} record struct {typeName} 是可变值类型（默认相等性按字段逐个比较，字段变动会影响哈希）");
            }
        }
        if (files == 0)
            return "record 可变性报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"record 可变性报告: {files} 个 .cs 文件");
        Report(output, "持有可变集合字段", mutableCollection, maxResults);
        Report(output, "改状态的成员", mutatingSetter, maxResults);
        Report(output, "record struct", recordStructField, maxResults);
        if (mutableCollection.Count == 0 && mutatingSetter.Count == 0 && recordStructField.Count == 0)
            output.AppendLine("未发现 record 可变性问题");
        return output.ToString().TrimEnd();
    }

    private static string Body(string[] lines, int typeLine)
    {
        var depth = 0;
        var started = false;
        for (var i = typeLine; i < lines.Length && i < typeLine + 400; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') { depth++; started = true; }
                else if (ch == '}') depth--;
            }
            if (started && depth <= 0)
                return string.Join("\n", lines.Skip(typeLine).Take(i - typeLine + 1));
        }
        return started ? string.Join("\n", lines.Skip(typeLine)) : string.Empty;
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
