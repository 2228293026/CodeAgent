using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式查看 Git 工作区状态和差异摘要，不经过 shell 拼接命令。</summary>
public sealed class GitStatusTool : ITool
{
    public string Name => "git_status";
    public string Description =>
        "查看 Git 仓库状态（分支、已暂存/未暂存/未跟踪文件及可选 diff --stat）。使用参数化进程调用，不执行任意 shell。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_diff"] = new JsonObject { ["type"] = "boolean", ["description"] = "附带工作区和暂存区 diff 统计（默认 true）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 12000，最大 100000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var includeDiff = ToolArgs.GetBool(args, "include_diff", true);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 12_000), 1_000, 100_000);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var status = await RunGitAsync(root, ["status", "--short", "--branch"], timeout.Token);
        if (status.ExitCode != 0)
            throw new ToolException($"git status 失败（退出码 {status.ExitCode}）: {status.Error}");

        var output = new StringBuilder();
        output.AppendLine($"Git 状态: {requestedPath ?? "."}");
        output.Append(status.Output.TrimEnd());
        if (includeDiff)
        {
            var worktree = await RunGitAsync(root, ["diff", "--stat"], timeout.Token);
            var staged = await RunGitAsync(root, ["diff", "--cached", "--stat"], timeout.Token);
            if (worktree.Output.Length > 0)
            {
                output.AppendLine("\n工作区 diff:");
                output.Append(worktree.Output);
            }
            if (staged.Output.Length > 0)
            {
                output.AppendLine("\n暂存区 diff:");
                output.Append(staged.Output);
            }
        }
        return Limit(output.ToString().TrimEnd(), maxOutput);
    }

    private static async Task<GitResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new ToolException("无法启动 git");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ToolException($"无法启动 git（请确认已安装 Git）: {ex.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new GitResult(process.ExitCode, await stdout, await stderr);
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git 状态输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
