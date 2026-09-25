using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 .gitignore 规则是否失效：追踪了本应忽略的文件、存在已失效的否定规则。</summary>
public sealed class GitignoreRuleReportTool : ITool
{
    public string Name => "gitignore_rule_report";
    public string Description =>
        "只读交叉检查 .gitignore 与已跟踪文件：找出被忽略模式命中却仍被追踪的文件，以及永不可能生效的否定规则。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 50，最大 500）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 20，最大 120）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var start = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(start))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 50), 1, 500);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 20), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var probe = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--git-dir"], timeout.Token);
        if (probe.ExitCode != 0)
            throw new ToolException("当前目录不是 Git 仓库");
        var files = await GitStatusTool.RunGitAsync(start, ["ls-files"], timeout.Token);
        if (files.ExitCode != 0)
            throw new ToolException($"无法读取已跟踪文件（退出码 {files.ExitCode}）");
        var tracked = files.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim().Replace('\\', '/'))
            .Where(f => f.Length > 0)
            .ToList();
        var patterns = await ReadPatternsAsync(start, ct);
        if (patterns.Count == 0)
            return "Gitignore 规则报告: 未找到 .gitignore 规则";
        // 命中忽略规则却仍被追踪：.gitignore 改动不会影响已追踪文件，规则形同虚设
        var ignoredButTracked = new List<(string File, string Rule)>();
        foreach (var file in tracked)
        {
            ct.ThrowIfCancellationRequested();
            var rule = LastMatchingRule(patterns, file);
            if (rule is not null)
                ignoredButTracked.Add((file, rule));
        }
        // 否定规则前面没有任何规则能匹配它：永不可能生效
        var deadNegations = new List<string>();
        foreach (var pattern in patterns)
        {
            if (!pattern.Negated)
                continue;
            var probePath = pattern.Pattern.TrimStart('!').TrimStart('/');
            if (probePath.Length == 0)
                continue;
            var before = patterns.Where(p => !p.Negated && p.Order < pattern.Order).ToList();
            var reachable = before.Any(p => Matches(p.Pattern, probePath));
            if (!reachable)
                deadNegations.Add($"!{pattern.Pattern}（前面没有任何规则能匹配它，永不生效）");
        }
        var output = new StringBuilder();
        output.AppendLine($"Gitignore 规则报告: {patterns.Count} 条规则，{tracked.Count} 个已跟踪文件");
        Report(output, "被忽略规则命中却仍被追踪", ignoredButTracked.Select(x => $"{x.File} ← {x.Rule}"), maxResults,
            "这些文件需要 git rm --cached 后 .gitignore 才会生效");
        Report(output, "永不可能生效的否定规则", deadNegations, maxResults, "前缀规则缺失或顺序有误");
        if (ignoredButTracked.Count == 0 && deadNegations.Count == 0)
            output.AppendLine("未发现失效规则");
        return output.ToString().TrimEnd();
    }

    private static void Report(StringBuilder output, string title, IEnumerable<string> items, int maxResults, string advice)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return;
        output.AppendLine($"{title}: {list.Count} —— {advice}");
        foreach (var item in list.OrderBy(x => x, StringComparer.Ordinal).Take(maxResults))
            output.AppendLine($"  {item}");
        if (list.Count > maxResults)
            output.AppendLine($"  …（另有 {list.Count - maxResults} 条未显示）");
    }

    private static async Task<List<(string Pattern, bool Negated, int Order)>> ReadPatternsAsync(string root, CancellationToken ct)
    {
        var result = new List<(string, bool, int)>();
        var file = Path.Combine(root, ".gitignore");
        if (!File.Exists(file))
            return result;
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(file, ct); }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return result; }
        catch (UnauthorizedAccessException) { return result; }
        var order = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var negated = line.StartsWith('!');
            if (negated)
                line = line[1..];
            result.Add((line, negated, order++));
        }
        return result;
    }

    /// <summary>返回最后一条命中的规则（gitignore 语义：后写的覆盖先写的），未命中返回 null。</summary>
    internal static string? LastMatchingRule(List<(string Pattern, bool Negated, int Order)> patterns, string file)
    {
        string? match = null;
        foreach (var (pattern, negated, _) in patterns)
        {
            if (Matches(pattern, file))
                match = negated ? null : pattern;
        }
        return match;
    }

    /// <summary>极简 gitignore 匹配：支持 **、目录锚定（/）、尾部 / 与任意层级前缀。</summary>
    internal static bool Matches(string pattern, string path)
    {
        var p = pattern.Trim();
        // 容忍调用方忘记剥掉否定前缀
        if (p.StartsWith('!'))
            p = p[1..];
        if (p.Length == 0)
            return false;
        var anchored = p.StartsWith('/');
        if (anchored)
            p = p[1..];
        var dirOnly = p.EndsWith('/');
        if (dirOnly)
            p = p[..^1];
        if (p.Length == 0)
            return false;
        // build/ 匹配 build 本身及其下所有内容（而不是要求路径里必须有 /）
        if (dirOnly && !path.Contains('/', StringComparison.Ordinal) && path != p)
            return false;
        var body = "^" + string.Join(
            ".*",
            p.Split("**", StringSplitOptions.None)
                .Select(part => Regex.Escape(part).Replace("\\*", "[^/]*", StringComparison.Ordinal))
        ) + (dirOnly ? "(/.*)?$" : "$");
        return Regex.IsMatch(path, body)
            || (!anchored && path.Contains('/') && Regex.IsMatch(path[(path.IndexOf('/', StringComparison.Ordinal) + 1)..], body));
    }
}
