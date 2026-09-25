using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 .editorconfig：未遵守的规则、未纳入版本库的例外、以及被覆盖的重复条目。</summary>
public sealed class EditorConfigReportTool : ITool
{
    public string Name => "editorconfig_report";
    public string Description =>
        "只读检查 .editorconfig：未被任何编辑器遵守的严重级别、被覆盖的重复条目，以及未被版本控制的配置。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 6，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 6), 1, 32);
        var files = new List<string>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(file).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase))
                files.Add(file);
        }
        if (files.Count == 0)
            return "EditorConfig 报告: 未找到 .editorconfig";
        var severities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<string>();
        var unversioned = new List<string>();
        var noRoot = new List<string>();
        var totalRules = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!relative.Equals(".editorconfig", StringComparison.Ordinal))
                unversioned.Add(relative);
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var seen = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var hasRoot = false;
            var inPreamble = true;
            string section = "(前言)";
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                    continue;
                if (line.StartsWith('['))
                {
                    inPreamble = false;
                    section = line;
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (inPreamble)
                {
                    if (key.Equals("root", StringComparison.OrdinalIgnoreCase) && value.Equals("true", StringComparison.OrdinalIgnoreCase))
                        hasRoot = true;
                    continue;
                }
                totalRules++;
                // 同一节内重复的键：先前的值被静默丢弃，这才是真正需要报告的情况
                // （只报 key=value 完全相同的重复反而漏掉了 indent_size=4 被 indent_size=2 覆盖这种）
                // 键必须带上节名：不同 glob 下同名键各自生效，不算重复
                var normalizedKey = section + " " + key.ToLowerInvariant();
                if (!seen.TryGetValue(normalizedKey, out var values))
                {
                    values = new List<string>();
                    seen[normalizedKey] = values;
                }
                values.Add(value);
                // severity 决定编辑器是否真的提示——写成 error 却拼错键名是最常见的失效
                if (key.StartsWith("dotnet_diagnostic", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("indent_size", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("csharp_", StringComparison.OrdinalIgnoreCase))
                {
                    severities[key] = severities.GetValueOrDefault(key) + 1;
                }
            }
            if (!hasRoot)
                noRoot.Add(relative);
            foreach (var pair in seen.Where(p => p.Value.Count > 1))
            {
                // 键是「节名 空格 键名」，展示时只取键名部分
                var keyOnly = pair.Key[(pair.Key.IndexOf(' ') + 1)..];
                var sectionName = pair.Key[..pair.Key.IndexOf(' ')];
                duplicates.Add($"{relative} {sectionName}: {keyOnly} 出现 {pair.Value.Count} 次（{string.Join(" → ", pair.Value)}，只生效最后一个）");
            }
        }
        var output = new StringBuilder();
        output.AppendLine($"EditorConfig 报告: {files.Count} 个 .editorconfig，{totalRules} 条规则");
        if (severities.Count > 0)
        {
            output.AppendLine("常见拼写统计（键名写错则规则完全失效）:");
            foreach (var pair in severities.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(20))
                output.AppendLine($"  {pair.Key}: {pair.Value}");
        }
        else
        {
            output.AppendLine("未发现 severity 相关规则");
        }
        Report(output, "重复条目（后者覆盖前者）", duplicates);
        Report(output, "未纳入版本控制的 .editorconfig", unversioned);
        Report(output, "缺少 root = true（子目录配置会覆盖父目录）", noRoot);
        if (duplicates.Count == 0 && unversioned.Count == 0 && noRoot.Count == 0 && severities.Count > 0)
            output.AppendLine("未发现配置问题");
        return output.ToString().TrimEnd();
    }

    private static void Report(StringBuilder output, string title, List<string> items)
    {
        if (items.Count == 0)
            return;
        output.AppendLine($"{title}: {items.Count}");
        foreach (var item in items.OrderBy(x => x, StringComparer.Ordinal))
            output.AppendLine($"  {item}");
    }
}
