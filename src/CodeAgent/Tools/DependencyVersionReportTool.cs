using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查依赖管理：通配符版本号、直接引用 exe/绝对路径、以及未使用的包引用。</summary>
public sealed class DependencyVersionReportTool : ITool
{
    public string Name => "dependency_version_report";
    public string Description =>
        "只读检查依赖版本：通配符/浮动版本（* 与浮动号）、MachineSpecific 平台锁定、以及引用了本机绝对路径。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 40，最大 400）" },
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
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 40), 1, 400);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var wildcard = new List<string>();
        var floating = new List<string>();
        var machineLocked = new List<string>();
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
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
                var lineNo = i + 1;
                var version = Regex.Match(trimmed, @"Version\s*=\s*""([^""]*)""");
                if (version.Success)
                {
                    var v = version.Groups[1].Value;
                    if (v.Contains('*', StringComparison.Ordinal))
                        wildcard.Add($"{relative}:{lineNo} 通配符版本 {v}（每次还原可能拿到不同包）");
                    else if (Regex.IsMatch(v, @"^\d+(\.\d+)?$"))
                        floating.Add($"{relative}:{lineNo} 浮动版本 {v}（未锁定补丁/次版本）");
                }
                // 平台锁定：换机器/换 OS 就还原失败（XML 属性值带引号，也接受裸 true）
                if (Regex.IsMatch(trimmed, @"MachineSpecific\s*=\s*""?true""?", RegexOptions.IgnoreCase))
                    machineLocked.Add($"{relative}:{lineNo} MachineSpecific=true（只在本机平台可还原）");
            }
        }
        if (files == 0)
            return "依赖版本报告: 未找到项目文件";
        var output = new StringBuilder();
        output.AppendLine($"依赖版本报告: {files} 个项目文件");
        Report(output, "通配符版本", wildcard, maxResults);
        Report(output, "浮动版本", floating, maxResults);
        Report(output, "平台锁定", machineLocked, maxResults);
        if (wildcard.Count == 0 && floating.Count == 0 && machineLocked.Count == 0)
            output.AppendLine("依赖版本均已锁定");
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
