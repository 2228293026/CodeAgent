using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读汇总 GitHub Actions 工作流和常见 CI 命令。</summary>
public sealed class CiWorkflowReportTool : ITool
{
    public string Name => "ci_workflow_report";
    public string Description =>
        "只读发现 .yml/.yaml CI 工作流，汇总触发器、runner、构建格式化和测试命令，不执行或修改工作流。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 6，最大 20）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏目录（默认 true，以便发现 .github）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多分析工作流数（默认 100，最大 1000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var depth = Math.Clamp(ToolArgs.GetInt(args, "depth", 6), 0, 20);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", true);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 100), 1, 1_000);
        var workflows = new List<string>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            if (Path.GetExtension(file).Equals(".yml", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(file).Equals(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                workflows.Add(file);
                if (workflows.Count >= maxFiles)
                    break;
            }
        }
        var output = new StringBuilder();
        output.AppendLine($"CI 工作流报告: {workflows.Count:N0} 个 YAML 文件");
        foreach (var file in workflows)
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try
            {
                if (new FileInfo(file).Length > 1_048_576)
                    continue;
                text = await File.ReadAllTextAsync(file, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var name = Match(text, @"(?m)^\s*name:\s*['""]?([^#\r\n]+)") ?? Path.GetFileName(file);
            var triggers = new List<string>();
            foreach (var trigger in new[] { "push", "pull_request", "workflow_dispatch", "schedule", "workflow_call" })
                if (Regex.IsMatch(text, $@"(?m)^\s*(?:-\s*)?{trigger}:\s*(?:$|[^\r\n])", RegexOptions.IgnoreCase))
                    triggers.Add(trigger);
            var runners = Regex.Matches(text, @"(?m)^\s*runs-on:\s*([^\r\n#]+)").Select(match => match.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var commands = new[] { "dotnet format", "dotnet build", "dotnet test", "npm test", "pytest", "cargo test" }
                .Where(command => text.Contains(command, StringComparison.OrdinalIgnoreCase)).ToArray();
            output.AppendLine($"  {relative}: {name.Trim()}");
            output.AppendLine($"    触发器: {(triggers.Count == 0 ? "(未识别)" : string.Join(", ", triggers))}");
            output.AppendLine($"    Runner: {(runners.Length == 0 ? "(未声明)" : string.Join(", ", runners))}");
            output.AppendLine($"    CI 命令: {(commands.Length == 0 ? "(未识别)" : string.Join(", ", commands))}");
        }
        return output.ToString().TrimEnd();
    }

    private static string? Match(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
