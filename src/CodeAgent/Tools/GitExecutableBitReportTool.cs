using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读统计 Git 索引里的文件模式（可执行位 / 符号链接 / 子模块 gitlink）。</summary>
public sealed class GitExecutableBitReportTool : ITool
{
    public string Name => "git_executable_bit_report";
    public string Description =>
        "只读列出 Git 索引中带可执行位（100755）、符号链接（120000）和子模块（160000）的文件，附全部模式分布。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_symlinks"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含符号链接（默认 true）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示条目数（默认 200，最大 5000）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 20，最大 120）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var start = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(start))
            throw new ToolException($"目录不存在: {requestedPath}");
        var includeSymlinks = ToolArgs.GetBool(args, "include_symlinks", true);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 200), 1, 5_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 20), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var probe = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--git-dir"], timeout.Token);
        if (probe.ExitCode != 0)
            throw new ToolException("当前目录不是 Git 仓库");
        var result = await GitStatusTool.RunGitAsync(start, ["ls-files", "--stage"], timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"无法读取索引（退出码 {result.ExitCode}）");
        var byMode = new Dictionary<string, int>(StringComparer.Ordinal);
        var executables = new List<string>();
        var symlinks = new List<string>();
        var gitlinks = new List<string>();
        foreach (var line in result.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0 || line.Length < 6)
                continue;
            var mode = line[..6];
            var file = line[(tab + 1)..];
            byMode[mode] = byMode.GetValueOrDefault(mode) + 1;
            switch (mode)
            {
                case "100755": executables.Add(file); break;
                case "120000": symlinks.Add(file); break;
                case "160000": gitlinks.Add(file); break;
            }
        }
        var output = new StringBuilder();
        output.AppendLine($"Git 文件模式报告: {byMode.Values.Sum():N0} 个索引条目");
        foreach (var pair in byMode.OrderBy(p => p.Key, StringComparer.Ordinal))
            output.AppendLine($"  {pair.Key}: {pair.Value:N0}");
        output.AppendLine($"可执行文件（100755）: {executables.Count:N0}");
        foreach (var file in executables.OrderBy(f => f, StringComparer.Ordinal).Take(maxResults))
            output.AppendLine($"  {file}");
        if (executables.Count > maxResults)
            output.AppendLine($"…（另有 {executables.Count - maxResults} 个未显示）");
        if (includeSymlinks)
        {
            output.AppendLine($"符号链接（120000）: {symlinks.Count:N0}");
            foreach (var file in symlinks.OrderBy(f => f, StringComparer.Ordinal).Take(maxResults))
                output.AppendLine($"  {file}");
        }
        output.AppendLine($"子模块（160000）: {gitlinks.Count:N0}");
        foreach (var file in gitlinks.OrderBy(f => f, StringComparer.Ordinal).Take(maxResults))
            output.AppendLine($"  {file}");
        return output.ToString().TrimEnd();
    }
}
