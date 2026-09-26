using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查值类型/集合的相等性契约：自定义 Equals 但未重写 GetHashCode、
/// 在 HashSet/字典键上重写了 Equals、以及用 == 比较引用类型（容易与 Equals 语义不一致）。</summary>
public sealed class EqualityContractReportTool : ITool
{
    public string Name => "equality_contract_report";
    public string Description =>
        "只读检查相等性契约：重写 Equals 未重写 GetHashCode、被重写 Equals 的类型用 == 比较、值类型用引用相等。";

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
        var missingHashCode = new List<string>();
        var operatorVsEquals = new List<string>();
        var structUsingEquals = new List<string>();
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
            // 本文件里定义了重写 Equals 的类型
            var eqTypes = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                var eq = Regex.Match(trimmed, @"public\s+(?:override\s+)?(?:virtual\s+)?bool\s+Equals\s*\(\s*(?:object\??\s+)?(\w+)?");
                if (eq.Success)
                {
                    var owner = eq.Groups[1].Value;
                    if (owner.Length == 0)
                    {
                        // object.Equals() 重载：在同一类型里找 GetHashCode
                        var hasHash = lines.Skip(i).Take(40).Any(l => l.Contains("GetHashCode", StringComparison.Ordinal));
                        if (!hasHash)
                            missingHashCode.Add($"{relative}:{lineNo} 重写 Equals 但附近未见 GetHashCode（哈希集合里会失效）");
                    }
                    else
                        eqTypes.Add(owner);
                }
            }
            foreach (var type in eqTypes)
            {
                if (!lines.Any(l => Regex.IsMatch(l, @"int\s+GetHashCode\s*\(") && l.Contains(type, StringComparison.Ordinal)))
                    missingHashCode.Add($"{relative}:{type} 重写 Equals 但未见 GetHashCode（HashSet/字典键会失效）");
            }
            // 自定义 Equals 的类型用 == 比较：与 Equals 语义可能不一致
            foreach (Match m in Regex.Matches(string.Join("\n", lines), @"\b(\w+)\s*==\s*\1\b"))
            {
                var name = m.Groups[1].Value;
                if (eqTypes.Contains(name) || char.IsUpper(name.Length > 0 ? name[0] : 'a'))
                    operatorVsEquals.Add($"{relative} 对自定义类型 {name} 使用 == 比较（与 Equals 语义可能不一致）");
            }
            // struct 未重写 Equals 却用 Equals
            if (Regex.IsMatch(string.Join("\n", lines), @"\bstruct\s+(\w+)"))
            {
                foreach (Match m in Regex.Matches(string.Join("\n", lines), @"\bstruct\s+(\w+)"))
                {
                    var name = m.Groups[1].Value;
                    var body = string.Join("\n", lines);
                    if (!body.Contains($"struct {name}", StringComparison.Ordinal)) continue;
                    if (body.Contains($"{name}.Equals", StringComparison.Ordinal)
                        && !Regex.IsMatch(body, @"bool\s+Equals\s*\("))
                        structUsingEquals.Add($"{relative} struct {name} 用 Equals 比较但未见重写（依赖逐字段比较，值相同但类型不同就不相等）");
                }
            }
        }
        if (files == 0)
            return "相等性契约报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"相等性契约报告: {files} 个 .cs 文件");
        Report(output, "Equals 缺 GetHashCode", missingHashCode, maxResults);
        Report(output, "== 与 Equals 混用", operatorVsEquals, maxResults);
        Report(output, "struct 用 Equals", structUsingEquals, maxResults);
        if (missingHashCode.Count == 0 && operatorVsEquals.Count == 0 && structUsingEquals.Count == 0)
            output.AppendLine("未发现相等性契约问题");
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
