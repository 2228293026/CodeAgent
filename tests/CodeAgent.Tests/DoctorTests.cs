using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DoctorTests
{
    private static DoctorProbe Healthy() => new()
    {
        ConfigPath = "/tmp/codeagent.json",
        WorkspacePath = "/tmp",
        ConfigFileExists = true,
        ProviderKeyPresent = true,
        WorkspaceWritable = true,
        SessionDirWritable = true,
        ShellName = "bash",
        GitAvailable = true,
        GitBranch = "(main)",
        CodeAgentIgnored = true,
        RuleFileSummary = "AGENTS.md（Loaded）",
        CustomCommandCount = 2,
        StyleNames = ["default", "concise"],
        HookCount = 0,
        StatusLineState = StatusLineStates.Off,
    };

    [Fact]
    public void HealthyEnvironmentHasNoWarningsOrFailures()
    {
        var checks = Doctor.Run(new AgentConfig(), Healthy());
        Assert.All(checks, c => Assert.Equal(DoctorLevel.Ok, c.Level));
    }

    [Fact]
    public void ConfigWarningsBecomeAFailureWithTheFirstFewListed()
    {
        var config = new AgentConfig();
        config.Warnings.Add("未知配置项 'x'");
        var check = Check(Doctor.Run(config, Healthy()), "配置");
        Assert.Equal(DoctorLevel.Fail, check.Level);
        Assert.Contains("未知配置项", check.Advice);
    }

    [Fact]
    public void MissingProviderKeyFailsWithAdvice()
    {
        var probe = Healthy() with { ProviderKeyPresent = false };
        var check = Check(Doctor.Run(new AgentConfig(), probe), "Provider 密钥");
        Assert.Equal(DoctorLevel.Fail, check.Level);
        Assert.Contains("CODEAGENT_API_KEY", check.Advice);
    }

    [Fact]
    public void UnwritableSessionDirOnlyWarns()
    {
        var probe = Healthy() with { SessionDirWritable = false };
        Assert.Equal(DoctorLevel.Warn, Check(Doctor.Run(new AgentConfig(), probe), "会话目录可写").Level);
    }

    [Fact]
    public void MissingShellIsAFailure()
    {
        var probe = Healthy() with { ShellName = "" };
        Assert.Equal(DoctorLevel.Fail, Check(Doctor.Run(new AgentConfig(), probe), "Shell").Level);
    }

    [Fact]
    public void MissingGitOnlyWarns()
    {
        var probe = Healthy() with { GitAvailable = false, GitBranch = null };
        var checks = Doctor.Run(new AgentConfig(), probe);
        Assert.Equal(DoctorLevel.Warn, Check(checks, "Git").Level);
        // 没有 git 时「.codeagent 是否被忽略」不该再报问题（没东西可提交）
        Assert.Equal(DoctorLevel.Ok, Check(checks, ".codeagent 已忽略").Level);
    }

    [Fact]
    public void TrackedSessionDirectoryWarnsAboutLeakingLogs()
    {
        var probe = Healthy() with { CodeAgentIgnored = false };
        var check = Check(Doctor.Run(new AgentConfig(), probe), ".codeagent 已忽略");
        Assert.Equal(DoctorLevel.Warn, check.Level);
        Assert.Contains(".gitignore", check.Advice);
    }

    [Fact]
    public void UnknownOutputStyleFailsAndListsWhatIsAvailable()
    {
        var config = new AgentConfig { OutputStyle = "nope" };
        var check = Check(Doctor.Run(config, Healthy()), "输出风格");
        Assert.Equal(DoctorLevel.Fail, check.Level);
        Assert.Contains("concise", check.Advice);
    }

    [Fact]
    public void ConfiguredButSilentStatusLineWarns()
    {
        var probe = Healthy() with { StatusLineState = StatusLineStates.Empty };
        var check = Check(Doctor.Run(new AgentConfig(), probe), "状态栏命令");
        Assert.Equal(DoctorLevel.Warn, check.Level);
        Assert.False(string.IsNullOrWhiteSpace(check.Advice));
    }

    [Fact]
    public void EveryNonOkCheckCarriesActionableAdvice()
    {
        // 只说「有问题」不说「怎么办」的自检等于没做。这条把所有降级路径一次扫完。
        var config = new AgentConfig { OutputStyle = "nope" };
        config.Warnings.Add("未知配置项 'x'");
        var probe = Healthy() with
        {
            ProviderKeyPresent = false,
            WorkspaceWritable = false,
            SessionDirWritable = false,
            ShellName = "",
            GitAvailable = false,
            GitBranch = null,
            CodeAgentIgnored = false,
            CustomCommandProblems = ["坏模板"],
            StatusLineState = StatusLineStates.Empty,
        };
        foreach (var check in Doctor.Run(config, probe).Where(c => c.Level != DoctorLevel.Ok))
            Assert.False(string.IsNullOrWhiteSpace(check.Advice), $"{check.Name} 缺建议");
    }

    [Fact]
    public void FormatReportShowsCountsAndAdvice()
    {
        var config = new AgentConfig { OutputStyle = "nope" };
        var lines = Doctor.FormatReport(Doctor.Run(config, Healthy()), 120);
        Assert.Contains(lines, l => l.Contains("自检"));
        Assert.Contains(lines, l => l.Contains("失败"));
        Assert.Contains(lines, l => l.Contains("建议"));
    }

    [Fact]
    public void CanWriteInDetectsAPathUnderAFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ca_doctor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(Program.CanWriteIn(dir));
            var file = Path.Combine(dir, "a-file");
            File.WriteAllText(file, "x");
            Assert.False(Program.CanWriteIn(Path.Combine(file, "sub")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    private static DoctorCheck Check(IReadOnlyList<DoctorCheck> checks, string name) =>
        checks.Single(c => c.Name == name);
}
