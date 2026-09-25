using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读解析 CHANGELOG 的版本分节，统计各类条目数量并抽取发布日期。</summary>
public sealed class ChangelogReportTool : ITool
{
    /// <summary>版本标题：## 1.2.0 / ## [1.2.0] - 2024-05-01 / ### v1.2.0（2024-05-01）。
    /// Keep-a-Changelog 的方括号写法与 v 前缀都要认，否则该格式的仓库一个版本都解析不出来。</summary>
    private static readonly Regex Version = new(
        @"^#{2,3}\s*\[?v?(?<ver>\d[\w.+-]*)\]?\s*(?:[-–—(]\s*(?<date>[\d]{4}[-/]\d{1,2}[-/]\d{1,2}))?\)?\s*$",
        RegexOptions.Compiled);

    private static readonly (string Label, string[] Prefixes)[] Categories =
    {
        ("新增", new[] { "added", "新增", "feat", "feature", "features" }),
        ("修复", new[] { "fixed", "修复", "bugfix", "fix" }),
        ("变更", new[] { "changed", "变更", "change", "changes" }),
        ("移除", new[] { "removed", "移除", "remove" }),
        ("弃用", new[] { "deprecated", "弃用" }),
        ("安全", new[] { "security", "安全" }),
    };

    public string Name => "changelog_report";
    public string Description =>
        "只读解析 CHANGELOG 的版本分节，统计每个版本的发布日期和新增/修复/变更/移除等条目数。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "文件或目录，默认工作区根目录" },
            ["max_releases"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示版本数（默认 50，最大 500）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var resolved = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        var maxReleases = Math.Clamp(ToolArgs.GetInt(args, "max_releases", 50), 1, 500);
        var files = new List<string>();
        if (File.Exists(resolved) && IsChangelog(Path.GetFileName(resolved)))
        {
            files.Add(resolved);
        }
        else if (Directory.Exists(resolved))
        {
            foreach (var file in SkipDirs.EnumerateFilesPruned(resolved, 3, includeIgnored: false, ct: ct, followSymlinks: false))
            {
                ct.ThrowIfCancellationRequested();
                if (IsChangelog(Path.GetFileName(file)))
                    files.Add(file);
            }
        }
        if (files.Count == 0)
            throw new ToolException("未找到 CHANGELOG 文件");
        var output = new StringBuilder();
        var totalReleases = 0;
        var totalEntries = 0;
        foreach (var file in files)
        {
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var releases = Parse(lines);
            var relative = Path.GetFileName(file);
            output.AppendLine($"{relative}: {releases.Count} 个版本");
            totalReleases += releases.Count;
            foreach (var release in releases.Take(maxReleases))
            {
                totalEntries += release.Counts.Values.Sum();
                var date = release.Date ?? "(无日期)";
                var parts = Categories
                    .Select(c => (c.Label, Count: release.Counts.GetValueOrDefault(c.Label)))
                    .Where(x => x.Count > 0)
                    .Select(x => $"{x.Label} {x.Count}");
                var summary = string.Join(" / ", parts);
                output.AppendLine($"  {release.Version} · {date} · {(summary.Length == 0 ? "（无条目）" : summary)}");
            }
            if (releases.Count > maxReleases)
                output.AppendLine($"  …（另有 {releases.Count - maxReleases} 个版本未显示）");
        }
        output.Insert(0, $"CHANGELOG 报告: {totalReleases} 个版本，{totalEntries} 条条目\n");
        return output.ToString().TrimEnd();
    }

    internal static List<(string Version, string? Date, Dictionary<string, int> Counts)> Parse(IReadOnlyList<string> lines)
    {
        var releases = new List<(string, string?, Dictionary<string, int>)>();
        (string Version, string? Date, Dictionary<string, int> Counts)? current = null;
        string? section = null; // 当前 ### 子标题给出的默认分类（Keep-a-Changelog 风格：标题分类 + 无前缀条目）
        var inFence = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
                continue;
            var match = Version.Match(line.TrimStart());
            if (match.Success)
            {
                if (current is { } prev)
                    releases.Add(prev);
                current = (match.Groups["ver"].Value, match.Groups["date"].Success ? match.Groups["date"].Value : null, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
                section = null;
                continue;
            }
            var trimmed = line.TrimStart();
            // 「### Added」这类子标题：整段的条目都归该分类，否则同一段的条目会全部漏统计
            if (trimmed.StartsWith('#'))
            {
                section = CategoryOf(trimmed.TrimStart('#').Trim())?.Label;
                continue;
            }
            if (current is not { } c)
                continue;
            var bullet = trimmed;
            if (!bullet.StartsWith('-') && !bullet.StartsWith('*') && !bullet.StartsWith('•'))
                continue;
            var label = CategoryOf(bullet)?.Label ?? section;
            if (label is not null)
                c.Counts[label] = c.Counts.GetValueOrDefault(label) + 1;
        }
        if (current is { } last)
            releases.Add(last);
        return releases;
    }

    /// <summary>从一行文本里找出它归属的分类（显式前缀优先，其次子标题名）。</summary>
    private static (string Label, string[] Prefixes)? CategoryOf(string text)
    {
        foreach (var category in Categories)
            if (category.Prefixes.Any(prefix => text.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
                return category;
        return null;
    }

    private static bool IsChangelog(string name) =>
        name.StartsWith("changelog", StringComparison.OrdinalIgnoreCase)
        && Path.GetExtension(name).Equals(".md", StringComparison.OrdinalIgnoreCase);
}
