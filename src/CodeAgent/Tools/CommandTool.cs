using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>
/// 在工作区内执行 shell 命令（如 dotnet build / git status）。支持超时与（可选）执行前确认。
/// </summary>
public sealed class CommandTool : ITool
{
    public string Name => "run_command";
    public string Description =>
        "在工作区中执行 shell 命令并返回输出（stdout+stderr）与退出码。用于运行构建、测试、git 等。" +
        (AllowCommandsEnabled ? "" : " 注意：配置中 AllowCommands=false，此工具不可用。");

    private static bool AllowCommandsEnabled = true;

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "要执行的命令，如 dotnet build" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "超时秒数（默认 60，最大 300）" },
            ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "执行目录（相对工作区，默认工作区根）" },
        },
        ["required"] = new JsonArray("command"),
    };

    public CommandTool()
    {
        AllowCommandsEnabled = true;
    }

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var command = ToolArgs.GetString(args, "command");
        if (string.IsNullOrWhiteSpace(command))
            throw new ToolException("缺少必填参数 command");

        if (!ctx.Config.AllowCommands)
            return "命令执行被禁用（config.AllowCommands = false）。";

        var timeout = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 60), 1, 300);
        var cwdArg = ToolArgs.GetString(args, "cwd");
        var cwd = ctx.Workspace.Resolve(string.IsNullOrWhiteSpace(cwdArg) ? null : cwdArg);

        if (ctx.Config.ConfirmCommands)
        {
            Console.Write($"\n[codeagent] 执行命令? {command}\n[y/N] ");
            var answer = Console.ReadLine()?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
                return "用户已取消命令执行。";
        }

        var (fileName, arguments) = BuildShellCommand(ctx.Config.Shell, command);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new ToolException($"无法启动进程 {fileName}: {ex.Message}");
        }

        var stdout = ReadWithCap(proc.StandardOutput, 200_000, ct);
        var stderr = ReadWithCap(proc.StandardError, 100_000, ct);
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
            return $"[命令超时（{timeout}s），已终止]\n$ {command}\n\n" + (await stdout) + (await stderr);
        }

        var outText = (await stdout).TrimEnd();
        var errText = (await stderr).TrimEnd();
        var sb = new StringBuilder();
        sb.AppendLine($"$ {command}");
        sb.AppendLine($"[退出码 {proc.ExitCode}]");
        if (outText.Length > 0)
            sb.AppendLine(outText);
        if (errText.Length > 0)
            sb.AppendLine(errText);
        return sb.ToString().TrimEnd();
    }

    /// <summary>按 shell 配置构造启动参数。Windows 默认 cmd.exe，Unix 默认 bash。</summary>
    private static (string fileName, string arguments) BuildShellCommand(string shell, string command)
    {
        if (OperatingSystem.IsWindows())
        {
            return shell.ToLowerInvariant() switch
            {
                "powershell" or "pwsh" => ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command {QuotePwsh(command)}"),
                "bash" => ("bash.exe", $"-lc {QuoteBash(command)}"),
                _ => ("cmd.exe", $"/d /c {command}"),
            };
        }

        return shell.ToLowerInvariant() switch
        {
            "sh" => ("/bin/sh", $"-lc {QuoteBash(command)}"),
            _ => ("/bin/bash", $"-lc {QuoteBash(command)}"),
        };
    }

    private static string QuoteBash(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string QuotePwsh(string s) => "& '" + s.Replace("'", "''") + "'";

    private static async Task<string> ReadWithCap(StreamReader reader, int cap, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        while (sb.Length < cap)
        {
            var n = await reader.ReadAsync(buf, ct);
            if (n == 0)
                break;
            var remaining = cap - sb.Length;
            if (n > remaining)
                n = remaining;
            sb.Append(buf, 0, n);
        }
        if (sb.Length >= cap)
            sb.Append("\n…(输出过长，已截断)");
        return sb.ToString();
    }
}
