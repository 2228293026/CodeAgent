namespace CodeAgent;

/// <summary>一种工作模式：自定义系统提示 + 可用工具范围。</summary>
public sealed record AgentMode(string Name, string Description, string SystemPrompt, string[]? AllowedTools);

/// <summary>工作模式目录（/mode 命令用）：内置模式 + 配置中的自定义模式。</summary>
public static class Modes
{
    /// <summary>只读模式可用工具（读/搜索 + stop 结束对话）。</summary>
    private static readonly string[] ReadOnlyTools = ["read_file", "list_directory", "glob", "grep", "stop"];

    public static readonly AgentMode[] All =
    [
        new("code",
            "编码模式（默认）：自主读写文件、执行命令、完成任务",
            AgentConfig.DefaultSystemPrompt,
            null),
        new("plan",
            "规划模式：只读分析项目，输出实施计划，不修改任何文件",
            "You are CodeAgent in PLAN mode. Analyze the project and produce a clear, actionable implementation plan. " +
            "Rules: NEVER modify files and NEVER run commands; use only the read/search tools; " +
            "end with the stop tool when done. Output the plan as concise bullet points.",
            ReadOnlyTools),
        new("explain",
            "解释模式：只读讲解代码结构与原理",
            "You are CodeAgent in EXPLAIN mode. Read the relevant code and explain it clearly, covering structure, " +
            "data flow, and key design decisions. Rules: NEVER modify files and NEVER run commands; " +
            "use only the read/search tools; end with the stop tool when done.",
            ReadOnlyTools),
        new("review",
            "审查模式：只读审查代码，输出问题清单与改进建议",
            "You are CodeAgent in REVIEW mode. Review the code for bugs, security issues, and improvement " +
            "opportunities. Rules: NEVER modify files and NEVER run commands; use only the read/search tools; " +
            "end with the stop tool when done. Output a prioritized findings list.",
            ReadOnlyTools),
        new("debug",
            "调试模式：先复现 bug，定位根因，最小修复并验证",
            "You are CodeAgent in DEBUG mode. Debug the reported issue: reproduce the problem first, gather " +
            "diagnostics (logs, state, stack traces), find the root cause, apply a minimal fix, then verify " +
            "with the build/tests. Rules: reproduce before fixing; investigate with read/grep/run_command; " +
            "keep fixes minimal and explain the root cause; report verification results honestly, including " +
            "failures; end with the stop tool.",
            null),
        new("refactor",
            "重构模式：小步重构代码，保持行为不变并运行验证",
            "You are CodeAgent in REFACTOR mode. Refactor code to improve structure, readability and performance " +
            "while keeping behavior identical. Rules: make small incremental changes; run the build/tests after " +
            "each change with run_command to verify; explain what you changed and why; end with the stop tool.",
            null),
        new("test",
            "测试模式：编写与运行测试，优先补充测试覆盖",
            "You are CodeAgent in TEST mode. Write and run tests for the project. Rules: inspect the existing " +
            "test structure first; add focused tests for the requested behavior; run the test suite with " +
            "run_command and report results honestly, including failures; end with the stop tool.",
            null),
        new("doc",
            "文档模式：编写与更新项目文档",
            "You are CodeAgent in DOC mode. Write and update project documentation (README, docs, comments). " +
            "Rules: read the existing docs first; keep the style consistent; update the docs index where " +
            "relevant; end with the stop tool.",
            null),
    ];

    /// <summary>完整模式目录 = 内置模式 + 配置中的自定义模式。</summary>
    public static List<AgentMode> Build(AgentConfig config)
    {
        var list = All.ToList();
        foreach (var c in config.Modes)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
                continue;
            list.Add(new AgentMode(
                c.Name,
                string.IsNullOrWhiteSpace(c.Description) ? c.Name : c.Description,
                string.IsNullOrWhiteSpace(c.SystemPrompt) ? AgentConfig.DefaultSystemPrompt : c.SystemPrompt,
                c.Tools is { Count: > 0 } ? [.. c.Tools] : null));
        }
        return list;
    }

    /// <summary>按名称查找模式（含自定义），未匹配时回退到 code 模式。</summary>
    public static AgentMode Find(string name, AgentConfig config) =>
        Build(config).FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>模式列表展示文本。</summary>
    public static string ListText(AgentConfig config) =>
        string.Join("\n", Build(config).Select(m => $"  {m.Name} — {m.Description}"));
}
