using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式查看 Git remote 名称和地址，并在输出中隐藏凭据。</summary>
public sealed class GitRemotesTool : ITool
{
    public string Name => "git_remotes";
    public string Description =>
        "列出 Git remote 名称及 fetch/push 地址，自动隐藏 URL 中的用户名和密码；支持名称筛选、输出限制和超时。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "remote 名称模式，如 origin" },
            ["max_remotes"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示 remote 数（默认 100，最大 1000）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 30000，最大 200000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxRemotes = Math.Clamp(ToolArgs.GetInt(args, "max_remotes", 100), 1, 1_000);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 30_000), 1_000, 200_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        var pattern = ToolArgs.GetString(args, "pattern");
        if (!string.IsNullOrWhiteSpace(pattern) && pattern.StartsWith('-'))
            throw new ToolException("pattern 不能以 '-' 开头");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(ctx.Workspace.Root, ["remote", "-v"], timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git remote -v 失败（退出码 {result.ExitCode}）: {result.Error}");
        if (string.IsNullOrWhiteSpace(result.Output))
            return "没有配置 Git remote。";

        var remotes = new List<(string Name, string Fetch, string Push)>();
        foreach (var line in result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || (fields[2] != "(fetch)" && fields[2] != "(push)"))
                continue;
            var name = fields[0];
            if (!string.IsNullOrWhiteSpace(pattern) && !name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                continue;
            var url = RedactUrl(fields[1]);
            var index = remotes.FindIndex(r => r.Name == name);
            if (index < 0)
                remotes.Add((name, url, url));
            else if (fields[2] == "(fetch)")
                remotes[index] = (name, url, remotes[index].Push);
            else
                remotes[index] = (name, remotes[index].Fetch, url);
        }
        if (remotes.Count == 0)
            return "没有匹配的 Git remote。";
        var shown = Math.Min(remotes.Count, maxRemotes);
        var output = new StringBuilder();
        output.AppendLine($"Git remote: {remotes.Count} 个");
        for (var i = 0; i < shown; i++)
        {
            var remote = remotes[i];
            output.AppendLine($"{remote.Name}  fetch: {remote.Fetch}  push: {remote.Push}");
        }
        if (shown < remotes.Count)
            output.AppendLine($"…（另有 {remotes.Count - shown} 个 remote 未显示）");
        return Limit(output.ToString().TrimEnd(), maxOutput);
    }

    private static string RedactUrl(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return url;
        var authorityStart = schemeEnd + 3;
        var authorityEnd = url.IndexOf('/', authorityStart);
        if (authorityEnd < 0)
            authorityEnd = url.Length;
        var authority = url[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');
        if (at < 0)
            return url;
        return url[..authorityStart] + "***@" + authority[(at + 1)..] + url[authorityEnd..];
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git remote 输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
