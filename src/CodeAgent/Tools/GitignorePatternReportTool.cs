using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读分析 .gitignore 文件中的模式、否定规则和通配符特征。</summary>
public sealed class GitignorePatternReportTool : ITool
{
    public string Name => "gitignore_pattern_report";
    public string Description =>
        "只读发现 .gitignore 文件，统计有效模式、否定规则、目录规则和通配符，不修改 Git 配置。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 8，最大 30）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多分析 .gitignore 文件数（默认 100，最大 1000）" },
            ["max_patterns"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示模式数（默认 200，最大 5000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var depth = Math.Clamp(ToolArgs.GetInt(args, "depth", 8), 0, 30);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 100), 1, 1_000);
        var maxPatterns = Math.Clamp(ToolArgs.GetInt(args, "max_patterns", 200), 1, 5_000);
        var files = SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false)
            .Where(file => Path.GetFileName(file).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
            .Take(maxFiles).ToList();
        var output = new StringBuilder();
        output.AppendLine($"Gitignore 模式报告: {files.Count:N0} 个文件");
        var total = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var patterns = new List<(string Value, bool Negated, bool Directory, bool Anchored, bool Wildcard)>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var escaped = line.StartsWith("\\#") || line.StartsWith("\\!");
                if (escaped) line = line[1..];
                var negated = !escaped && line.StartsWith('!');
                if (negated) line = line[1..];
                var directory = line.EndsWith('/');
                if (directory) line = line.TrimEnd('/');
                var anchored = line.StartsWith('/') || line.Contains('/');
                if (line.StartsWith('/')) line = line.TrimStart('/');
                patterns.Add((line, negated, directory, anchored, line.Contains('*') || line.Contains('?') || line.Contains('[')));
            }
            total += patterns.Count;
            output.AppendLine($"  {Path.GetRelativePath(root, file).Replace('\\', '/')}: {patterns.Count:N0} 个模式");
            foreach (var pattern in patterns.Take(maxPatterns))
                output.AppendLine($"    {pattern.Value} [{(pattern.Negated ? "否定" : "忽略")}{(pattern.Directory ? ",目录" : "")}{(pattern.Anchored ? ",锚定" : "")}{(pattern.Wildcard ? ",通配" : "")}]");
        }
        output.AppendLine($"总模式数: {total:N0}");
        return output.ToString().TrimEnd();
    }
}
