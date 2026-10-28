namespace CodeAgent;

/// <summary>/hooks check：给一个工具名，列出**实际会命中**的钩子。
/// matcher 只认 * 或精确名、不做前缀匹配——write 不会命中 write_file，
/// 配错时钩子就是不执行，界面上什么都不会发生。</summary>
public static class HookMatchReport
{
    public static IReadOnlyList<string> Format(IReadOnlyList<HookDefinition> hooks, string toolName, int width = 0)
    {
        var matched = hooks.Where(h => Hooks.Matches(h.Matcher, toolName)).ToList();
        var lines = new List<string>
        {
            Program.FormatResultLine($"{toolName}: 命中 {matched.Count} / {hooks.Count} 条钩子", width),
        };
        foreach (var hook in matched)
        {
            var ev = hook.Event == HookEvent.PreToolUse ? Hooks.EventNames[0] : Hooks.EventNames[1];
            var effect = hook.Event == HookEvent.PreToolUse ? "退出码 2 会否决本次调用" : "退出码 2 会把 stderr 作为反馈附给模型";
            lines.Add(Program.FormatResultLine($"{ev,-11} {hook.Matcher} {SafeColor.Glyphs.Arrow} {hook.Command}（{effect}）", width));
        }
        if (matched.Count == 0 && hooks.Count > 0)
            lines.Add(Program.FormatHintLine($"matcher 不做前缀匹配：现有 {string.Join("、", hooks.Select(h => h.Matcher).Distinct())}", width));
        return lines;
    }
}
