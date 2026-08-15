using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>
/// 在工作区内执行 shell 命令（如 dotnet build / git status）。支持超时与（可选）执行前确认。
/// Windows 下自动检测并使用 Git Bash（找不到才退回 cmd）。
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

        if (ctx.Config.ConfirmCommands && !await ShellRunner.ConfirmAsync(command))
            return "用户已取消命令执行。";

        var shell = string.IsNullOrWhiteSpace(ctx.Config.Shell) ? ShellRunner.AutoShell() : ctx.Config.Shell;
        var (_, output) = await ShellRunner.RunAsync(shell, command, cwd, timeout, ct);
        return output;
    }
}
