using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查接口契约漂移：接口方法没有实现类对应实现、
/// 实现方法漏了 override、以及接口新增成员后实现类没跟上（签名对不上）。</summary>
public sealed class InterfaceContractReportTool : ITool
{
    public string Name => "interface_contract_report";
    public string Description =>
        "只读检查接口契约漂移：接口方法在实现类里找不到实现、实现方法漏了 override/关键字、接口成员与实现签名对不上。";

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
        var missingImpl = new List<string>();
        var signatureMismatch = new List<string>();
        var unoverridden = new List<string>();
        var files = 0;
        // 接口名 → 成员（名称 + 参数类型串）
        var interfaceMembers = new Dictionary<string, List<(string Name, string Params, int Line, string File)>>(StringComparer.Ordinal);
        // 类名 → 它声明实现的接口 + 自己的成员签名
        var classes = new Dictionary<string, (List<string> Implements, List<(string Name, string Params, int Line, string File)> Members, string File)>(StringComparer.Ordinal);
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
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var decl = Regex.Match(trimmed, @"^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected)?\s*(?<kind>interface|class|record|struct)\s+(?<n>\w+)\s*(?::\s*(?<impl>[^{]+))?");
                if (!decl.Success)
                    continue;
                var kind = decl.Groups["kind"].Value;
                var name = decl.Groups["n"].Value;
                var impl = decl.Groups["impl"].Success ? decl.Groups["impl"].Value : "";
                var members = new List<(string, string, int, string)>();
                var depth = 0;
                var started = false;
                for (var j = i + 1; j < lines.Length && j < i + 300; j++)
                {
                    var t = lines[j].Trim();
                    if (t.StartsWith("//", StringComparison.Ordinal) || t.Length == 0)
                        continue;
                    if (kind == "class" || kind == "struct" || kind == "record")
                    {
                        var m = Regex.Match(t, @"(?:(?:public|internal|protected|private|static|virtual|override|sealed|async|abstract|extern|unsafe|partial)\s+)*[\w\.<>\[\],\?]+\s+(?<mn>\w+)\s*\((?<mp>[^)]*)\)");
                        if (m.Success)
                        {
                            members.Add((m.Groups["mn"].Value, Normalize(m.Groups["mp"].Value), j + 1, relative));
                            // 没有 override/接口实现迹象的**非抽象**公共方法：可能是漏了 override
                            if (!t.Contains("override", StringComparison.Ordinal)
                                && !t.Contains("abstract", StringComparison.Ordinal)
                                && !t.Contains("virtual", StringComparison.Ordinal)
                                && !t.Contains("static", StringComparison.Ordinal)
                                && (t.StartsWith("public", StringComparison.Ordinal) || t.StartsWith("internal", StringComparison.Ordinal)))
                                unoverridden.Add($"{relative}:{j + 1} {name}.{m.Groups["mn"].Value} 是具体方法却没有 override/virtual（实现接口时漏写 override 编译不过；继承时则静默变成新方法）");
                        }
                    }
                    else
                    {
                        var m = Regex.Match(t, @"[\w\.<>\[\],\?]+\s+(?<mn>\w+)\s*\((?<mp>[^)]*)\)");
                        if (m.Success)
                            members.Add((m.Groups["mn"].Value, Normalize(m.Groups["mp"].Value), j + 1, relative));
                    }
                    foreach (var ch in lines[j])
                    {
                        if (ch == '{') { depth++; started = true; }
                        else if (ch == '}') depth--;
                    }
                    if (started && depth <= 0) break;
                }
                if (kind == "interface")
                    interfaceMembers[name] = members;
                else
                {
                    var impls = Regex.Matches(impl, @"\b([A-Z]\w+)")
                        .Select(x => x.Groups[1].Value)
                        .Where(x => x != name)
                        .ToList();
                    classes[name] = (impls, members, relative);
                }
            }
        }
        foreach (var (cls, (impls, members, clsFile)) in classes)
        {
            foreach (var iface in impls)
            {
                if (!interfaceMembers.TryGetValue(iface, out var want))
                    continue;
                var have = members.Select(m => (m.Name, m.Params)).ToHashSet();
                foreach (var (mn, mp, line, file) in want)
                {
                    if (!have.Contains((mn, mp)))
                        missingImpl.Add($"{file}:{line} 接口 {iface}.{mn}({mp}) 在实现类 {cls} 里找不到对应实现（编译不过；或参数类型悄悄对不上）");
                }
            }
        }
        if (files == 0)
            return "接口契约报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"接口契约报告: {files} 个 .cs 文件，{interfaceMembers.Count} 个接口");
        Report(output, "接口成员未实现", missingImpl, maxResults);
        Report(output, "可能漏写 override", unoverridden, maxResults);
        if (missingImpl.Count == 0 && unoverridden.Count == 0)
            output.AppendLine("未发现接口契约漂移");
        return output.ToString().TrimEnd();
    }

    /// <summary>参数列表归一化：只比**类型**，忽略参数名、默认值与 ref/out/in 修饰。
    /// 改了参数名不该算契约漂移，改了参数类型才算。
    /// 注意要取**第一个**词（类型）而不是最后一个（参数名）——取错会让
    /// `Save(string path)` 与 `Save(string file)` 被判成不同签名，满屏误报。</summary>
    internal static string Normalize(string parameters) => string.Join(",", parameters
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p =>
        {
            var type = p.Split('=')[0].Trim();
            foreach (var mod in new[] { "ref ", "out ", "in ", "params ", "this " })
                type = type.StartsWith(mod, StringComparison.Ordinal) ? type[mod.Length..] : type;
            // "string path" → "string"；"List<int> xs" → "List<int>"
            return type.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        })
        .Where(t => t.Length > 0));

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
