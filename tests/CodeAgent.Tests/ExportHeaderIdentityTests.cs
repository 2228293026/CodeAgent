using System;
using System.IO;
using System.Linq;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 导出 Markdown 的元信息头。
///
/// 此前头部写的是 <c>config.Provider</c> 与 <c>config.Providers[x].Model</c>——
/// 那是**配置里写的**值。会话级覆盖（<c>-m</c>、<c>-p</c>、环境变量、<c>/model</c>）
/// 不写回配置，于是几个月后回看这份归档，模型信息是错的。
/// 实测：会话跑的是 <c>kilo/stealth/space-bunny-alpha</c>，导出头写着 <c>custom / gpt5</c>。
/// </summary>
public sealed class ExportHeaderIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "codeagent-export-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响断言 */ }
        GC.SuppressFinalize(this);
    }

    private AgentClass NewAgent()
    {
        var config = new AgentConfig
        {
            Provider = "custom",
            Providers = new System.Collections.Generic.Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["custom"] = new ProviderOptions { Model = "gpt5" },
            },
            ExportDir = _dir,
        };
        return new AgentClass(config, new FakeProvider(), ToolRegistry.CreateDefault(), _dir);
    }

    private string ExportHeader(AgentClass agent)
    {
        var file = agent.ExportSessionLogMarkdown(WriteLog());
        return File.ReadAllText(file);
    }

    private string WriteLog()
    {
        var p = Path.Combine(_dir, "s.jsonl");
        File.WriteAllLines(p, ["""{"role":"user","content":"hi"}"""]);
        return p;
    }

    [Fact]
    public void WithoutOverride_HeaderUsesTheConfiguredModel()
    {
        var agent = NewAgent();
        var md = ExportHeader(agent);
        Assert.Contains("gpt5", md);
    }

    [Fact]
    public void WithOverride_HeaderUsesTheEffectiveModel()
    {
        // 核心回归：会话级覆盖不写回配置，但归档要记**实际跑的那个**
        var agent = NewAgent();
        agent.SetEffectiveIdentity("custom", "kilo/stealth/space-bunny-alpha");
        var md = ExportHeader(agent);
        Assert.Contains("kilo/stealth/space-bunny-alpha", md);
        Assert.DoesNotContain("gpt5", md);
    }

    [Fact]
    public void WithOverride_HeaderUsesTheEffectiveProvider()
    {
        var agent = NewAgent();
        agent.SetEffectiveIdentity("openai", "some-model");
        var md = ExportHeader(agent);
        Assert.Contains("openai", md);
    }

    [Fact]
    public void MidSessionModelSwitchIsReflected()
    {
        // 刷新语义：构造时记一次还不够，/model 切换后要记切换后的那个
        var agent = NewAgent();
        agent.SetEffectiveIdentity("custom", "model-before-switch");
        var first = ExportHeader(agent);
        agent.SetEffectiveIdentity("custom", "model-after-switch");
        var second = ExportHeader(agent);
        Assert.Contains("model-before-switch", first);
        Assert.Contains("model-after-switch", second);
        Assert.DoesNotContain("model-before-switch", second);
    }

    [Fact]
    public void BlankOverrideFallsBackToConfig()
    {
        var agent = NewAgent();
        agent.SetEffectiveIdentity("  ", "");
        var md = ExportHeader(agent);
        Assert.Contains("gpt5", md);
    }

    [Fact]
    public void NoOverrideAtAll_StillUsesConfig()
    {
        // 从不调用 SetEffectiveIdentity 的路径（如 --resume 直出的旧日志）不能崩
        var agent = NewAgent();
        Assert.Null(agent.EffectiveModelName);
        Assert.Contains("gpt5", ExportHeader(agent));
    }

    [Fact]
    public void HeaderStillCarriesTheOtherProvenanceFields()
    {
        var md = ExportHeader(NewAgent());
        Assert.Contains("导出时间", md);
        Assert.Contains("消息数", md);
    }

    [Fact]
    public void EveryExportSiteRefreshesTheIdentity()
    {
        // 构造处刷新一次 + 两个导出点各刷新一次：少了哪一处，
        // 那个入口（/export 当前对话 /export 编号 /export all）就会写回旧模型
        var src = File.ReadAllText(Find());
        Assert.True(Occurrences(src, "agent.SetEffectiveIdentity(config.Provider, opts.Model)") >= 3,
            "构造处 + 两个导出点都应刷新");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
