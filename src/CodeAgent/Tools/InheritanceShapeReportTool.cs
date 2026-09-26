using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查继承体系：可空引用类型字段未初始化、方法隐藏（new 关键字/同���非 override）、
/// 以及基类成员可被子类意外覆盖。</summary>
public sealed class InheritanceShapeReportTool : ITool
{
    public string Name => "inheritance_shape_report";
    public string Description =>
        "只读检查继承体系：非空引用字段未初始化、方法隐藏（new/无 override 的同名同签名）、以及基类成员可被意外覆盖。";

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
        var uninitializedNrt = new List<string>();
        var hiddenMethod = new List<string>();
        var virtualWithoutOverride = new List<string>();
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
            var seenSignatures = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // 可空引用类型（无 ? 的非基元引用类型字段）既未初始化也未在构造里赋值
                if (Regex.IsMatch(trimmed, @"^(public|internal|protected|private)\s+(?!.*\?)(string|[\w\.]+)\s+_\w+\s*;\s*$"))
                {
                    var field = Regex.Match(trimmed, @"_\w+").Value;
                    var assigned = lines.Any(l => l.Contains(field + " =", StringComparison.Ordinal) || l.Contains(field + "=", StringComparison.Ordinal));
                    if (!assigned && !lines.Any(l => l.Contains("null!", StringComparison.Ordinal)))
                        uninitializedNrt.Add($"{relative}:{lineNo} 非空引用字段 {field} 未见初始化（可能触发 CS8618）");
                }
                // 方法隐藏：非 override 的同名同参数列表方法在同文件里出现两次。
                // 收尾符要含 `=>`——表达式体成员（public new string Name() => "a";）很常见，
                // 只认 `{`/`;` 会把这一整类漏掉。
                var sig = Regex.Match(trimmed, @"^(?:(?:public|internal|protected|private|static|virtual|override|sealed|async|new)\s+)+[\w<>\[\],\.\?]+\s+(?<name>\w+)\s*\((?<args>[^)]*)\)\s*(\{|;|=>)");
                if (sig.Success)
                {
                    var name = sig.Groups["name"].Value;
                    var key = name + "(" + sig.Groups["args"].Value.Replace(" ", string.Empty) + ")";
                    // `new` 是**返回类型之前**的修饰符（public new string Name()），
                    // 不是紧挨方法名——按 `new\s+Name\(` 匹配永远找不到。
                    var isOverride = trimmed.Contains("override", StringComparison.Ordinal);
                    var isNew = Regex.IsMatch(trimmed, @"\bnew\b") && !isOverride;
                    if (isNew && !isOverride)
                        hiddenMethod.Add($"{relative}:{lineNo} 用 new 隐藏 {name}（多态按基类静态类型分发，易误调用）");
                    if (seenSignatures.TryGetValue(key, out var first) && !isOverride && !isNew)
                        virtualWithoutOverride.Add($"{relative}:{lineNo} {key} 与第 {first} 行同名同签名但既非 override 也非 new（成员隐藏）");
                    else
                        seenSignatures.TryAdd(key, lineNo);
                }
            }
        }
        if (files == 0)
            return "继承体系报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"继承体系报告: {files} 个 .cs 文件");
        Report(output, "非空字段未初始化", uninitializedNrt, maxResults);
        Report(output, "new 隐藏成员", hiddenMethod, maxResults);
        Report(output, "成员隐藏", virtualWithoutOverride, maxResults);
        if (uninitializedNrt.Count == 0 && hiddenMethod.Count == 0 && virtualWithoutOverride.Count == 0)
            output.AppendLine("未发现继承体系问题");
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
