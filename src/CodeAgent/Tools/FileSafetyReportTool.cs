using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查文件操作安全：未校验的删除目标、跟随符号链接的读写、以及路径拼接穿越。</summary>
public sealed class FileSafetyReportTool : ITool
{
    public string Name => "file_safety_report";
    public string Description =>
        "只读检查文件操作安全：直接删除未校验的路径、跟随符号链接的读写，以及用字符串拼接构造路径（.. 穿越）。";

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
        var unguardedDelete = new List<string>();
        var symlinkFollow = new List<string>();
        var pathConcat = new List<string>();
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
                var lineNo = i + 1;
                // 删除目标未经存在性/边界校验
                if (Regex.IsMatch(trimmed, @"\b(File|Directory)\.Delete\s*\(")
                    && !trimmed.Contains("exists", StringComparison.Ordinal)
                    && !trimmed.Contains("Exists", StringComparison.Ordinal)
                    && !trimmed.Contains("Resolve", StringComparison.Ordinal))
                    unguardedDelete.Add($"{relative}:{lineNo} 删除前未见存在性/边界校验（{TextUtil.TruncateLine(trimmed, 50)}）");
                // 跟随符号链接读写：链接可能指向工作区之外
                if (Regex.IsMatch(trimmed, @"File\.(ReadAllText|WriteAllText|OpenRead|OpenWrite|AppendAllText)\s*\(")
                    && !trimmed.Contains("LinkTarget", StringComparison.Ordinal)
                    && !lines.Any(l => l.Contains("Resolve", StringComparison.Ordinal) || l.Contains("LinkTarget", StringComparison.Ordinal)))
                    symlinkFollow.Add($"{relative}:{lineNo} 读写文件未解析符号链接（可能指向工作区之外）");
                // 用字符串拼接构造路径：../ 可穿越。
                // 拼接右侧可能是标识符也可能是字符串字面量（Path + "/" + name），两种都要认。
                if (Regex.IsMatch(trimmed, @"\w+\s*\+\s*(""|'|[A-Za-z_])") && trimmed.Contains("Path", StringComparison.Ordinal)
                    && !trimmed.Contains("Combine", StringComparison.Ordinal)
                    && !trimmed.Contains("GetFullPath", StringComparison.Ordinal))
                    pathConcat.Add($"{relative}:{lineNo} 用 + 拼接路径（应改 Path.Combine 并 GetFullPath 归一）");
            }
        }
        if (files == 0)
            return "文件安全报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"文件安全报告: {files} 个 .cs 文件");
        Report(output, "删除未校验", unguardedDelete, maxResults);
        Report(output, "未解析符号链接", symlinkFollow, maxResults);
        Report(output, "路径字符串拼接", pathConcat, maxResults);
        if (unguardedDelete.Count == 0 && symlinkFollow.Count == 0 && pathConcat.Count == 0)
            output.AppendLine("未发现文件操作安全问题");
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
