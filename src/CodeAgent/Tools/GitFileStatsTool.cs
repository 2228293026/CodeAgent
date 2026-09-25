using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读汇总单个 Git 文件的大小、行数、类型和最近提交。</summary>
public sealed class GitFileStatsTool : ITool
{
    public string Name => "git_file_stats";
    public string Description =>
        "查看单个 Git 文件的大小、行数、二进制类型和最近提交摘要，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "要统计的仓库内文件" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
        },
        ["required"] = new JsonArray("path"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = ToolArgs.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("缺少必填参数 path");
        var root = ctx.Workspace.ResolveRead(null);
        var full = ctx.Workspace.ResolveRead(path);
        if (!File.Exists(full))
            throw new ToolException($"文件不存在: {path}");
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        var info = new FileInfo(full);
        long lineCount = 0;
        var binary = false;
        await using (var stream = File.OpenRead(full))
        {
            var buffer = new byte[8192];
            var read = 0;
            while (read >= 0)
            {
                read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                    break;
                if (!binary && buffer.AsSpan(0, read).Contains((byte)0))
                    binary = true;
                if (!binary)
                {
                    foreach (var value in buffer.AsSpan(0, read))
                    {
                        if (value == (byte)'\n')
                            lineCount++;
                    }
                }
            }
        }
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(root, ["log", "-1", "--date=short", "--format=%h %ad %an %s", "--", relative], timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git log 失败（退出码 {result.ExitCode}）: {result.Error.Trim()}");
        return $"Git 文件统计: {relative}\n  大小: {info.Length:N0} 字节\n  行数: {(binary ? "不适用（二进制）" : lineCount.ToString("N0"))}\n  类型: {(binary ? "二进制" : "文本")}\n  最近提交: {(string.IsNullOrWhiteSpace(result.Output) ? "(无)" : result.Output.Trim())}";
    }
}
