using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>
/// 在工作区内执行 shell 命令（如 dotnet build / git status）。支持超时与（可选）执行前确认。
/// Windows 下自动检测并使用 Git Bash（找不到才退回 cmd）。
/// </summary>
public sealed class CommandTool : ITool
{
    public string Name => "run_command";
    public string Description => "在工作区中执行 shell 命令并返回输出（stdout+stderr）与退出码。用于运行构建、测试、git 等（受配置 allowCommands 控制）。每次调用都是独立进程：cd 等目录切换不会保留，需在单条命令内完成（如 cd sub && dotnet build）或用 cwd 参数。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "要执行的命令，如 dotnet build" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "超时秒数（默认 60，最大 300）" },
            ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "执行目录（相对工作区，默认工作区根）" },
            ["shell"] = new JsonObject { ["type"] = "string", ["description"] = "本次调用使用的 shell（cmd / powershell / pwsh / bash / sh），覆盖配置默认值" },
            ["env"] = new JsonObject { ["type"] = "object", ["description"] = "附加环境变量（字符串键值对，叠加到当前环境）", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("command"),
    };

    public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var shell = string.IsNullOrWhiteSpace(ctx.Config.Shell) ? ShellRunner.AutoShell() : ctx.Config.Shell;
        return ShellRunner.ExecuteCommandToolAsync(shell, args, ctx, ct);
    }
}
