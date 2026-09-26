using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 struct 的语义陷阱：readonly struct 里有 setter（readonly 名不副实）、
/// 公共字段的 struct 放进 HashSet/Dictionary 后再改（哈希变了就找不回来）、
/// 以及在非 readonly 成员里写 this.X = 触发防御性拷贝。</summary>
public sealed class ReadonlyStructReportTool : ITool
{
    public string Name => "readonly_struct_report";
    public string Description =>
        "只读检查 struct 语义陷阱：readonly struct 里声明 setter（readonly 名不副实）、可变 struct 当集合键后又被改写、this.X = 触发防御性拷贝。";

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
        var setterInReadonly = new List<string>();
        var mutableStructKey = new List<string>();
        var defensiveCopy = new List<string>();
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
            // 声明成 struct 的类型
            var structTypes = new Dictionary<string, (bool Readonly, int Line)>(StringComparer.Ordinal);
            foreach (Match s in Regex.Matches(text, @"\b(?:(?<ro>readonly)\s+)?struct\s+(?<n>\w+)"))
                structTypes[s.Groups["n"].Value] = (s.Groups["ro"].Success, text[..s.Index].Count(c => c == '\n') + 1);
            if (structTypes.Count == 0)
                continue;
            foreach (var (name, info) in structTypes)
            {
                var body = ClassBody(lines, info.Line - 1);
                if (body is null)
                    continue;
                if (info.Readonly && Regex.IsMatch(body, @"\{[^}]*\bget\s*;\s*set\s*;"))
                    setterInReadonly.Add($"{relative}:{info.Line} readonly struct {name} 里声明了 setter（readonly 只约束字段和只读成员，这个 setter 照样能改状态；readonly 名不副实）");
                // 可变 struct（不是 readonly struct）被当成集合键，之后又改写了这个实例。
                // 必须顺着"加入集合的那个变量"去找——早先只找 `类型名.字段 =`，
                // 而真实代码写的是 `p.X = 5`，于是这条规则对所有代码静默失效。
                if (!info.Readonly)
                {
                    foreach (Match u in Regex.Matches(text, @"\b(?:HashSet|Dictionary)<\s*" + Regex.Escape(name) + @"\s*[,>]"))
                    {
                        var added = new HashSet<string>(StringComparer.Ordinal);
                        foreach (Match a in Regex.Matches(text, @"\.(?:Add|TryAdd)\s*\(\s*(?<v>\w+)\s*\)"))
                            added.Add(a.Groups["v"].Value);
                        foreach (Match a in Regex.Matches(text, @"\[[^\]\n]*\]\s*=\s*(?<v>\w+)\s*;"))
                            added.Add(a.Groups["v"].Value);
                        foreach (var v in added)
                        {
                            var mut = Regex.Match(text, $@"\b{Regex.Escape(v)}\s*\.\s*(?<f>\w+)\s*=");
                            if (mut.Success)
                            {
                                mutableStructKey.Add($"{relative}:{info.Line} 可变 struct {name} 被用作集合键，之后又写了 {v}.{mut.Groups["f"].Value} = （哈希一变，set.Contains 会突然返回 false）");
                                break;
                            }
                        }
                        break;
                    }
                }
            }
            // 非 readonly 成员里写 this.X = —— 触发防御性拷贝
            foreach (Member m in Members(lines))
            {
                if (m.IsReadonly)
                    continue;
                var hit = Regex.Match(m.Body, @"this\.(?<f>\w+)\s*=");
                if (hit.Success && structTypes.ContainsKey(m.OwnerType) && structTypes[m.OwnerType].Readonly)
                    defensiveCopy.Add($"{relative}:{m.Line} 在非 readonly 成员 {m.Name}() 里写 this.{hit.Groups["f"].Value} = （readonly struct 走的是防御性拷贝，改的是副本，调用方看不到变化）");
            }
        }
        if (files == 0)
            return "readonly struct 报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"readonly struct 报告: {files} 个 .cs 文件");
        Report(output, "readonly struct 里有 setter", setterInReadonly, maxResults);
        Report(output, "可变 struct 当集合键", mutableStructKey, maxResults);
        Report(output, "防御性拷贝", defensiveCopy, maxResults);
        if (setterInReadonly.Count == 0 && mutableStructKey.Count == 0 && defensiveCopy.Count == 0)
            output.AppendLine("未发现 readonly struct 问题");
        return output.ToString().TrimEnd();
    }

    private readonly record struct Member(string Name, string OwnerType, string Body, int Line, bool IsReadonly);

    /// <summary>列出文件里的成员（方法/属性），带上它属于哪个类型。</summary>
    private static List<Member> Members(string[] lines)
    {
        var result = new List<Member>();
        var owner = string.Empty;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal))
                continue;
            var decl = Regex.Match(t, @"\b(?:struct|class|record)\s+(?<n>\w+)");
            if (decl.Success)
                owner = decl.Groups["n"].Value;
            var sig = Regex.Match(t, @"^(?<mods>(?:(?:public|private|internal|protected|static|virtual|override|async|sealed|new|extern|unsafe|partial)\s+)*)[\w<>\[\],\.]+\s+(?<n>\w+)\s*\([^)]*\)");
            if (!sig.Success)
                continue;
            var body = new List<string>();
            var depth = 0;
            var started = false;
            for (var j = i; j < lines.Length && j < i + 60; j++)
            {
                foreach (var ch in lines[j])
                {
                    if (ch == '{') { depth++; started = true; }
                    else if (ch == '}') depth--;
                }
                body.Add(lines[j]);
                if (started && depth <= 0) break;
            }
            if (started)
                result.Add(new Member(sig.Groups["n"].Value, owner, string.Join("\n", body), i + 1, Regex.IsMatch(sig.Groups["mods"].Value, @"\breadonly\b")));
        }
        return result;
    }

    private static string? ClassBody(string[] lines, int declIndex)
    {
        var body = new List<string>();
        var depth = 0;
        var started = false;
        for (var j = declIndex; j < lines.Length && j < declIndex + 400; j++)
        {
            foreach (var ch in lines[j])
            {
                if (ch == '{') { depth++; started = true; }
                else if (ch == '}') depth--;
            }
            body.Add(lines[j]);
            if (started && depth <= 0)
                return string.Join("\n", body);
        }
        return started ? string.Join("\n", body) : null;
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
