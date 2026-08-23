using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>共享的 shell 命令执行器：shell 选择、进程启动、超时、输出截断。</summary>
public static class ShellRunner
{
    /// <summary>主线程正在做控制台输入（如 y/N 确认）时置 true：
    /// 回合期间的 ESC 监视线程会吞掉所有按键，不置闩用户输入的 y/n 会被抢走。</summary>
    internal static volatile bool ConsoleInputBusy;

    /// <summary>执行命令，返回 (退出码, 格式化输出)。env 为附加环境变量（叠加到当前环境）。</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string shell, string command, string cwd, int timeoutSeconds, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (!Directory.Exists(cwd))
            throw new ToolException($"执行目录不存在: {cwd}");

        var (fileName, args) = BuildShellCommand(shell, command);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        // 用 ArgumentList 逐个传参：Linux/macOS 上 .NET 把 Arguments 字符串按双引号规则解析，
        // 单引号包裹的命令会被拆坏（bash 报语法错误退出码 2）；ArgumentList 在 Unix 直接进 argv，
        // 在 Windows 由 .NET 自动做引号转义，跨平台都安全。
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (env is not null)
        {
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;
        }

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
        catch (OperationCanceledException)
        {
            // 用户取消（ESC / Ctrl+C）：必须杀掉子进程树，否则 dotnet build 之类会变成
            // 脱管后台进程继续跑完，占 CPU 且可能继续写文件
            try { proc.Kill(entireProcessTree: true); } catch { /* 进程可能刚好退出 */ }
            throw;
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

    /// <summary>Windows 下自动检测：优先 Git Bash，没有则用 PowerShell（Windows 必有）。</summary>
    public static string AutoShell()
    {
        if (OperatingSystem.IsWindows())
            return FindGitBash() is not null ? "bash" : "powershell";
        return "";
    }

    /// <summary>命令类工具（run_command / bash / powershell）的共用执行管线：
    /// 参数校验、鉴权、超时收敛、目录解析、确认、撤销快照与结果记录——三者行为完全一致，只差 shell 选择。
    /// 模型可用 `shell` 参数按次覆盖默认选择（如必须用 powershell 跑 PS 脚本）。</summary>
    internal static async Task<string> ExecuteCommandToolAsync(string shell, JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var command = ToolArgs.GetString(args, "command");
        if (string.IsNullOrWhiteSpace(command))
            throw new ToolException("缺少必填参数 command");

        // 单次调用级 shell 覆盖（可选）：非法值明确报错而不是静默回落
        var shellArg = ToolArgs.GetString(args, "shell");
        if (!string.IsNullOrWhiteSpace(shellArg))
        {
            var v = shellArg.Trim().ToLowerInvariant();
            if (v is not ("cmd" or "powershell" or "pwsh" or "bash" or "sh"))
                throw new ToolException($"无效 shell: {shellArg}（可选 cmd / powershell / pwsh / bash / sh）");
            shell = v;
        }

        if (!ctx.Config.AllowCommands)
            return "命令执行被禁用（config.AllowCommands = false）。";

        var timeout = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", ctx.Config.CommandTimeoutSeconds), 1, 300);
        var cwdArg = ToolArgs.GetString(args, "cwd");
        var cwd = ctx.Workspace.Resolve(string.IsNullOrWhiteSpace(cwdArg) ? null : cwdArg);
        var env = ToolArgs.GetStringDict(args, "env");

        if (ctx.Config.ConfirmCommands && !await ConfirmAsync(command))
            return "用户已取消命令执行。";

        // 命令副作用撤销：执行前快照，执行后差异入栈（/undo 可回滚命令对文件的改动）
        var snapshot = UndoManager.SnapshotDir(cwd);
        var (_, output) = await RunAsync(shell, command, cwd, timeout, ct, env);
        UndoManager.RecordCommandSideEffects(cwd, snapshot, ctx.Undo);
        return output;
    }

    /// <summary>命令确认询问，返回是否放行。</summary>
    public static async Task<bool> ConfirmAsync(string command)
    {
        Console.Write($"\n[codeagent] 执行命令? {command}\n[y/N] ");
        bool answer;
        ShellRunner.ConsoleInputBusy = true; // 挡住 ESC 监视线程，防止按键被吞
        try
        {
            answer = string.Equals(await Task.Run(() => Console.ReadLine()?.Trim()), "y", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ShellRunner.ConsoleInputBusy = false;
        }
        return answer;
    }

    private static (string fileName, string[] args) BuildShellCommand(string shell, string command)
    {
        if (OperatingSystem.IsWindows())
        {
            return shell.ToLowerInvariant() switch
            {
                // 注意：不能把命令包进 & '...'——PowerShell 会把整串当命令名查找导致 CommandNotFound
                "powershell" => ("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command }),
                "pwsh" => ("pwsh", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command }),
                // 找不到 Git Bash 时不能退回 "bash.exe"——System32 下那是 WSL 启动器，
                // 命令会被带进 Linux 子系统执行（工作区路径语义完全不同）。明确报错让模型换工具。
                "bash" => (FindGitBash()
                           ?? throw new ToolException("未找到 Git Bash：bash 工具需要安装 Git for Windows（或改用 run_command / powershell）"),
                    new[] { "-lc", command }),
                _ => ("cmd.exe", new[] { "/d", "/c", command }),
            };
        }
        return shell.ToLowerInvariant() switch
        {
            "sh" => ("/bin/sh", new[] { "-lc", command }),
            // Unix 上 PowerShell 走 pwsh（曾落入默认 bash 分支：PowerShellTool 的命令被 bash 执行而报 127）
            "powershell" or "pwsh" => ("pwsh", new[] { "-NoProfile", "-Command", command }),
            _ => ("/bin/bash", new[] { "-lc", command }),
        };
    }
    /// <summary>查找 Git Bash：常见路径 → PATH 上的 git.exe 反推安装根 → where.exe 解析。</summary>
    private static string? FindGitBash()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        var candidates = new[]
        {
            Path.Combine(pf ?? "", "Git", "bin", "bash.exe"),
            Path.Combine(pf ?? "", "Git", "usr", "bin", "bash.exe"),
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
            @"C:\msys64\usr\bin\bash.exe",
            @"C:\msys32\usr\bin\bash.exe",
        };
        var hit = candidates.FirstOrDefault(File.Exists);
        if (hit is not null)
            return hit;

        // Git for Windows / Scoop 只把 cmd 加入 PATH：由 git.exe 反推安装根目录（如 D:\Program Files\Git）
        var git = FindOnPath("git.exe");
        if (git is not null)
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(git)); // ...\Git\cmd -> ...\Git
            var b = Path.Combine(root ?? "", "usr", "bin", "bash.exe");
            if (File.Exists(b))
                return b;
        }

