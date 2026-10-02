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
        // 文件数在**截断前**记：此前直接报 files.Count（已被 Take 截到 max_files），
        // 150 个 .gitattributes 的仓库会说"100 个文件"——**少报**，模型据此认为
        // 剩下的文件不存在，可能据此给出"已全部检查过"的结论。
        var allFiles = SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false)
            .Where(file => Path.GetFileName(file).Equals(".gitattributes", StringComparison.OrdinalIgnoreCase));
        var filesCapped = false;
        var files = new List<string>();
        foreach (var f in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (files.Count >= maxFiles)
            {
                filesCapped = true;
                break;
            }
            files.Add(f);
        }
        var output = new StringBuilder();
        // 封顶时用「≥」而不是编一个精确数：枚举到第 maxFiles+1 个就停了，
        // 后面还有多少**不知道**。报 101 是假的（150 个仓库会说 101），
        // 模型会照着这个假数字认为只剩 1 个文件没查。与 session_search 同一口径。
        output.AppendLine(filesCapped
            ? $"Gitattributes 报告: ≥{files.Count + 1:N0} 个文件（达到 max_files={maxFiles} 上限，仅检查前 {files.Count} 个）"
            : $"Gitattributes 报告: {files.Count:N0} 个文件");
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
            // 单个文件的规则也可能超上限。表头那行报的是 rules.Count（未截断），
            // 列出来的却只有 maxRules 条——不说的话，等于让人以为列全了。
            if (rules.Count > maxRules)
                output.AppendLine($"    …（另有 {rules.Count - maxRules} 条规则未显示，调大 max_rules 可看全）");
        }
        output.AppendLine($"总规则数: {total:N0}");
        return output.ToString().TrimEnd();
    }
}
