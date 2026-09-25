using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查测试隔离性：依赖执行顺序、依赖真实时间/网络/文件系统状态、缺少清理的共享夹具。</summary>
public sealed class TestIsolationReportTool : ITool
{
    public string Name => "test_isolation_report";
    public string Description =>
        "只读检查测试隔离性：依赖执行顺序的共享状态、依赖真实时间/网络/随机数的用例、以及缺少清理的 IDisposable 测试类。";

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
        var sharedState = new List<string>();
        var nondeterministic = new List<string>();
        var missingCleanup = new List<string>();
        var tests = 0;
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
            var isTestFile = false;
            var disposesField = lines.Any(l => l.Contains("Dispose", StringComparison.Ordinal)
                || l.Contains("Directory.Delete", StringComparison.Ordinal)
                || l.Contains("File.Delete", StringComparison.Ordinal));
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (Regex.IsMatch(line, @"\[(Fact|Theory|Test|TestMethod)\b"))
                    isTestFile = true;
                var lineNo = i + 1;
                // 共享可变静态字段：测试之间互相污染。
                // 必须扫**整个类**——字段声明在 [Fact] 之前，只看测试体里的行会完全漏掉。
                if (Regex.IsMatch(line, @"\bstatic\s+(?!readonly\b)\w+\s+_\w+\s*[=;]"))
                    sharedState.Add($"{relative}:{lineNo} 测试类持有静态可变字段（用例间会互相污染）");
                if (Regex.IsMatch(line, @"Environment\.CurrentDirectory\s*="))
                    sharedState.Add($"{relative}:{lineNo} 改写进程级当前目录（影响同进程其他用例）");
                if (!isTestFile)
                    continue;
                tests++;
                if (Regex.IsMatch(line, @"\b(DateTime\.(Now|UtcNow|Today)|Random\.Shared|new\s+Random\s*\()"))
                    nondeterministic.Add($"{relative}:{lineNo} 用例依赖真实时间/随机数（重跑可能失败）");
                if (Regex.IsMatch(line, @"new\s+HttpClient\s*\(") || Regex.IsMatch(line, @"\bWebRequest\b"))
                    nondeterministic.Add($"{relative}:{lineNo} 用例访问真实网络（离线环境必然失败）");
            }
            // IDisposable 测试类却没有 Dispose：临时文件/目录会残留并影响后续运行
            if (isTestFile && !disposesField
                && Regex.Match(string.Join("\n", lines), @"class\s+(\w+)\s*:\s*[^\n]*\bIDisposable\b").Success)
                missingCleanup.Add($"{relative} 实现 IDisposable 但未见清理（临时文件会残留）");
        }
        if (files == 0)
            return "测试隔离性报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"测试隔离性报告: {files} 个文件，{tests} 个测试行");
        Report(output, "共享可变静态状态", sharedState, maxResults);
        Report(output, "非确定性依赖", nondeterministic, maxResults);
        Report(output, "缺少清理", missingCleanup, maxResults);
        if (sharedState.Count == 0 && nondeterministic.Count == 0 && missingCleanup.Count == 0)
            output.AppendLine("未发现测试隔离性问题");
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
