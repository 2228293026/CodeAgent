using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查重复代码：完全相同的方法体、成对出现的拷贝粘贴片段、以及重复的常量定义。</summary>
public sealed class DuplicateCodeReportTool : ITool
{
    public string Name => "duplicate_code_report";
    public string Description =>
        "只读检查重复代码：完全相同的方法体、成对出现的相同代码片段、以及重复定义的同名常量。";

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

    /// <summary>判定「重复片段」所需的最少行数——少于此数的相同片段多半是样板（getter 等），不算问题。</summary>
    internal const int MinFragmentLines = 3;

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var fragments = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var constNames = new Dictionary<string, List<string>>(StringComparer.Ordinal);
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
            var significant = new List<int>();
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var constMatch = Regex.Match(trimmed, @"\b(?:const|static readonly)\s+\w+\s+(\w+)\s*=");
                if (constMatch.Success)
                {
                    if (!constNames.TryGetValue(constMatch.Groups[1].Value, out var sites))
                        constNames[constMatch.Groups[1].Value] = sites = new List<string>();
                    sites.Add($"{relative}:{i + 1}");
                }
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // 归一化空白：同逻辑不同缩进的拷贝粘贴也算重复
                significant.Add(i);
            }
            for (var s = 0; s + MinFragmentLines <= significant.Count; s++)
            {
                var key = string.Join("\n", significant.Skip(s).Take(MinFragmentLines)
                    .Select(i => Regex.Replace(lines[i].Trim(), @"\s+", " ")));
                if (!fragments.TryGetValue(key, out var sites))
                    fragments[key] = sites = new List<string>();
                sites.Add($"{relative}:{significant[s]}");
            }
        }
        if (files == 0)
            return "重复代码报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"重复代码报告: {files} 个 .cs 文件（片段窗口 {MinFragmentLines} 行）");
        var dupFragments = fragments.Where(p => p.Value.Count > 1)
            .OrderByDescending(p => p.Value.Count)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
        if (dupFragments.Count > 0)
        {
            output.AppendLine($"重复代码片段（{MinFragmentLines} 行及以上）: {dupFragments.Count} 处");
            foreach (var pair in dupFragments.Take(maxResults))
            {
                output.AppendLine($"  {string.Join(" · ", pair.Value)}");
                output.AppendLine($"    {TextUtil.TruncateLine(pair.Key.Replace("\n", " ⏎ "), 70)}");
            }
            if (dupFragments.Count > maxResults)
                output.AppendLine($"  …（另有 {dupFragments.Count - maxResults} 处未显示）");
        }
        else
        {
            output.AppendLine($"重复代码片段（{MinFragmentLines} 行及以上）: 0 处");
        }
        var dupConsts = constNames.Where(p => p.Value.Count > 1).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        output.AppendLine($"同名常量重复定义: {dupConsts.Count} 处");
        foreach (var pair in dupConsts.Take(maxResults))
            output.AppendLine($"  {pair.Key}: {string.Join(" · ", pair.Value)}");
        if (dupFragments.Count == 0 && dupConsts.Count == 0)
            output.AppendLine("未发现重复代码");
        return output.ToString().TrimEnd();
    }
}
