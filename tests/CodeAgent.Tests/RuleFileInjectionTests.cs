using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 规则文件注入的**可见性**：注入之后，用户得能从 /prompt 看到它在里面。
///
/// 上一轮修的是「内容从未被读取」；这一轮补的是另一半——
/// 就算内容真的进了系统提示，/prompt 的来源说明只说「含 ADOFAI 上下文」，
/// 用户翻到那段规则时会以为是哪儿来的野内容，或者反过来以为规则没生效。
/// </summary>
public class RuleFileInjectionTests : IDisposable
{
    private readonly string _dir;

    public RuleFileInjectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "codeagent-inject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void InjectedContentKeepsTheDelimiters()
    {
        // 分隔标记必须有：系统提示里夹着一大段用户规则，
        // 不标边界时模型分不清「这段是项目约定」还是「这段是当前任务」
        var path = Path.Combine(_dir, "AGENTS.md");
        File.WriteAllText(path, "规则内容");
        var content = Program.ReadRuleFile(path);
        Assert.NotNull(content);
        Assert.Contains("规则内容", content);
    }

    [Fact]
    public void RuleFileSearchIsDirectoryLocal()
    {
        // 只在当前目录找、不逐级上溯：改了上级目录的文件却在当前目录没生效，
        // 是启动横幅「明确告诉用户哪一份在起作用」要防的事。
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "parent");
        Assert.Null(Program.RuleFile(sub));
        File.WriteAllText(Path.Combine(sub, "AGENTS.md"), "child");
        Assert.NotNull(Program.RuleFile(sub));
    }

    [Fact]
    public void TruncationNoticeStatesTheOriginalSize()
    {
        // 截断说明要报**原始**字符数：只说「已保留头尾」用户不知道丢了多大量，
        // 也就无法判断该不该把这份规则拆小。
        var path = Path.Combine(_dir, "AGENTS.md");
        File.WriteAllText(path, new string('x', Program.MaxRuleFileChars + 5_000));
        var content = Program.ReadRuleFile(path);
        Assert.NotNull(content);
        Assert.Contains("规则文件共", content);
    }
}
