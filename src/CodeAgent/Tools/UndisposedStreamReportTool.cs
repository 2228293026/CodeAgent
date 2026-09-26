using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查文件与流的资源泄漏：using 没包住 FileStream/StreamWriter、
/// StreamReader/StreamWriter 没调 Dispose、以及 new 出来的 IDisposable 被直接返回。</summary>
public sealed class UndisposedStreamReportTool : ITool
{
    public string Name => "undisposed_stream_report";
    public string Description =>
        "只读检查流与文件句柄泄漏：new FileStream/StreamWriter/StreamReader 没有 using、没有 Dispose，返回类型是可释放对象却直接 new。";

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

    /// <summary>需要显式释放的类型。<c>MemoryStream</c> 不在其中——它不占系统句柄，
    /// 把它算进去会淹没真正的问题。</summary>
    internal static readonly string[] DisposableTypes =
    {
        "FileStream", "StreamReader", "StreamWriter", "FileInfo", "DirectoryInfo",
        "SqlConnection", "HttpClient", "Process", "BinaryReader", "BinaryWriter", "ZipArchive",
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
        var notWrapped = new List<string>();
        var noDispose = new List<string>();
        var returnedDirectly = new List<string>();
        var files = 0;
        var pattern = @"\bnew\s+(?:" + string.Join('|', DisposableTypes) + @")\s*\(";
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
            var usingDepth = new List<bool>();
            var pendingUsing = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                var m = Regex.Match(trimmed, pattern);
                if (m.Success)
                {
                    var type = Regex.Match(trimmed, @"new\s+(?<t>\w+)\s*\(").Groups["t"].Value;
                    var varName = Regex.Match(trimmed, @"(?:var|[\w\.<>,\[\]]+)\s+(?<v>\w+)\s*=\s*(?:new\s+\w+\s*\()?").Groups["v"].Value;
                    var guarded = usingDepth.Exists(x => x)
                        || Regex.IsMatch(trimmed, @"^\s*using\s")
                        || Regex.IsMatch(trimmed, @"\)\s*;?\s*$") && Regex.IsMatch(line, @"using\s*\(");
                    if (!guarded && varName.Length > 0)
                    {
                        // 同一行后面出现 Dispose/using 就算处理了
                        var rest = string.Join("\n", lines.Skip(i).Take(6));
                        var handled = Regex.IsMatch(rest, $@"\b{Regex.Escape(varName)}\s*\.\s*Dispose\s*\(")
                            || Regex.IsMatch(rest, $@"using\s*\(.*\b{Regex.Escape(varName)}\b");
                        if (!handled)
                            notWrapped.Add($"{relative}:{lineNo} new {type}（{varName}）既没有 using 也没有 Dispose");
                    }
                    // 直接把 new 出来的可释放对象作为返回值
                    if (Regex.IsMatch(trimmed, @"=>\s*new\s+\w+\s*\(") || Regex.IsMatch(trimmed, @"return\s+new\s+\w+\s*\("))
                        returnedDirectly.Add($"{relative}:{lineNo} 直接返回 new 出来的 {type}（调用方必须记得释放，签名上看不出来）");
                }
                var ds = Regex.Match(trimmed, @"\b(?<v>\w+)\s*\.\s*(?:Dispose|Close)\s*\(\s*\)");
                if (ds.Success && !usingDepth.Exists(x => x))
                    noDispose.Add($"{relative}:{lineNo} 手动 {trimmed.Split('.')[^1].Trim().TrimEnd('(', ')', ';')} 而不是 using（异常路径上会漏掉）");
                if (Regex.IsMatch(trimmed, @"^\s*(?:public|private|internal|protected).*\busing\s*\("))
                    pendingUsing = true;
                var firstIndex = usingDepth.Count;
                var opened = 0;
                foreach (var ch in line)
                {
                    if (ch == '{') { usingDepth.Add(pendingUsing); pendingUsing = false; opened++; }
                    else if (ch == '}' && usingDepth.Count > 0) usingDepth.RemoveAt(usingDepth.Count - 1);
                }
                if (pendingUsing && opened > 0) { usingDepth[firstIndex] = true; pendingUsing = false; }
            }
        }
        if (files == 0)
            return "流泄漏报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"流泄漏报告: {files} 个 .cs 文件");
        Report(output, "未包在 using 里的可释放对象", notWrapped, maxResults);
        Report(output, "手动 Dispose/Close", noDispose, maxResults);
        Report(output, "直接返回可释放对象", returnedDirectly, maxResults);
        if (notWrapped.Count == 0 && noDispose.Count == 0 && returnedDirectly.Count == 0)
            output.AppendLine("未发现流泄漏风险");
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
