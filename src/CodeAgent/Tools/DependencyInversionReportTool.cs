using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查依赖倒置的实际缺口：字段/构造参数声明成具体类型而不是接口、
/// 直接 new 具体实现、以及 service 层直接依赖数据库/文件而不是抽象。</summary>
public sealed class DependencyInversionReportTool : ITool
{
    public string Name => "dependency_inversion_report";
    public string Description =>
        "只读检查依赖倒置的缺口：字段/构造参数声明成具体类型、直接 new 具体实现而不是注入、跨层直接依赖基础设施。";

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

    /// <summary>基础设施类名特征：这些是"被依赖方"，业务代码直接依赖它们就难以替换/测试。</summary>
    internal static readonly string[] InfraMarkers =
    {
        "SqlConnection", "SqlClient", "Npgsql", "MySql", "HttpClient", "FileStream",
        "StreamReader", "StreamWriter", "Process", "DbContext", "SocketsHttpHandler",
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
        var concreteField = new List<string>();
        var directNew = new List<string>();
        var infraCoupling = new List<string>();
        var files = 0;
        var interfaceNames = new HashSet<string>(StringComparer.Ordinal);
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
            foreach (Match i in Regex.Matches(text, @"\binterface\s+(\w+)"))
                interfaceNames.Add(i.Groups[1].Value);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 字段/构造参数声明成具体类型（非接口、非抽象、无基类泛型）
                var f = Regex.Match(trimmed, @"\b(?:private|protected|public|internal)\s+(?:readonly\s+)?(?<t>[A-Z]\w+)\s+(?<n>_\w+)\s*(?:;|=)");
                if (f.Success)
                {
                    var t = f.Groups["t"].Value;
                    if (!interfaceNames.Contains(t) && !Regex.IsMatch(t, @"^(?:List|Dictionary|HashSet|StringBuilder|Stopwatch|Task|ILogger|IReadOnly|ReadOnly)$"))
                        concreteField.Add($"{relative}:{lineNo} 字段 {f.Groups["n"].Value} 声明成具体类型 {t}（换成接口字段才能注入替身做单测）");
                }
                // 构造参数是具体实现
                var ctor = Regex.Match(trimmed, @"\bpublic\s+(?<n>\w+)\s*\(\s*(?:[^)]*,\s*)?(?<t>[A-Z]\w+)\s+(?<p>\w+)\s*(?:[,)=])");
                if (ctor.Success)
                {
                    var t = ctor.Groups["t"].Value;
                    if (!interfaceNames.Contains(t) && !Regex.IsMatch(t, @"^(?:string|int|long|bool|double|CancellationToken|Task|List|Dictionary|IReadOnlyList)$"))
                        directNew.Add($"{relative}:{lineNo} 构造参数 {ctor.Groups["p"].Value} 声明成具体类型 {t}（构造注入应面向接口/抽象）");
                }
                // 业务代码直接 new 基础设施
                var nw = Regex.Match(trimmed, @"\bnew\s+(?<t>\w+)\s*\(");
                if (nw.Success)
                {
                    var t = nw.Groups["t"].Value;
                    if (InfraMarkers.Contains(t, StringComparer.Ordinal) && !Regex.IsMatch(trimmed, @"(?i)(Test|Fake|Mock|Stub)"))
                        infraCoupling.Add($"{relative}:{lineNo} 直接 new {t}——换实现就得改这行（应通过接口注入）");
                }
            }
        }
        if (files == 0)
            return "依赖倒置报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"依赖倒置报告: {files} 个 .cs 文件，{interfaceNames.Count} 个接口");
        Report(output, "字段声明成具体类型", concreteField, maxResults);
        Report(output, "构造参数是具体类型", directNew, maxResults);
        Report(output, "直接 new 基础设施", infraCoupling, maxResults);
        if (concreteField.Count == 0 && directNew.Count == 0 && infraCoupling.Count == 0)
            output.AppendLine("未发现依赖倒置缺口");
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
