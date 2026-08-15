namespace CodeAgent;

/// <summary>一种工作模式：自定义系统提示 + 可用工具范围。</summary>
public sealed record AgentMode(string Name, string Description, string SystemPrompt, string[]? AllowedTools);

/// <summary>内置工作模式目录（/mode 命令用）。</summary>
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
    ];

    /// <summary>按名称查找模式，未匹配时回退到 code 模式。</summary>
    public static AgentMode Find(string name) =>
        All.FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>模式列表展示文本。</summary>
    public static string ListText() =>
        string.Join("\n", All.Select(m => $"  {m.Name} — {m.Description}"));
}