        // 最后用 where.exe 让 Windows 在 PATH 里解析 bash
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("where.exe", "bash")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is not null)
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) is not null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || !File.Exists(line) || IsWslBash(line))
                        continue;
                    return line;
                }
            }
        }
        catch { /* 忽略 */ }

        var onPath = FindOnPath("bash.exe") ?? FindOnPath("bash");
        return onPath is not null && !IsWslBash(onPath) ? onPath : null;
    }

    /// <summary>System32 下的 bash.exe 是 WSL 启动器：用它执行会把命令带进 Linux 子系统
    /// （错误的工具链与文件系统视图），Git Bash 检测必须跳过。</summary>
    internal static bool IsWslBash(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemX86 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        return string.Equals(dir, system, StringComparison.OrdinalIgnoreCase)
            || string.Equals(dir, systemX86, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>在 PATH 中查找 pwsh（PowerShell 7）；找不到返回 null。</summary>
    public static string? FindPwsh() => FindOnPath("pwsh.exe") ?? FindOnPath("pwsh");

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;
            try
            {
                var full = Path.Combine(dir.Trim(), name);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // 忽略无权限访问的目录
            }
        }
        return null;
    }

    /// <summary>
    /// 读取输出并截断到 cap。达到上限后仍继续读取丢弃剩余字节，
    /// 否则子进程写满管道缓冲会阻塞、被超时误杀（命令本身可能很快完成）。
    /// </summary>
    private static async Task<string> ReadWithCap(StreamReader reader, int cap, CancellationToken ct)
    {
        // 读原始字节再解码：中文 Windows 上 cmd/dotnet 等输出 GBK（CP936），
        // 固定按 UTF-8 解会把中文路径/报错变成替换符，模型读不懂命令输出
        var ms = new MemoryStream();
        var buf = new byte[8192];
        var capped = false;
        while (true)
        {
            var n = await reader.BaseStream.ReadAsync(buf, ct);
            if (n == 0)
                break;
            if (!capped)
            {
                var take = Math.Min(cap - (int)ms.Length, n);
                ms.Write(buf, 0, take);
                if (take < n)
                    capped = true;
            }
        }
        var bytes = ms.ToArray();
        if (capped)
        {
            // 截断点可能切在多字节序列中间：回退到序列边界，否则整段被误判为 GBK
            var end = TextUtil.TrimPartialTail(bytes, bytes.Length);
            return TextUtil.DecodeSmart(bytes[..end]) + "\n…(输出过长，已截断)";
        }
        return TextUtil.DecodeSmart(bytes);
    }
}

