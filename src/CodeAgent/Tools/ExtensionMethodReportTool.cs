using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查扩展方法：定义在非 static 类里（编译器 CS1106）、
/// 扩展方法的第一个参数不是 this、以及扩展方法定义在嵌套类里（不会被隐式 using 引入）。</summary>
public sealed class ExtensionMethodReportTool : ITool
{
    public string Name => "extension_method_report";
    public string Description =>
        "只读检查扩展方法：定义在非 static 类里、首个参数缺少 this、定义在嵌套/泛型受限类里。";

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
        var nonStaticClass = new List<string>();
        var missingThis = new List<string>();
        var nestedClass = new List<string>();
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
            // 每个类：是否 static、嵌套深度、起始行
            var classes = new List<(string Name, bool IsStatic, int Depth, int Start)>();
            var depth = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var cm = Regex.Match(trimmed, @"^(?<mods>(?:public|internal|private|protected|static|sealed|abstract|partial|new|\s)*)\b(class|struct)\s+(?<name>\w+)");
                if (cm.Success)
                {
                    var isStatic = Regex.IsMatch(cm.Groups["mods"].Value, @"\bstatic\b");
                    // Depth = 声明所在的大括号深度：0 = 顶层，≥1 = 嵌套在别的类型里
                    classes.Add((cm.Groups["name"].Value, isStatic, depth, i + 1));
                }
                // 大括号深度必须**每行**都更新。此前只在命中扩展方法时才累加，
                // 于是「外层类 - 嵌套类 - 方法」这种最常见的嵌套形态深度算出来是 0。
                depth += Regex.Matches(trimmed, @"{").Count - Regex.Matches(trimmed, @"}").Count;
                // 扩展方法：public static 返回值 名字(this ...)
                var em = Regex.Match(trimmed, @"\bpublic\s+static\s+(?<ret>[\w\.<>\[\],\?]+)\s+(?<name>\w+)\s*\(\s*(?<first>[^)]*)\)\s*(\{|=>)");
                if (!em.Success)
                    continue;
                var first = em.Groups["first"].Value;
                var isExtension = first.Contains("this ", StringComparison.Ordinal) || first.StartsWith("this ", StringComparison.Ordinal);
                var member = em.Groups["name"].Value;
                if (!isExtension)
                {
                    missingThis.Add($"{relative}:{i + 1} {member}({first}) 形似扩展方法但首参没有 this");
                    continue;
                }
                // 找它所在的类
                var owner = classes.LastOrDefault(c => c.Start <= i + 1);
                if (owner.Name is null)
                    continue;
                if (!owner.IsStatic)
                    nonStaticClass.Add($"{relative}:{i + 1} 扩展方法 {member} 定义在非 static 类 {owner.Name} 里（编译期 CS1106）");
                if (owner.Depth >= 1)
                    nestedClass.Add($"{relative}:{i + 1} 扩展方法 {member} 所在类 {owner.Name} 是嵌套类（不会被隐式 using 引入）");
            }
        }
        if (files == 0)
            return "扩展方法报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"扩展方法报告: {files} 个 .cs 文件");
        Report(output, "非 static 类里的扩展方法", nonStaticClass, maxResults);
        Report(output, "首参缺 this", missingThis, maxResults);
        Report(output, "定义在嵌套类", nestedClass, maxResults);
        if (nonStaticClass.Count == 0 && missingThis.Count == 0 && nestedClass.Count == 0)
            output.AppendLine("未发现扩展方法问题");
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
