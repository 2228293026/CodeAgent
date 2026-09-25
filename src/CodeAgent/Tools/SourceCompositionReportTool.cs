using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读按语言统计代码规模（行数、注释、空白占比），并标出注释比例异常的源文件。</summary>
public sealed class SourceCompositionReportTool : ITool
{
    public string Name => "source_composition_report";
    public string Description =>
        "只读按语言统计代码行/注释行/空白行构成，标出注释比例异常低或异常高的源文件。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["languages"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "只看指定语言（cs/py/js/ts/go/rs/java 等），默认全部",
            },
            ["low_comment_ratio"] = new JsonObject { ["type"] = "integer", ["description"] = "注释比例低于此百分比（0-100）标为欠注释，默认 5" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 8，最大 32）" },
        },
    };

    private static readonly Dictionary<string, (string Line, string BlockStart, string BlockEnd)> Comments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = ("//", "/*", "*/"),
        ["py"] = ("#", "\"\"\"", "\"\"\""),
        ["js"] = ("//", "/*", "*/"),
        ["ts"] = ("//", "/*", "*/"),
        ["go"] = ("//", "/*", "*/"),
        ["rs"] = ("//", "/*", "*/"),
        ["java"] = ("//", "/*", "*/"),
        ["c"] = ("//", "/*", "*/"),
        ["cpp"] = ("//", "/*", "*/"),
        ["rb"] = ("#", "=begin", "=end"),
        ["sh"] = ("#", null!, null!),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        var lowRatio = ToolArgs.GetInt(args, "low_comment_ratio", 5) / 100.0;
        if (lowRatio < 0 || lowRatio > 1)
            throw new ToolException("low_comment_ratio 必须在 0 到 100（百分比）之间");
        var wanted = ToolArgs.GetStringList(args, "languages") ?? new List<string>();
        var wantedSet = wanted.Count == 0
            ? null
            : new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        var stats = new Dictionary<string, (int Files, int Code, int Comment, int Blank)>();
        var low = new List<(string File, int Code, int Comment, double Ratio)>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var language = LanguageOf(file);
            if (language is null || !Comments.ContainsKey(language))
                continue;
            if (wantedSet is not null && !wantedSet.Contains(language))
                continue;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var (code, comment, blank) = Classify(lines, Comments[language]);
            var current = stats.GetValueOrDefault(language);
            stats[language] = (current.Files + 1, current.Code + code, current.Comment + comment, current.Blank + blank);
            if (code + comment > 0)
            {
                var ratio = (double)comment / (code + comment);
                if (ratio < lowRatio)
                    low.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), code, comment, ratio));
            }
        }
        if (stats.Count == 0)
            return "代码构成报告: 未找到受支持的源文件";
        var output = new StringBuilder();
        output.AppendLine("代码构成报告:");
        output.AppendLine("  语言      文件      代码行     注释行     空白行   注释占比");
        foreach (var pair in stats.OrderByDescending(p => p.Value.Code))
        {
            var v = pair.Value;
            var total = v.Code + v.Comment;
            var ratio = total == 0 ? 0 : (double)v.Comment / total;
            output.AppendLine($"  {pair.Key,-8} {v.Files,7:N0} {v.Code,10:N0} {v.Comment,10:N0} {v.Blank,10:N0}   {ratio,7:P0}");
        }
        if (low.Count > 0)
        {
            output.AppendLine($"注释比例低于 {lowRatio:P0} 的文件: {low.Count}");
            foreach (var hit in low.OrderBy(h => h.Ratio).ThenBy(h => h.File, StringComparer.Ordinal).Take(20))
                output.AppendLine($"  {hit.Ratio,7:P0}  {hit.Code,6:N0} 行  {hit.File}");
            if (low.Count > 20)
                output.AppendLine($"…（另有 {low.Count - 20} 个未显示）");
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>按扩展名判定语言；未知返回 null（不计入）。</summary>
    internal static string? LanguageOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0)
            return null;
        var key = ext[1..].ToLowerInvariant();
        return key is "cs" or "py" or "js" or "ts" or "go" or "rs" or "java" or "c" or "cpp" or "rb" or "sh" ? key : null;
    }

    /// <summary>逐行分类为代码/注释/空白，支持行注释与块注释（含跨行块注释）。</summary>
    internal static (int Code, int Comment, int Blank) Classify(string[] lines, (string Line, string BlockStart, string BlockEnd) syntax)
    {
        int code = 0, comment = 0, blank = 0;
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
                if (syntax.BlockEnd is not null && line.Contains(syntax.BlockEnd, StringComparison.Ordinal))
                    inBlock = false;
                continue;
            }
            if (syntax.BlockStart is not null
                && line.StartsWith(syntax.BlockStart, StringComparison.Ordinal))
            {
                comment++;
                var blockStart = syntax.BlockStart;
                var blockEnd = syntax.BlockEnd;
                if (blockEnd is not null && !line.Contains(blockEnd, StringComparison.Ordinal))
                {
                    // 跨行块注释的开头行：整行都是注释。
                    // 曾在此继续统计"尾部代码"，而开头行根本没有闭合标记，
                    // 同一行被重复计入代码与注释，比例随之失真。
                    inBlock = true;
                    continue;
                }
                // /* c */ var a = 1; —— 闭合在同一行，尾部代码仍要计入
                var after = Regex.Replace(line, Regex.Escape(blockStart) + ".*?" + Regex.Escape(blockEnd ?? blockStart), string.Empty);
                if (after.Trim().Length > 0)
                    code++;
                continue;
            }
            if (line.StartsWith(syntax.Line, StringComparison.Ordinal))
            {
                comment++;
                continue;
            }
            code++;
        }
        return (code, comment, blank);
    }
}
