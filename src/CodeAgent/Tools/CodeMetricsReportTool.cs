using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读统计代码行数构成（总行/空行/注释行/代码行）并按目录聚合。</summary>
public sealed class CodeMetricsReportTool : ITool
{
    private static readonly string[] SourceExtensions =
    {
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".go", ".rs", ".java", ".kt", ".c", ".h", ".cpp", ".hpp",
    };

    public string Name => "code_metrics_report";
    public string Description =>
        "只读统计源码文件的总行/空行/注释行/代码行构成，按顶层目录聚合，并列出代码量最大的文件。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["top_files"] = new JsonObject { ["type"] = "integer", ["description"] = "列出代码量最大的 N 个文件（默认 10，最大 100）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 8，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var topFiles = Math.Clamp(ToolArgs.GetInt(args, "top_files", 10), 0, 100);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        long total = 0, blank = 0, comment = 0, code = 0, files = 0;
        var byDir = new Dictionary<string, long>(StringComparer.Ordinal);
        var ranked = new List<(long Lines, string File)>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var (codeLines, commentLines, blankLines) = Classify(lines);
            var all = (long)lines.Length;
            files++;
            total += all;
            code += codeLines;
            comment += commentLines;
            blank += blankLines;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var slash = relative.IndexOf('/');
            var dir = slash < 0 ? "(根目录)" : relative[..slash];
            byDir[dir] = byDir.GetValueOrDefault(dir) + all;
            ranked.Add((all, relative));
        }
        if (files == 0)
            return "代码行数报告: 未发现源码文件";
        var output = new StringBuilder();
        output.AppendLine($"代码行数报告: {files:N0} 个文件，{total:N0} 行");
        output.AppendLine($"  代码行: {code:N0}   注释行: {comment:N0}   空行: {blank:N0}");
        output.AppendLine($"  注释率: {(total == 0 ? 0 : (int)Math.Round(100.0 * comment / total)):P0}");
        output.AppendLine("按目录:");
        foreach (var pair in byDir.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
            output.AppendLine($"  {pair.Key}: {pair.Value:N0} 行");
        if (topFiles > 0)
        {
            output.AppendLine($"代码量最大的文件:");
            foreach (var entry in ranked.OrderByDescending(r => r.Lines).ThenBy(r => r.File, StringComparer.Ordinal).Take(topFiles))
                output.AppendLine($"  {entry.Lines:N0} 行  {entry.File}");
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>按行归类：空行 / 注释行 / 代码行。支持 // 与 # 行注释、/* */ 块注释（按块状态机跟踪）。</summary>
    internal static (long Code, long Comment, long Blank) Classify(IReadOnlyList<string> lines)
    {
        long code = 0, comment = 0, blank = 0;
        var inBlock = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                blank++;
                continue;
            }
            if (inBlock)
            {
                comment++;
                var end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end >= 0)
                {
                    inBlock = false;
                    var rest = line[(end + 2)..].Trim();
                    if (rest.Length > 0)
                        code++;
                }
                continue;
            }
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#'))
            {
                comment++;
                continue;
            }
            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                comment++;
                var end = line.IndexOf("*/", 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    inBlock = true;
                }
                else if (line[(end + 2)..].Trim().Length > 0)
                {
                    code++; // /* c */ var a = 1; 同行闭合，闭合后的代码仍要计入
                }
                continue;
            }
            code++;
        }
        return (code, comment, blank);
    }
}
