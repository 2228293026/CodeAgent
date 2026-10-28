namespace CodeAgent;

/// <summary>自检项的严重程度。Ok 也算一项——「检查过且通过」和「没检查」必须能区分。</summary>
public enum DoctorLevel
{
    Ok,
    Warn,
    Fail,
}

/// <summary>一条自检结论：名字 + 级别 + 现场细节 + 可操作建议（没有建议时为 null）。</summary>
public sealed record DoctorCheck(string Name, DoctorLevel Level, string Detail, string? Advice = null);

/// <summary>
/// 自检需要的外部事实。全部由调用方（Program）采好再传进来，
/// 这样判定规则是纯函数、可逐条断言——否则「自检」只能靠人工看输出。
/// </summary>
public sealed record DoctorProbe
{
    public required string ConfigPath { get; init; }
    public string WorkspacePath { get; init; } = "";
    public bool ConfigFileExists { get; init; }
    public bool ProviderKeyPresent { get; init; }
    public bool WorkspaceWritable { get; init; }
    public bool SessionDirWritable { get; init; }
    public string? ShellName { get; init; }
    public bool GitAvailable { get; init; }
    public string? GitBranch { get; init; }
    public bool CodeAgentIgnored { get; init; }
    public string RuleFileSummary { get; init; } = "";
    public int CustomCommandCount { get; init; }
    public IReadOnlyList<string> CustomCommandProblems { get; init; } = [];
    public IReadOnlyList<string> StyleNames { get; init; } = [];
    public int HookCount { get; init; }
    public string StatusLineState { get; init; } = StatusLineStates.Off;
    public string StatusLineText { get; init; } = "";
}

/// <summary>状态栏三态：未配置 / 配了且有输出 / 配了但跑不出东西。</summary>
public static class StatusLineStates
{
    public const string Off = "off";
    public const string Ok = "ok";
    public const string Empty = "empty";
}

