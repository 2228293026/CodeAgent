using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查布尔参数与默认值陷阱：可选参数放在必选参数之后、
/// bool 参数用位置传参导致"传了 true 却没生效"、以及默认值与调用方约定不一致。</summary>
public sealed class BooleanParameterReportTool : ITool
{
    public string Name => "boolean_parameter_report";
    public string Description =>
        "只读检查布尔参数陷阱：可选参数排在必选参数之前、bool 参数被位置传参（看不出是 true/false）、同一参数有多个默认值。";

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
        var optionalBeforeRequired = new List<string>();
        var positionalBool = new List<string>();
        var conflictingDefaults = new List<string>();
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
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                var m = Regex.Match(trimmed, @"\b(?<name>\w+)\s*\((?<params>[^)]*)\)");
                if (!m.Success || m.Groups["params"].Value.Length == 0)
                    continue;
                // 必须真的是**方法签名**而不是一次函数调用：方法体要么同行（=> 表达式体），
                // 要么在下一行开括号。早先只在同一行找 `{`，于是跨行写的方法体
                // 整条规则静默空跑——检查看起来在跑，实际什么都没看。
                var next = i + 1 < lines.Length ? lines[i + 1].Trim() : string.Empty;
                if (!trimmed.Contains("=>", StringComparison.Ordinal) && !next.StartsWith('{'))
                    continue;
                var raw = m.Groups["params"].Value;
                // 按逗号**切分**参数，而不是让正则去扫。
                // 早先把 `,` 放进了类型字符类，于是 `int a, int b = 3` 被切成
                // `int a` 和 `, int`——第二个匹配的「参数名」变成了 int，
                // 带默认值的那一项根本匹配不到，整条规则静默空跑。
                var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var seenRequired = false;
                foreach (var part in parts)
                {
                    if (Regex.IsMatch(part, @"^(ref|out|in|params)\s"))
                        continue;
                    var eq = part.IndexOf('=');
                    var hasDefault = eq >= 0;
                    var name = Regex.Match(part.Split('=')[0], @"(?<n>\w+)\s*$").Groups["n"].Value;
                    if (name.Length == 0)
                        continue;
                    if (hasDefault && seenRequired)
                        optionalBeforeRequired.Add($"{relative}:{lineNo} {m.Groups["name"].Value}(...) 里带默认值的 {name} 排在必选参数之后（调用方没法跳过前面的必填项）");
                    if (!hasDefault) seenRequired = true;
                }
                // bool / bool? 参数：位置传参时看不出是 true 还是 false
                foreach (Match p in Regex.Matches(raw, @"\bbool\??\s+(?<n>\w+)"))
                {
                    var name = p.Groups["n"].Value;
                    if (!name.StartsWith("is", StringComparison.Ordinal)
                        && !name.StartsWith("has", StringComparison.Ordinal)
                        && !name.StartsWith("can", StringComparison.Ordinal)
                        && !name.StartsWith("allow", StringComparison.Ordinal)
                        && !name.StartsWith("enable", StringComparison.Ordinal)
                        && !name.StartsWith("with", StringComparison.Ordinal)
                        && !name.StartsWith("use", StringComparison.Ordinal)
                        && !name.StartsWith("force", StringComparison.Ordinal)
                        && !name.StartsWith("skip", StringComparison.Ordinal))
                        positionalBool.Add($"{relative}:{lineNo} 布尔参数 {name} 没有 is/has/enable 前缀：位置传参时调用方看到的是 (…, true)，看不出开关是什么");
                }
                // 同一参数名在同一签名里出现两次
                var names = parts
                    .Select(part => Regex.Match(part.Split('=')[0], @"(?<n>\w+)\s*$").Groups["n"].Value)
                    .Where(n => n.Length > 0)
                    .ToList();
                foreach (var dup in names.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key))
                    conflictingDefaults.Add($"{relative}:{lineNo} {m.Groups["name"].Value}(...) 里参数 {dup} 出现了多次");
            }
        }
        if (files == 0)
            return "布尔参数报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"布尔参数报告: {files} 个 .cs 文件");
        Report(output, "可选参数排在必选参数之后", optionalBeforeRequired, maxResults);
        Report(output, "语义不明的布尔参数", positionalBool, maxResults);
        Report(output, "签名里参数重名", conflictingDefaults, maxResults);
        if (optionalBeforeRequired.Count == 0 && positionalBool.Count == 0 && conflictingDefaults.Count == 0)
            output.AppendLine("未发现布尔参数陷阱");
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
