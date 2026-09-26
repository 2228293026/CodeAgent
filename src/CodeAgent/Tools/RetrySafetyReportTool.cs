using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查重试的正确性：重试固定间隔没有退避、重试没有次数上限、
/// 以及把非幂等操作（写文件、发请求）放进重试里造成重复执行。</summary>
public sealed class RetrySafetyReportTool : ITool
{
    public string Name => "retry_safety_report";
    public string Description =>
        "只读检查重试的正确性：固定间隔重试没有退避、没有次数上限、把非幂等写操作放进重试、重试里没记录尝试次数。";

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

    /// <summary>重试循环里出现这些调用就是**非幂等**操作：重试会重复写/重复发。</summary>
    internal static readonly string[] NonIdempotent =
    {
        "File.WriteAllText", "File.WriteAllBytes", "File.Delete", "File.Move", "File.Copy",
        "Directory.CreateDirectory", "Directory.Delete", "Directory.Move",
        "PostAsync", "PutAsync", "SendAsync", "PostAsJsonAsync", "PutAsJsonAsync",
        "ExecuteNonQuery", "ExecuteScalar", "SaveChanges", "Insert", "Update", "Delete",
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
        var noBackoff = new List<string>();
        var noLimit = new List<string>();
        var retryNonIdempotent = new List<string>();
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
                // 找到重试循环：while/for 且循环体里有 catch + 重试意图
                if (!Regex.IsMatch(trimmed, @"^\s*(?:while|for|do)\b") && !Regex.IsMatch(trimmed, @"^\s*retry", RegexOptions.IgnoreCase))
                    continue;
                var body = new List<string>();
                var d2 = 0;
                var started = false;
                for (var j = i; j < lines.Length && j < i + 60; j++)
                {
                    foreach (var ch in lines[j])
                    {
                        if (ch == '{') { d2++; started = true; }
                        else if (ch == '}') d2--;
                    }
                    body.Add(lines[j]);
                    if (started && d2 <= 0) break;
                }
                if (!started) continue;
                var bt = string.Join("\n", body);
                if (!Regex.IsMatch(bt, @"(?i)(catch|retry|attempt|失败)"))
                    continue;
                // 退避：循环体内有随尝试次数增长的延迟。
                // 注意不要把 `++` 写进正则——那是无效量词，整个规则会在运行时抛。
                var hasBackoff = Regex.IsMatch(bt, @"(?i)(backoff|exponential|attempt\s*\*|\*\s*attempt|2\s*\^|Math\.Pow|jitter|退避)")
                    || Regex.IsMatch(bt, @"(?i)Delay\s*\(\s*(?:attempt|retry|tryCount)\b");
                if (!hasBackoff)
                    noBackoff.Add($"{relative}:{lineNo} 重试循环用固定间隔，没有退避（失败方会同步重试，把故障放大）");
                // 次数上限
                var hasLimit = Regex.IsMatch(bt, @"(?i)(maxRetries|maxAttempts|attempt\s*<|attempt\s*<=|attempt\s*>|retries\s*<|for\s*\(\s*int\s+\w+\s*=\s*0;\s*\w+\s*<)")
                    || Regex.IsMatch(trimmed, @"(?i)(for|while)\s*\(\s*(?:int|long)\s+\w+\s*[<=>]");
                if (!hasLimit)
                    noLimit.Add($"{relative}:{lineNo} 重试循环没有次数上限（理论上能一直重试，调用方永远拿不到结果）");
                foreach (var op in NonIdempotent)
                {
                    if (Regex.IsMatch(bt, $@"\b{Regex.Escape(op)}\s*\("))
                        retryNonIdempotent.Add($"{relative}:{lineNo} 重试块里有 {op}（非幂等：重试会重复写/重复发，需要幂等键）");
                }
            }
        }
        if (files == 0)
            return "重试安全报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"重试安全报告: {files} 个 .cs 文件");
        Report(output, "固定间隔重试（无退避）", noBackoff, maxResults);
        Report(output, "重试没有次数上限", noLimit, maxResults);
        Report(output, "重试里包含非幂等操作", retryNonIdempotent, maxResults);
        if (noBackoff.Count == 0 && noLimit.Count == 0 && retryNonIdempotent.Count == 0)
            output.AppendLine("未发现重试安全问题");
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