/// <summary>自检：把「配了但没生效」这类问题一次问清。</summary>
public static class Doctor
{
    public static IReadOnlyList<DoctorCheck> Run(AgentConfig config, DoctorProbe probe)
    {
        var checks = new List<DoctorCheck>();

        checks.Add(config.Warnings.Count == 0
            ? new DoctorCheck("配置", DoctorLevel.Ok,
                probe.ConfigFileExists ? probe.ConfigPath : $"{probe.ConfigPath}（不存在，使用内置默认）")
            : new DoctorCheck("配置", DoctorLevel.Fail,
                $"{config.Warnings.Count} 条配置警告",
                string.Join("；", config.Warnings.Take(3))));

        checks.Add(probe.ProviderKeyPresent
            ? new DoctorCheck("Provider 密钥", DoctorLevel.Ok, config.Provider)
            : new DoctorCheck("Provider 密钥", DoctorLevel.Fail,
                $"{config.Provider} 未配置密钥",
                "设置 CODEAGENT_API_KEY，或写进 codeagent.json 的 apiKey"));

        checks.Add(probe.WorkspaceWritable
            ? new DoctorCheck("工作区可写", DoctorLevel.Ok, probe.WorkspacePath)
            : new DoctorCheck("工作区可写", DoctorLevel.Fail, "工作区不可写",
                "写工具和命令都会失败：换目录，或修权限"));

        checks.Add(probe.SessionDirWritable
            ? new DoctorCheck("会话目录可写", DoctorLevel.Ok, config.SessionDir)
            : new DoctorCheck("会话目录可写", DoctorLevel.Warn, $"{config.SessionDir} 不可写",
                "/resume 与 --continue 会恢复不到历史；可改 sessionDir"));

        checks.Add(string.IsNullOrWhiteSpace(probe.ShellName)
            ? new DoctorCheck("Shell", DoctorLevel.Fail, "未找到可用 shell", "Windows 需 cmd 或 powershell，或设置 config.shell")
            : new DoctorCheck("Shell", DoctorLevel.Ok, probe.ShellName));

        checks.Add(probe.GitAvailable
            ? new DoctorCheck("Git", DoctorLevel.Ok, probe.GitBranch ?? "（不在仓库内或没有分支）")
            : new DoctorCheck("Git", DoctorLevel.Warn, "git 不可用", "版本信息与分支显示会缺失；确认 git 在 PATH 中"));

        checks.Add(!probe.GitAvailable || probe.CodeAgentIgnored
            ? new DoctorCheck(".codeagent 已忽略", DoctorLevel.Ok, probe.GitAvailable ? "在 .gitignore 中" : "无 git，不需要")
            : new DoctorCheck(".codeagent 已忽略", DoctorLevel.Warn, "会话日志目录会被 git 跟踪",
                "把 .codeagent/ 加进 .gitignore，避免把会话记录提交进仓库"));

        checks.Add(new DoctorCheck("规则文件", DoctorLevel.Ok,
            probe.RuleFileSummary.Length > 0 ? probe.RuleFileSummary : "未找到 AGENTS.md / CLAUDE.md"));

        checks.Add(probe.CustomCommandProblems.Count == 0
            ? new DoctorCheck("自定义命令", DoctorLevel.Ok, $"{probe.CustomCommandCount} 条")
            : new DoctorCheck("自定义命令", DoctorLevel.Warn, $"{probe.CustomCommandCount} 条，{probe.CustomCommandProblems.Count} 条被跳过",
                string.Join("；", probe.CustomCommandProblems.Take(3))));

        var styleKnown = probe.StyleNames.Any(s => s.Equals(config.OutputStyle, StringComparison.OrdinalIgnoreCase));
        checks.Add(styleKnown
            ? new DoctorCheck("输出风格", DoctorLevel.Ok, config.OutputStyle)
            : new DoctorCheck("输出风格", DoctorLevel.Fail, $"{config.OutputStyle} 不存在",
                $"可用: {string.Join("、", probe.StyleNames)}"));

        checks.Add(new DoctorCheck("工具钩子", DoctorLevel.Ok,
            probe.HookCount == 0 ? "未配置" : $"{probe.HookCount} 条（退出码 2 可否决）"));

        checks.Add(probe.StatusLineState switch
        {
            StatusLineStates.Ok => new DoctorCheck("状态栏命令", DoctorLevel.Ok, probe.StatusLineText),
            StatusLineStates.Empty => new DoctorCheck("状态栏命令", DoctorLevel.Warn, "已配置但本次没有输出",
                "先手动执行同一条命令；失败、超时或打印为空都会这样"),
            _ => new DoctorCheck("状态栏命令", DoctorLevel.Ok, "未配置"),
        });

        checks.Add(new DoctorCheck("vim 模式", DoctorLevel.Ok, config.VimMode ? "开启" : "关闭"));
        return checks;
    }

    /// <summary>把结论渲染成可直接打印的行。失败/警告一律带「建议」，
    /// 只说「有问题」而不说「怎么办」的自检等于没做。</summary>
    public static IReadOnlyList<string> FormatReport(IReadOnlyList<DoctorCheck> checks, int width = 0)
    {
        var ok = checks.Count(c => c.Level == DoctorLevel.Ok);
        var warn = checks.Count(c => c.Level == DoctorLevel.Warn);
        var fail = checks.Count(c => c.Level == DoctorLevel.Fail);
        var lines = new List<string>
        {
            Program.FormatSectionHeaderLine("自检", $"{ok} 通过 / {warn} 警告 / {fail} 失败", width),
        };
        var nameWidth = checks.Count == 0 ? 0 : checks.Max(c => TextUtil.DisplayWidth(c.Name));
        foreach (var check in checks)
        {
            var glyph = check.Level switch
            {
                DoctorLevel.Ok => SafeColor.Glyphs.OkCheck,
                DoctorLevel.Warn => SafeColor.Glyphs.Warn,
                _ => SafeColor.Glyphs.Error,
            };
            var pad = new string(' ', Math.Max(0, nameWidth - TextUtil.DisplayWidth(check.Name)));
            lines.Add(Program.FormatResultLine($"{glyph} {check.Name}{pad}：{check.Detail}", width));
            if (check.Advice is { Length: > 0 } advice)
                lines.Add(Program.FormatHintLine($"   建议: {advice}", width));
        }
        return lines;
    }
}
