using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>共享的 shell 命令执行器：shell 选择、进程启动、超时、输出截断。</summary>
public static class ShellRunner
{
    /// <summary>执行命令，返回 (退出码, 格式化输出)。</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string shell, string command, string cwd, int timeoutSeconds, CancellationToken ct)
    {
        var (fileName, arguments) = BuildShellCommand(shell, command);
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
            return (124, $"[命令超时（{timeoutSeconds}s），已终止]\n$ {command}\n\n" + (await stdout) + (await stderr));
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
        return (proc.ExitCode, sb.ToString().TrimEnd());
    }

    /// <summary>Windows 下自动检测 Git Bash；找不到时返回空（由调用方退回 cmd）。</summary>
    public static string AutoShell()
    {
        if (OperatingSystem.IsWindows() && FindGitBash() is not null)
            return "bash";
        return "";
    }

    /// <summary>命令确认询问，返回是否放行。</summary>
    public static async Task<bool> ConfirmAsync(string command)
    {
        Console.Write($"\n[codeagent] 执行命令? {command}\n[y/N] ");
        var answer = await Task.Run(() => Console.ReadLine()?.Trim());
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase);
    }

    private static (string fileName, string arguments) BuildShellCommand(string shell, string command)
    {
        if (OperatingSystem.IsWindows())
        {
            return shell.ToLowerInvariant() switch
            {
                "powershell" or "pwsh" => ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command {QuotePwsh(command)}"),
                "bash" => (FindGitBash() ?? "bash.exe", $"-lc {QuoteBash(command)}"),
                _ => ("cmd.exe", $"/d /c {command}"),
            };
        }
        return shell.ToLowerInvariant() switch
        {
            "sh" => ("/bin/sh", $"-lc {QuoteBash(command)}"),
            _ => ("/bin/bash", $"-lc {QuoteBash(command)}"),
        };
    }

    /// <summary>常见 Git Bash / MSYS2 安装路径。</summary>
    private static string? FindGitBash()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        var candidates = new[]
        {
            Path.Combine(pf ?? "", "Git", "bin", "bash.exe"),
            Path.Combine(pf ?? "", "Git", "usr", "bin", "bash.exe"),
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\msys64\usr\bin\bash.exe",
            @"C:\msys32\usr\bin\bash.exe",
        };
        return candidates.FirstOrDefault(File.Exists);
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

/// <summary>在 bash（Windows 上为 Git Bash）中执行命令，支持管道、环境变量与 Unix 工具链。</summary>
public sealed class BashTool : ITool
{
    public string Name => "bash";
    public string Description => "在 bash（Windows 上为 Git Bash）中执行命令，支持管道、环境变量与 Unix 工具链，适合脚本化操作。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "要执行的 bash 命令" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "超时秒数（默认 60，最大 300）" },
            ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "执行目录（相对工作区，默认工作区根）" },
        },
        ["required"] = new JsonArray("command"),
    };

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

        if (ctx.Config.ConfirmCommands && !await ShellRunner.ConfirmAsync(command))
            return "用户已取消命令执行。";

        var (_, output) = await ShellRunner.RunAsync("bash", command, cwd, timeout, ct);
        return output;
    }
}
