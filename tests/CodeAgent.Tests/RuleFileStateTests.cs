using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 规则文件的**单一事实源**：一次探测同时决定「哪份文件」「读到了什么」「横幅怎么报」。
///
/// 上一轮修的是「内容从来没被读过」；这一轮修的是另一半同一个毛病——
/// 三处各自判一次「文件存在吗」，于是空文件、读不动的文件（权限、被占用）
/// 也被报成「已加载 AGENTS.md」。用户以为规则生效了，模型一个字都没看到，
/// 最后只能怪模型不听话。文件存在 ≠ 规则生效。
/// </summary>
public class RuleFileStateTests : IDisposable
{
    private readonly string _dir;

    public RuleFileStateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "codeagent-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MissingDirectoryYieldsMissingState()
    {
        var state = Program.LoadRuleFileState(_dir);
        Assert.Equal(Program.RuleFileStatus.Missing, state.Status);
        Assert.Null(state.Path);
        Assert.Null(state.Content);
        Assert.False(state.Inject);
        Assert.Null(Program.BuildRuleFileNotice(state));
    }

    [Fact]
    public void LoadedFileCarriesPathContentAndName()
    {
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "提交前必须跑 dotnet test。");
        var state = Program.LoadRuleFileState(_dir);
        Assert.Equal(Program.RuleFileStatus.Loaded, state.Status);
        Assert.Equal("AGENTS.md", state.FileName);
        Assert.Contains("提交前必须跑 dotnet test", state.Content);
        Assert.True(state.Inject);
    }

    [Fact]
    public void EmptyFileIsReportedAsEmptyNotLoaded()
    {
        // 关键回归：空白文件此前被报成「已加载」。
        // 说「已加载」而提示里没有任何规则，是最坏的一种谎。
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "  \n\n   \t\n");
        var state = Program.LoadRuleFileState(_dir);
        Assert.Equal(Program.RuleFileStatus.Empty, state.Status);
        Assert.Equal("AGENTS.md", state.FileName);
        Assert.False(state.Inject);
        Assert.Null(state.Content);

        var notice = Program.BuildRuleFileNotice(state);
        Assert.NotNull(notice);
        Assert.Contains("空文件", notice);
        Assert.DoesNotContain("已加载", notice);
    }

    [Fact]
    public void UnreadableFileIsReportedAsFailureNotLoaded()
    {
        // 同名的**目录**骗过一切「文件存在吗」的判断，读取时必然失败。
        // 用一个真实可复现的读取错误来验证：失败必须说出来，不能报成已加载。
        Directory.CreateDirectory(Path.Combine(_dir, "AGENTS.md"));
        var state = Program.LoadRuleFileState(_dir);
        Assert.Equal(Program.RuleFileStatus.Unreadable, state.Status);
        Assert.False(state.Inject);

        var notice = Program.BuildRuleFileNotice(state);
        Assert.NotNull(notice);
        Assert.Contains("读取失败", notice);
        Assert.Contains("未生效", notice);
        Assert.DoesNotContain("已加载", notice);
    }

    [Fact]
    public void LoadedNoticeNamesTheWinningFile()
    {
        // 三个候选都在时，横幅必须报出**实际胜出**的那一份：
        // 用户改了 CLAUDE.md 却没生效时，这行字是唯一的线索。
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "a");
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "c");
        var state = Program.LoadRuleFileState(_dir);
        var notice = Program.BuildRuleFileNotice(state);
        Assert.NotNull(notice);
        Assert.Contains("已加载 AGENTS.md", notice);
        Assert.Contains(Path.Combine(_dir, "AGENTS.md"), notice);
    }

    [Fact]
    public void StartupStateDefaultsToMissingSoBannerStaysSilent()
    {
        // 静态字段的初值必须是无事发生：测试进程里没人跑启动流程，
        // 横幅不该凭空报出一份宿主仓库的 AGENTS.md。
        Assert.Equal(Program.RuleFileStatus.Missing, Program.RuleFileState.Missing.Status);
        Assert.False(Program.RuleFileState.Missing.Inject);
        Assert.Null(Program.RuleFileState.Missing.FileName);
        Assert.Null(Program.BuildRuleFileNotice(Program.RuleFileState.Missing));
    }

    [Fact]
    public void TruncatedContentStillCountsAsLoaded()
    {
        // 截断是「有损但生效」：规则确实进了提示，只是头尾保留。
        // 把它报成失败会让用户白白去查权限问题。
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), new string('x', Program.MaxRuleFileChars + 1_000));
        var state = Program.LoadRuleFileState(_dir);
        Assert.Equal(Program.RuleFileStatus.Loaded, state.Status);
        Assert.True(state.Inject);
        Assert.Contains("已保留头尾", state.Content);
    }
}