/// <summary>在 PowerShell（优先 pwsh 7，否则 Windows PowerShell 5.1）中执行命令，支持管道、对象与 .NET 集成。</summary>
public sealed class BashTool : ITool
{
    public string Name => "bash";
    public string Description => "在 bash（Windows 上为 Git Bash）中执行命令，支持管道、环境变量与 Unix 工具链，适合脚本化操作。每次调用都是独立进程：目录切换不保留，请用 cwd 参数或 && 串联。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "要执行的 bash 命令" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "超时秒数（默认 60，最大 300）" },
            ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "执行目录（相对工作区，默认工作区根）" },
            ["shell"] = new JsonObject { ["type"] = "string", ["description"] = "本次调用使用的 shell（cmd / powershell / pwsh / bash / sh），覆盖工具默认值" },
            ["env"] = new JsonObject { ["type"] = "object", ["description"] = "附加环境变量（字符串键值对，叠加到当前环境）", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("command"),
    };

    public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct) =>
        ShellRunner.ExecuteCommandToolAsync("bash", args, ctx, ct);
}

/// <summary>在 PowerShell 中执行命令（优先 pwsh 7，否则 Windows PowerShell 5.1）。</summary>
public sealed class PowerShellTool : ITool
{
    public string Name => "powershell";
    public string Description => "在 PowerShell（优先 pwsh 7，否则 Windows PowerShell 5.1）中执行命令，支持管道、对象与 .NET 集成。每次调用都是独立进程：目录切换不保留，请用 cwd 参数或 ; 串联。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "要执行的 PowerShell 命令" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "超时秒数（默认 60，最大 300）" },
            ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "执行目录（相对工作区，默认工作区根）" },
            ["shell"] = new JsonObject { ["type"] = "string", ["description"] = "本次调用使用的 shell（cmd / powershell / pwsh / bash / sh），覆盖工具默认值" },
            ["env"] = new JsonObject { ["type"] = "object", ["description"] = "附加环境变量（字符串键值对，叠加到当前环境）", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("command"),
    };

    public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct) =>
        // Windows：无 pwsh 7 时用系统自带的 Windows PowerShell 5.1；其他平台需要 pwsh
        ShellRunner.ExecuteCommandToolAsync(
            OperatingSystem.IsWindows() && ShellRunner.FindPwsh() is null ? "powershell" : "pwsh",
            args, ctx, ct);
}
