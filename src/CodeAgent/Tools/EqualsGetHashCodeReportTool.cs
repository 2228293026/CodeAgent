using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查值语义的陷阱：重写了 Equals 却没重写 GetHashCode、
/// 只写了 GetHashCode 却不写 Equals（违反哈希契约）、以及在可变类型里缓存哈希。</summary>
public sealed class EqualsGetHashCodeReportTool : ITool
{
    public string Name => "equals_gethashcode_report";
    public string Description =>
        "只读检查 Equals/GetHashCode 契约：只重写 Equals 漏了 GetHashCode、只写 GetHashCode、Equals 与 GetHashCode 依据的字段不一致。";

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
        var missingHash = new List<string>();
        var orphanHash = new List<string>();
        var mismatchedFields = new List<string>();
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
            // 按类型分块：一个类型里出现了哪些运算符、依据哪些字段
            var typeName = "";
            var equals = new List<(int Line, string Body)>();
            var hash = new List<(int Line, string Body)>();
            void Flush()
            {
                if (equals.Count == 0 && hash.Count == 0) { equals.Clear(); hash.Clear(); return; }
                if (equals.Count > 0 && hash.Count == 0)
                    foreach (var e in equals)
                        missingHash.Add($"{relative}:{e.Line} 类型 {typeName} 重写了 Equals 却没有重写 GetHashCode（放进 HashSet/Dictionary 会找不到自己）");
                if (equals.Count == 0 && hash.Count > 0)
                    foreach (var h in hash)
                        orphanHash.Add($"{relative}:{h.Line} 类型 {typeName} 重写了 GetHashCode 却没有重写 Equals（相等性与哈希依据不一致）");
                if (equals.Count > 0 && hash.Count > 0)
                {
                    var ef = Identifiers(equals[0].Body);
                    var hf = Identifiers(hash[0].Body);
                    foreach (var f in ef)
                        if (!hf.Contains(f))
                            mismatchedFields.Add($"{relative}:{equals[0].Line} Equals 依据了 {f}，GetHashCode 没有（相等的两个对象可能哈希不同）");
                    foreach (var f in hf)
                        if (!ef.Contains(f))
                            mismatchedFields.Add($"{relative}:{hash[0].Line} GetHashCode 依据了 {f}，Equals 没有（哈希相同的两个对象可能不相等）");
                }
                equals.Clear();
                hash.Clear();
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                var typeDecl = Regex.Match(trimmed, @"^\s*(?:public|internal|private|protected|abstract|sealed|static|partial|\s)*\b(?:class|struct|record)\s+(?<n>\w+)");
                if (typeDecl.Success)
                {
                    Flush();
                    typeName = typeDecl.Groups["n"].Value;
                    continue;
                }
                var body = Body(lines, i, trimmed);
                if (body.Count == 0)
                    continue;
                var text = string.Join("\n", body);
                if (Regex.IsMatch(trimmed, @"\bpublic\s+override\s+bool\s+Equals\s*\(") || Regex.IsMatch(trimmed, @"\bbool\s+IEquatable"))
                {
                    if (Regex.IsMatch(trimmed, @"\bEquals\s*\("))
                        equals.Add((i + 1, text));
                }
                if (Regex.IsMatch(trimmed, @"\bpublic\s+override\s+int\s+GetHashCode\s*\("))
                    hash.Add((i + 1, text));
            }
            Flush();
        }
        if (files == 0)
            return "Equals/GetHashCode 报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"Equals/GetHashCode 报告: {files} 个 .cs 文件");
        Report(output, "重写 Equals 但漏了 GetHashCode", missingHash, maxResults);
        Report(output, "重写 GetHashCode 但漏了 Equals", orphanHash, maxResults);
        Report(output, "两个方法依据的字段不一致", mismatchedFields, maxResults);
        if (missingHash.Count == 0 && orphanHash.Count == 0 && mismatchedFields.Count == 0)
            output.AppendLine("未发现 Equals/GetHashCode 契约问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>方法体里出现过的**字段**（粗判据）。
    ///
    /// 关键：只取**后面不跟左括号**的标识符——跟括号的是方法调用
    /// （<c>Name.GetHashCode()</c> 里的 <c>GetHashCode</c>），不是比较依据。
    /// 不做这个区分时，<c>return Name.GetHashCode();</c> 会让 GetHashCode 看起来
    /// 依据了一个 Equals 没用的字段，把**完全正确**的一对误报成不一致。</summary>
    private static HashSet<string> Identifiers(string body)
    {
        var cleaned = Regex.Replace(body, @"(?s)//.*|/\*.*?\*/|""[^""]*""", "");
        var skip = new HashSet<string>(StringComparer.Ordinal)
        {
            "this", "base", "return", "int", "bool", "true", "false", "null", "new", "var",
            "public", "override", "virtual", "string", "object", "Math", "HashCode", "Combine",
        };
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(cleaned, @"\b[A-Za-z_]\w*\b"))
        {
            var after = m.Index + m.Length < cleaned.Length ? cleaned[m.Index + m.Length] : ' ';
            if (after == '(') continue;                       // 方法调用，不是字段
            if (m.Value.Length <= 2 || skip.Contains(m.Value)) continue;
            result.Add(m.Value);
        }
        return result;
    }

    private static List<string> Body(string[] lines, int declLine, string decl)
    {
        if (decl.Contains("=>", StringComparison.Ordinal))
            return new List<string> { decl };
        var depth = 0;
        var started = false;
        for (var i = declLine; i < lines.Length && i < declLine + 200; i++)
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
