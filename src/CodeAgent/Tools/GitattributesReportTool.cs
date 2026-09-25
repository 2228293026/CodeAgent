using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读分析 .gitattributes 规则及其 text、eol、diff 和 filter 属性。</summary>
public sealed class GitattributesReportTool : ITool
{
    public string Name => "gitattributes_report";
    public string Description =>
        "只读发现 .gitattributes 文件并汇总路径模式、text/binary、eol、diff 和 filter 属性，不修改 Git 配置。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 8，最大 30）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多分析文件数（默认 100，最大 1000）" },
            ["max_rules"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示规则数（默认 200，最大 5000）" },
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
        var maxRules = Math.Clamp(ToolArgs.GetInt(args, "max_rules", 200), 1, 5_000);
        var files = SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false)
            .Where(file => Path.GetFileName(file).Equals(".gitattributes", StringComparison.OrdinalIgnoreCase))
            .Take(maxFiles).ToList();
        var output = new StringBuilder();
        output.AppendLine($"Gitattributes 报告: {files.Count:N0} 个文件");
        var total = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var rules = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                rules.Add(line);
            }
            total += rules.Count;
            output.AppendLine($"  {Path.GetRelativePath(root, file).Replace('\\', '/')}: {rules.Count:N0} 条规则");
            foreach (var rule in rules.Take(maxRules))
            {
                var attributes = rule.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
                var tags = attributes.Where(attribute => attribute.StartsWith("text", StringComparison.OrdinalIgnoreCase)
                    || attribute.StartsWith("binary", StringComparison.OrdinalIgnoreCase)
                    || attribute.StartsWith("eol", StringComparison.OrdinalIgnoreCase)
                    || attribute.StartsWith("diff", StringComparison.OrdinalIgnoreCase)
                    || attribute.StartsWith("filter", StringComparison.OrdinalIgnoreCase)
                    || attribute.StartsWith("merge", StringComparison.OrdinalIgnoreCase)
                    || attribute.StartsWith("export-ignore", StringComparison.OrdinalIgnoreCase)).ToArray();
                output.AppendLine($"    {rule} [{(tags.Length == 0 ? "未分类" : string.Join(", ", tags))}]");
            }
        }
        output.AppendLine($"总规则数: {total:N0}");
        return output.ToString().TrimEnd();
    }
}
