using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>模型完成任务后请求结束本轮会话。</summary>
public sealed class StopTool : ITool
{
    public string Name => "stop";
    public string Description => "任务完成或需要询问用户时调用，结束本轮对话并给出最终总结。";
    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "结束原因或最终总结" },
        },
    };

    public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var reason = ToolArgs.GetString(args, "reason");
        ctx.StopRequested = true;
        return Task.FromResult(
            $"已请求结束本轮任务{(string.IsNullOrWhiteSpace(reason) ? "" : "：" + reason)}");
    }
}
