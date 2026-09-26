using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查数据契约：序列化属性与字段不一致、字典键未指定、以及共享可变状态。</summary>
public sealed class DataContractReportTool : ITool
{
    public string Name => "data_contract_report";
    public string Description =>
        "只读检查数据契约：序列化的字段/属性混用、字典键类型选错（object 会全变字符串）、以及可变 static 状态。";

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
        var mixedMembers = new List<string>();
        var looseDictKey = new List<string>();
        var mutableStatic = new List<string>();
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
            // 逐个类型块：统计其中带 JsonPropertyName 的成员是字段还是属性
            var currentType = string.Empty;
            var fieldHits = 0;
            var propHits = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var trimmed = raw.Trim();
                var lineNo = i + 1;
                // 类型声明行常写成 `class Payload` + 下一行 `{`（本仓库的代码风格如此），
                // 所以**不能**要求同行出现 `{`，否则整个类型块永远不会被结算。
                var typeDecl = Regex.Match(trimmed, @"\b(class|record|struct)\s+(\w+)");
                if (typeDecl.Success)
                {
                    // 收尾上一个类型
                    if (currentType.Length > 0 && fieldHits > 0 && propHits > 0)
                        mixedMembers.Add($"{relative} {currentType} 同一类型里字段与属性混用序列化（命名策略会不一致）");
                    currentType = typeDecl.Groups[2].Value;
                    fieldHits = 0;
                    propHits = 0;
                }
                if (trimmed.Contains("[JsonPropertyName", StringComparison.Ordinal))
                {
                    // 必须**先**判属性：字段正则 `public T Name;` 也会匹配 `public T Name { get; set; }`，
                    // 顺序反了（或用 else if）就会把属性也记成字段，混用检查永远不触发。
                    var isProperty = trimmed.Contains("{ get;", StringComparison.Ordinal)
                        || trimmed.Contains("{get;", StringComparison.Ordinal);
                    if (isProperty)
                        propHits++;
                    else if (Regex.IsMatch(trimmed, @"\b(public|private|internal|protected)\s+(?!partial)[\w<>\[\],\?\s]*\s\w+\s*(=|;|\{)"))
                        fieldHits++;
                }
                // Dictionary<object, …>：反序列化后键全是字符串，类型信息丢失
                if (Regex.IsMatch(trimmed, @"(Dictionary|IDictionary)<\s*object\s*,"))
                    looseDictKey.Add($"{relative}:{lineNo} 字典键用 object（反序列化后所有键变字符串）");
                // 可变 static：跨调用共享状态，并发下会互相污染
                if (Regex.IsMatch(trimmed, @"\bstatic\s+(?!readonly|const|class|void|[A-Za-z]+\s+[A-Za-z]+\s*\()[A-Za-z_][\w<>\[\],\?\s]*\s+[A-Za-z_]\w*\s*(=|;|=>|\{|\+)")
                    && !trimmed.Contains("const ", StringComparison.Ordinal)
                    && !trimmed.Contains("readonly", StringComparison.Ordinal)
                    && !trimmed.StartsWith("//", StringComparison.Ordinal))
                    mutableStatic.Add($"{relative}:{lineNo} 可变 static 状态（跨调用共享，并发下互相污染）");
            }
            if (currentType.Length > 0 && fieldHits > 0 && propHits > 0)
                mixedMembers.Add($"{relative} {currentType} 同一类型里字段与属性混用序列化（命名策略会不一致）");
        }
        if (files == 0)
            return "数据契约报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"数据契约报告: {files} 个 .cs 文件");
        Report(output, "字段与属性混用", mixedMembers, maxResults);
        Report(output, "字典键用 object", looseDictKey, maxResults);
        Report(output, "可变 static 状态", mutableStatic, maxResults);
        if (mixedMembers.Count == 0 && looseDictKey.Count == 0 && mutableStatic.Count == 0)
            output.AppendLine("未发现数据契约问题");
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
