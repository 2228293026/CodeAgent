using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查文档同步：新增公开类型但 README 未提及、以及注释与实现描述不符。</summary>
public sealed class DocumentationSyncReportTool : ITool
{
    public string Name => "documentation_sync_report";
    public string Description =>
        "只读检查文档同步：新增的公开类型没写进 README、公开方法缺少 XML 文档注释、以及被注释掉的死代码。";

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

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var undocumented = new List<string>();
        var readmeMissing = new List<string>();
        var commentedOutCode = new List<string>();
        var readme = await ReadIfExists(Path.Combine(root, "README.md"), ct);
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
            var fileName = Path.GetFileNameWithoutExtension(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                // 公开成员缺 XML 文档
                var isPublicMember = Regex.IsMatch(trimmed, @"^public\s+(?!partial|sealed|abstract|static\s+class)");
                if (isPublicMember)
                {
                    var documented = i > 0
                        && (lines[i - 1].Trim().StartsWith("///", StringComparison.Ordinal)
                            || lines[i - 1].Trim().StartsWith("[", StringComparison.Ordinal));
                    // 表达式体/单行实现不需要文档（自解释）
                    if (!documented && !trimmed.Contains("=>", StringComparison.Ordinal) && !trimmed.Contains("{", StringComparison.Ordinal))
                        undocumented.Add($"{relative}:{lineNo} 公开成员缺少 XML 文档注释（{TextUtil.TruncateLine(trimmed, 45)}）");
                }
                // README 里没有这个文件名（新增文件忘了登记）
                if (trimmed.StartsWith("public ", StringComparison.Ordinal)
                    && trimmed.Contains("class ", StringComparison.Ordinal)
                    && fileName.EndsWith("Tool", StringComparison.Ordinal)
                    && readme is not null
                    && !readme.Contains(fileName, StringComparison.Ordinal))
                    readmeMissing.Add($"{relative} README 未提及 {fileName}");
                // 被注释掉的死代码：整行是注释，但内容还是 C# 语句
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    && Regex.IsMatch(trimmed, @"^//\s*(var|return|if\s*\(|for\s*\(|foreach|while\s*\(|throw|await|int |string |bool )"))
                    commentedOutCode.Add($"{relative}:{lineNo} 疑似注释掉的死代码（{TextUtil.TruncateLine(trimmed, 45)}）");
            }
        }
        if (files == 0)
            return "文档同步报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"文档同步报告: {files} 个 .cs 文件{(readme is null ? "（未找到 README.md）" : "")}");
        Report(output, "公开成员缺文档", undocumented, maxResults);
        Report(output, "README 未登记", readmeMissing, maxResults);
        Report(output, "注释掉的死代码", commentedOutCode, maxResults);
        if (undocumented.Count == 0 && readmeMissing.Count == 0 && commentedOutCode.Count == 0)
            output.AppendLine("未发现文档同步问题");
        return output.ToString().TrimEnd();
    }

    private static async Task<string?> ReadIfExists(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        try { return await File.ReadAllTextAsync(path, ct); }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return null; }
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
