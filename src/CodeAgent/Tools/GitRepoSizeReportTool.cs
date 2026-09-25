using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读统计 .git 目录各子目录的体积，标出可回收的大头（pack、gc 残留）。</summary>
public sealed class GitRepoSizeReportTool : ITool
{
    public string Name => "git_repo_size_report";
    public string Description =>
        "只读统计 .git 目录各部分体积（objects/pack、refs、logs、worktrees），并给出可回收空间提示。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
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
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 20), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--absolute-git-dir"], timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException("当前目录不是 Git 仓库");
        var gitDir = result.Output.Trim();
        if (gitDir.Length == 0 || !Directory.Exists(gitDir))
            throw new ToolException("无法定位 .git 目录");
        var parts = new List<(string Name, long Bytes, int Files)>();
        long total = 0;
        foreach (var dir in Directory.EnumerateDirectories(gitDir))
        {
            ct.ThrowIfCancellationRequested();
            var (bytes, files) = Measure(dir, timeout.Token);
            total += bytes;
            parts.Add((Path.GetFileName(dir), bytes, files));
        }
        var (topBytes, topFiles) = MeasureFiles(gitDir, ct);
        total += topBytes;
        parts.Add(("（根目录文件）", topBytes, topFiles));
        if (parts.Count == 0)
            return "Git 仓库体积报告: .git 目录为空";
        parts.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        var pack = parts.FirstOrDefault(p => p.Name == "objects");
        var output = new StringBuilder();
        output.AppendLine($"Git 仓库体积报告: .git 共 {total / 1024.0 / 1024.0:0.00} MiB");
        foreach (var part in parts)
        {
            if (part.Bytes == 0 && part.Files == 0)
                continue;
            output.AppendLine($"  {part.Name}: {part.Bytes / 1024.0 / 1024.0:0.00} MiB（{part.Files} 个文件）");
        }
        output.AppendLine("提示:");
        output.AppendLine("  objects 体积主要来自历史对象；`git gc --aggressive` 可压缩但耗时较长");
        if (pack.Name is not null && pack.Bytes == 0)
            output.AppendLine("  未发现 objects 内容：仓库可能刚初始化或使用了外部对象存储");
        return output.ToString().TrimEnd();
    }

    private static (long Bytes, int Files) Measure(string dir, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch (IOException) { /* 读取期间被删除/锁定 */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        return (bytes, files);
    }

    private static (long Bytes, int Files) MeasureFiles(string dir, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bytes += new FileInfo(file).Length;
                files++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (bytes, files);
    }
}
