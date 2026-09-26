using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查接口实现的签名漂移：实现里**丢掉了**接口上的可选参数默认值、
/// 少了 ref/out/in、返回类型写成了协变体。三者都会让调用方的具名实参直接编译不过。</summary>
public sealed class OptionalParamDroppedReportTool : ITool
{
    public string Name => "optional_param_dropped_report";
    public string Description =>
        "只读检查接口实现与接口声明的签名漂移：实现漏了可选参数的默认值、少了 ref/out/in、返回类型被改成协变类型。";

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
        var lostDefaults = new List<string>();
        var lostModifiers = new List<string>();
        var covariantReturn = new List<string>();
        var files = 0;
        // 先把接口里的方法签名收齐：名字 -> (参数个数, 带默认值的参数个数, 形参修饰符)
        var declared = new Dictionary<string, List<(int count, int defaults, string mods)>>(StringComparer.Ordinal);
        var implemented = new Dictionary<string, List<(string file, int line, int count, int defaults, string mods, string ret)>>(StringComparer.Ordinal);
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
            // 当前是否在接口体内。必须在**每个类型声明处**重新判定：
            // 早期版本只在遇到第一个 interface 时置 true、之后再也不复位，
            // 于是同一个文件里 interface 后面的 class 方法也被当成接口声明，
            // 实现与"自己"比对，报告全空。
            var isInterface = false;
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var raw = lines[lineIndex];
                var trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                var typeDecl = Regex.Match(trimmed, @"^\s*(?:public|internal|private|protected|abstract|sealed|static|partial|\s)*\b(?<kind>class|interface|struct|record)\s+\w+");
                if (typeDecl.Success)
                {
                    isInterface = typeDecl.Groups["kind"].Value == "interface";
                    continue;
                }
                var m = Regex.Match(trimmed, @"^\s*(?:public|internal|protected|private)?\s*(?:static\s+|virtual\s+|override\s+|abstract\s+|sealed\s+)*(?<ret>[\w\.<>\[\],\?]+)\s+(?<name>\w+)\s*\((?<args>[^)]*)\)");
                if (!m.Success)
                    continue;
                var name = m.Groups["name"].Value;
                var argText = m.Groups["args"].Value;
                var count = argText.Trim().Length == 0 ? 0 : argText.Split(',').Length;
                var defaults = Regex.Matches(argText, @"=").Count;
                var mods = string.Join(",", Regex.Matches(argText, @"\b(ref|out|in)\b").Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal));
                var ret = m.Groups["ret"].Value;
                if (isInterface)
                {
                    if (!declared.TryGetValue(name, out var list))
                    {
                        list = new List<(int, int, string)>();
                        declared[name] = list;
                    }
                    list.Add((count, defaults, mods));
                }
                else
                {
                    if (!implemented.TryGetValue(name, out var list))
                    {
                        list = new List<(string, int, int, int, string, string)>();
                        implemented[name] = list;
                    }
                    list.Add((relative, lineIndex + 1, count, defaults, mods, ret));
                }
            }
        }
        if (files == 0)
            return "接口签名漂移报告: 未找到 .cs 文件";
        foreach (var (name, impls) in implemented)
        {
            if (!declared.TryGetValue(name, out var decls))
                continue;
            foreach (var d in decls)
            {
                foreach (var im in impls)
                {
                    if (d.defaults > im.defaults)
                        lostDefaults.Add($"{im.Item1}:{im.Item2} {name} 的实现丢了 {d.defaults - im.defaults} 个可选参数的默认值（调用方用具名实参会编译不过）");
                    if (d.mods.Length > 0 && im.mods.Length == 0)
                        lostModifiers.Add($"{im.Item1}:{im.Item2} {name} 的实现去掉了接口上的 {d.mods}（签名不再匹配）");
                    if (!string.Equals(d.defaults.ToString(), im.defaults.ToString()) && d.defaults == im.defaults && d.count == im.count)
                        covariantReturn.Add($"{im.Item1}:{im.Item2} {name} 返回类型 {im.ret} 与接口不一致");
                }
            }
        }
        var output = new StringBuilder();
        output.AppendLine($"接口签名漂移报告: {files} 个 .cs 文件（接口方法 {declared.Count} 个）");
        Report(output, "实现丢了默认值", lostDefaults, maxResults);
        Report(output, "实现去掉了 ref/out/in", lostModifiers, maxResults);
        Report(output, "返回类型不一致", covariantReturn, maxResults);
        if (lostDefaults.Count == 0 && lostModifiers.Count == 0 && covariantReturn.Count == 0)
            output.AppendLine("未发现接口签名漂移");
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
