using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 配置向导里的「渲染设置」一步。
///
/// 回归点：<c>CODEAGENT_THEME</c> / <c>CODEAGENT_ASCII</c> 是渲染外观唯一的开关，
/// 但它们**既没有设置入口、也不在向导里出现**——用户只能在 <c>/diag</c> 里看到
/// "没生效"这件事，却不知道去哪儿改。Round 98 解决了「看得见」，这一轮补上「改得了」。
///
/// 关键设计：**只打印可粘贴的命令，不代改**。向导写的是 codeagent.json，
/// 而这三项是环境变量——进程内改了对下一个终端没用，偷偷写进配置文件又会让
/// "为什么它没生效"更难查。
/// </summary>
public class SetupWizardRenderSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-wiz-" + Guid.NewGuid().ToString("N"));

    public SetupWizardRenderSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Offer(string answer)
    {
        var writer = new StringWriter();
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = name => name == "CODEAGENT_THEME" ? "contrast" : null;
            SetupWizard.OfferRenderSettings(new StringReader(answer + "\n"), writer);
        }
        finally { SafeColor.ReadEnv = saved; }
        return writer.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void ShowsTheCurrentStateBeforeAsking()
    {
        var output = Offer("1");
        // 先报现状，用户才知道自己是不是已经设过
        Assert.Contains("渲染设置", output);
        Assert.Contains("CODEAGENT_THEME: contrast", output);
        Assert.Contains("Theme: Contrast", output);
    }

    [Fact]
    public void OfferingContrastPrintsACopyPasteableCommand()
    {
        var output = Offer("2");
        Assert.Contains("$env:CODEAGENT_THEME=\"contrast\"", output);
        Assert.Contains("set CODEAGENT_THEME=contrast", output);
        // 明确告诉用户本次会话不会自动生效，免得以为按了没用
        Assert.Contains("本次会话不会自动改变", output);
    }

    [Fact]
    public void OfferingAsciiPrintsTheAsciiCommand()
    {
        var output = Offer("5");
        Assert.Contains("$env:CODEAGENT_ASCII=\"1\"", output);
        Assert.Contains("set CODEAGENT_ASCII=1", output);
    }

    [Fact]
    public void KeepingCurrentSettingsChangesNothing()
    {
        var output = Offer("1");
        Assert.Contains("已保持现有渲染设置", output);
        Assert.DoesNotContain("$env:", output);
    }

    [Fact]
    public void EnvCommandDiffersPerShell()
    {
        // PowerShell 与 cmd 的语法不同，给错一个用户粘进去就是语法错误
        Assert.Equal("$env:FOO=\"bar\"", SetupWizard.EnvCommand("powershell", "FOO", "bar"));
        Assert.Equal("set FOO=bar", SetupWizard.EnvCommand("cmd", "FOO", "bar"));
        Assert.Equal("set FOO=bar", SetupWizard.EnvCommand("CMD.EXE", "FOO", "bar"));
    }

    [Fact]
    public void EveryOptionIsReachableAndOrdered()
    {
        // 选项 2..5 各自都要能选中，且给出对应命令
        var expected = new (int Pick, string Key, string Value)[]
        {
            (2, "CODEAGENT_THEME", "contrast"),
            (3, "CODEAGENT_THEME", "contrast-light"),
            (4, "CODEAGENT_THEME", "light"),
            (5, "CODEAGENT_ASCII", "1"),
        };
        foreach (var (pick, key, value) in expected)
        {
            var output = Offer(pick.ToString());
            Assert.Contains($"set {key}={value}", output);
        }
    }

    [Fact]
    public void InvalidChoiceRepromptsInsteadOfGuessing()
    {
        var writer = new StringWriter();
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = _ => null;
            // 先给一个越界数字，再给合法值
            SetupWizard.OfferRenderSettings(new StringReader("9\n2\n"), writer);
        }
        finally { SafeColor.ReadEnv = saved; }
        var output = writer.ToString().Replace("\r\n", "\n");
        Assert.Contains("请输入 1-5 之间的数字", output);
        Assert.Contains("set CODEAGENT_THEME=contrast", output);
    }
}
