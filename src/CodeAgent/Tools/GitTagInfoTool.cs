using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读查看 Git 标签的目标对象、类型和注释信息。</summary>
public sealed class GitTagInfoTool : ITool
{
    public string Name => "git_tag_info";
    public string Description =>
        "查看 Git 标签指向的对象、对象类型、标签说明和签名标记，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["tag"] = new JsonObject { ["type"] = "string", ["description"] = "标签名" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_message"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含标签注释正文（默认 true）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
        },
        ["required"] = new JsonArray("tag"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var start = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(start))
            throw new ToolException($"目录不存在: {requestedPath}");
        var tag = ToolArgs.GetString(args, "tag");
        if (string.IsNullOrWhiteSpace(tag))
            throw new ToolException("缺少必填参数 tag");
        var includeMessage = ToolArgs.GetBool(args, "include_message", true);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        async Task<string> ReadRefField(string format)
        {
            var result = await GitStatusTool.RunGitAsync(
                start, ["for-each-ref", $"refs/tags/{tag}", $"--format={format}"], timeout.Token);
            return result.ExitCode == 0 ? result.Output.Trim() : "";
        }

        var objectName = await ReadRefField("%(objectname)");
        if (string.IsNullOrWhiteSpace(objectName))
            throw new ToolException($"无法读取 Git 标签: {tag}");
        var objectType = await ReadRefField("%(objecttype)");
        var tagger = await ReadRefField("%(taggername) %(taggeremail)");
        var taggerDate = await ReadRefField("%(taggerdate:iso8601)");
        var subject = await ReadRefField("%(subject)");
        var target = await GitStatusTool.RunGitAsync(start, ["rev-parse", $"{tag}^{{}}"], timeout.Token);
        var targetType = target.ExitCode == 0
            ? await GitStatusTool.RunGitAsync(start, ["cat-file", "-t", target.Output.Trim()], timeout.Token)
            : null;
        var message = "";
        if (objectType == "tag")
        {
            var body = await GitStatusTool.RunGitAsync(start, ["cat-file", "-p", $"refs/tags/{tag}"], timeout.Token);
            var text = body.Output.Replace("\r\n", "\n");
            var separator = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (separator >= 0)
                message = text[(separator + 2)..].Trim();
        }
        var output = new StringBuilder();
        output.AppendLine($"Git 标签: {tag}");
        output.AppendLine($"  标签对象: {objectName}");
        output.AppendLine($"  标签对象类型: {objectType}");
        output.AppendLine($"  指向对象: {(target.ExitCode == 0 ? target.Output.Trim() : "未知")}");
        output.AppendLine($"  指向对象类型: {(targetType is { ExitCode: 0 } ? targetType.Output.Trim() : "未知")}");
        output.AppendLine($"  标签作者: {(string.IsNullOrWhiteSpace(tagger) ? "(轻量标签)" : tagger)}");
        output.AppendLine($"  标签时间: {(string.IsNullOrWhiteSpace(taggerDate) ? "(无)" : taggerDate)}");
        output.AppendLine($"  摘要: {(string.IsNullOrWhiteSpace(subject) ? "(无)" : subject)}");
        if (includeMessage && message.Length > 0)
            output.AppendLine($"  正文:\n{message}");
        return output.ToString().TrimEnd();
    }
}
