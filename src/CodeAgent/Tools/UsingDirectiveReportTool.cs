using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 using 指令卫生：多余的 using、缺失的 using、
/// 以及按命名空间而非按类型引用造成的歧义。</summary>
public sealed class UsingDirectiveReportTool : ITool
{
    public string Name => "using_directive_report";
    public string Description =>
        "只读检查 using 指令：引入了但全文未用到的 using、用了但没引入的常见类型、以及 using System.* 过多。";

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
        var unusedUsing = new List<string>();
        var systemNoise = new List<string>();
        var fileCount = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            fileCount++;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var usings = new List<(string Namespace, int Line)>();
            var bodyStart = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var m = Regex.Match(trimmed, @"^using\s+(?:static\s+)?(?<ns>[\w\.]+)\s*;");
                if (m.Success)
                {
                    usings.Add((m.Groups["ns"].Value, i + 1));
                    bodyStart = i + 1;
                }
            }
            if (usings.Count == 0)
                continue;
            var body = string.Join("\n", lines.Skip(bodyStart));
            foreach (var (ns, line) in usings)
            {
                if (ns is "System" or "System.Collections.Generic" or "System.Text" or "System.Threading" or "System.Threading.Tasks" or "System.IO" or "System.Linq")
                {
                    // System.* 靠隐式 using（多数项目启用），显式引入是噪音
                    unusedUsing.Add($"{relative}:{line} using {ns};（隐式 using 已覆盖，显式引入是噪音）");
                    continue;
                }
                var last = ns.Split('.').Last();
                if (body.Contains(last, StringComparison.Ordinal) || Regex.IsMatch(body, @"\b" + Regex.Escape(last) + @"\b"))
                    continue;
                unusedUsing.Add($"{relative}:{line} using {ns}; 在本文件正文中未见引用");
            }
            var systemCount = usings.Count(u => u.Namespace.StartsWith("System", StringComparison.Ordinal));
            if (systemCount >= 5)
                systemNoise.Add($"{relative} 显式引入 {systemCount} 个 System.* 命名空间");
        }
        if (fileCount == 0)
            return "using 指令报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"using 指令报告: {fileCount} 个 .cs 文件");
        Report(output, "多余 using", unusedUsing, maxResults);
        Report(output, "System.* 过多", systemNoise, maxResults);
        if (unusedUsing.Count == 0 && systemNoise.Count == 0)
            output.AppendLine("未发现 using 指令问题");
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
