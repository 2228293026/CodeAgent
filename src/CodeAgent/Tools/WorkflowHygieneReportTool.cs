using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 CI 工作流的触发条件与依赖冗余：不可用触发器、冗余 needs、被通配覆盖的过滤器。</summary>
public sealed class WorkflowHygieneReportTool : ITool
{
    public string Name => "workflow_hygiene_report";
    public string Description =>
        "只读审查 GitHub Actions：标出不可用的触发器（cron 无空行、分支永不对）、冗余 needs、只靠 schedule 的工作流与缺失的超时。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 50，最大 500）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 4，最大 12）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 50), 1, 500);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 4), 1, 12);
        var files = new List<string>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/.github/workflows/", StringComparison.Ordinal)
                && (normalized.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    || normalized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
                files.Add(file);
        }
        if (files.Count == 0)
            return "工作流卫生报告: 未发现 .github/workflows 下的工作流文件";
        var issues = new List<(string Kind, string File, string Detail)>();
        var scheduleOnly = new List<string>();
        var totalJobs = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetRelativePath(root, file).Replace('\\', '/');
            string text;
            try { text = await File.ReadAllTextAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            // cron 表达式前必须有空行，否则 YAML 会把它解析进上一个键——schedule 静默失效
            foreach (Match m in Regex.Matches(text, @"(?m)^(\s*)-\s*(?:cron\s*:\s*)?'?\s*([0-9*/,\-]+\s+[0-9*/,\-]+\s+[0-9*/,\-]+\s+[0-9*/,\-]+\s+[0-9*/,\-]+)\s*'?\s*$"))
                issues.Add(("cron 无空行", name, $"cron「{m.Groups[2].Value.Trim()}」前缺空行，YAML 会把它并进上一个键"));
            var graph = ParseJobGraph(text);
            totalJobs += graph.Count;
            foreach (var cycle in FindCycles(graph))
                issues.Add(("依赖环", name, $"{string.Join(" → ", cycle)}（GitHub 会拒绝运行整个工作流）"));
            foreach (var (job, missing) in FindMissingNeeds(graph))
                issues.Add(("依赖不存在的 job", name, $"{job} 依赖了未定义的 {missing}"));
            var hasCron = Regex.IsMatch(text, @"(?m)^\s*-\s*cron\s*:");
            var hasOnDemand = Regex.IsMatch(text, @"(?m)^\s*(push|pull_request|workflow_dispatch|workflow_call)\s*:");
            if (hasCron && !hasOnDemand)
                scheduleOnly.Add(name); // 只靠 cron：推代码不会触发
            if (!Regex.IsMatch(text, @"(?m)^\s*timeout-minutes\s*:"))
                issues.Add(("缺少超时", name, "未设置 timeout-minutes，挂死的 job 会一直占住并发额度"));
        }
        var output = new StringBuilder();
        output.AppendLine($"工作流卫生报告: {files.Count} 个工作流，{totalJobs} 个 job，发现 {issues.Count} 个问题");
        var kinds = issues.GroupBy(i => i.Kind, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in kinds)
        {
            output.AppendLine($"  {group.Key}: {group.Count()}");
            foreach (var issue in group.Take(maxResults))
                output.AppendLine($"    {issue.File}: {issue.Detail}");
            if (group.Count() > maxResults)
                output.AppendLine($"    …（另有 {group.Count() - maxResults} 个未显示）");
        }
        if (scheduleOnly.Count == 0)
            output.AppendLine("没有「只靠 schedule 触发」的工作流（推代码都会跑）");
        else
            output.AppendLine($"⚠ 只靠 cron 触发、推送不触发的工作流: {string.Join(", ", scheduleOnly)}");
        return output.ToString().TrimEnd();
    }

    /// <summary>解析 job 名 → needs 依赖列表（含缩进判定，避免把嵌套键当成 job）。</summary>
    internal static Dictionary<string, List<string>> ParseJobGraph(string text)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var job = Regex.Match(line, @"^(\s{2,6})([A-Za-z0-9_\-]+)\s*:\s*$");
            if (job.Success)
            {
                current = job.Groups[2].Value;
                if (!graph.ContainsKey(current))
                    graph[current] = new List<string>();
                continue;
            }
            var needs = Regex.Match(line, @"^\s*needs\s*:\s*(.+)$");
            if (!needs.Success || current is null)
                continue;
            var raw = needs.Groups[1].Value.Trim();
            IEnumerable<string> items = raw.StartsWith('[')
                ? raw.Trim('[', ']', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries)
                : raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
            graph[current].AddRange(items.Select(x => x.Trim().Trim('\'', '"')).Where(x => x.Length > 0));
        }
        return graph;
    }

    /// <summary>检测依赖环（DFS 三色标记）。GitHub Actions 遇到环会直接拒绝整个工作流。</summary>
    internal static List<string> FindCycles(Dictionary<string, List<string>> graph)
    {
        var cycles = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        void Visit(string node)
        {
            state[node] = 1; // 在栈上
            path.Add(node);
            if (graph.TryGetValue(node, out var next))
            {
                foreach (var n in next)
                {
                    if (!graph.ContainsKey(n))
                        continue;
                    if (state.GetValueOrDefault(n) == 1)
                    {
                        var start = path.IndexOf(n);
                        if (start >= 0)
                            cycles.Add(string.Join(" → ", path.Skip(start).Append(n)));
                    }
                    else if (state.GetValueOrDefault(n) == 0)
                    {
                        Visit(n);
                    }
                }
            }
            path.RemoveAt(path.Count - 1);
            state[node] = 2; // 已完成
        }
        foreach (var node in graph.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (state.GetValueOrDefault(node) == 0)
                Visit(node);
        }
        return cycles.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>找出依赖了未定义 job 的边（拼错的 job 名会让整个工作流失败）。</summary>
    internal static List<(string Job, string Missing)> FindMissingNeeds(Dictionary<string, List<string>> graph) =>
        graph.Where(p => p.Value.Any(v => !graph.ContainsKey(v)))
            .SelectMany(p => p.Value.Where(v => !graph.ContainsKey(v)).Select(v => (p.Key, v)))
            .Distinct()
            .ToList();
}
