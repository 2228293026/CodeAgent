using System;
using System.IO;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 配置保存只有一个入口。
///
/// <c>SaveConfig</c> 保存前会把 <c>config.Provider</c> 换回 <c>PersistedProvider</c>：
/// <c>/model</c>、<c>/thinking</c>、<c>/shell</c>、<c>/access</c> 的保存
/// <b>不能</b>把 <c>CODEAGENT_PROVIDER</c> / <c>-p</c> 这类**会话级覆盖**
/// 固化成配置文件的默认 provider（那是启动参数，不是用户选择的默认值）。
///
/// 只要有一处绕过它直接调 <c>AgentConfig.Save</c>，这个保护就形同虚设——
/// 而且那种地方看着完全正常，要等某天在 CI 里真的把覆盖写进了配置才发现。
/// </summary>
public sealed class ConfigSaveChokePointTests
{
    [Fact]
    public void ProgramNeverCallsAgentConfigSaveDirectly()
    {
        var src = File.ReadAllText(FindSource("Program.cs"));
        // 只允许出现在 SaveConfig 的实现内部（那一行是"真正的保存"）
        var occurrences = Occurrences(src, "AgentConfig.Save(");
        Assert.True(occurrences <= 1,
            $"Program.cs 里有 {occurrences} 处 AgentConfig.Save(，除 SaveConfig 内部那一处外都必须走 SaveConfig");
    }

    [Fact]
    public void ProgramHasSeveralSaveSites()
    {
        // 反向断言：确认这条守卫不是"因为根本没有保存点"而空转
        var src = File.ReadAllText(FindSource("Program.cs"));
        Assert.True(Occurrences(src, "SaveConfig(config,") >= 4, "保存点应有若干处，守卫才有意义");
    }

    [Fact]
    public void SaveConfigRestoresThePersistedProvider()
    {
        var config = new AgentConfig { Provider = "session-only", PersistedProvider = "persisted" };
        var path = Path.Combine(Path.GetTempPath(), "codeagent-cp-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Program.SaveConfig(config, path);
            var saved = File.ReadAllText(path);
            Assert.Contains("persisted", saved);
            Assert.DoesNotContain("session-only", saved);
            // 保存完内存里的值必须还原，否则后续 /thinking 之类会拿错 provider
            Assert.Equal("session-only", config.Provider);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void SaveConfigRestoresTheProviderEvenWhenSavingThrows()
    {
        var config = new AgentConfig { Provider = "session-only", PersistedProvider = "persisted" };
        // 路径是一个**目录**：保存必然抛异常，此时 Provider 也必须已经换回
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codeagent-cp-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Assert.ThrowsAny<Exception>(() => Program.SaveConfig(config, dir));
            Assert.Equal("session-only", config.Provider);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ExplicitProviderSwitchUpdatesPersistedProviderBeforeSaving()
    {
        // /provider 是**显式**切换：用户就是要它持久化，所以要先更新 PersistedProvider。
        // 这个顺序依赖就是上一条"别直接调 AgentConfig.Save"的理由。
        var src = File.ReadAllText(FindSource("Program.cs"));
        var setAt = src.IndexOf("config.PersistedProvider = hit.Key", StringComparison.Ordinal);
        Assert.True(setAt > 0, "应先设置 PersistedProvider");
        // 3 参重载：(值, 起始位置, 比较方式) —— 从 setAt 之后开始找那一处保存
        var saveAt = src.IndexOf("SaveConfig(config, savePath);", setAt, StringComparison.Ordinal);
        Assert.True(saveAt > setAt, "PersistedProvider 必须在保存之前更新");
    }

    [Fact]
    public void WizardStillSavesDirectlyOnPurpose()
    {
        // 向导是**显式重新配置**（用户在那里挑 provider），就该直接保存——
        // 它的语义与 SaveConfig 不同，不该被这条守卫误伤。
        var wizard = File.ReadAllText(FindSource("SetupWizard.cs"));
        Assert.Contains("AgentConfig.Save(", wizard);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string FindSource(string fileName)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", fileName);
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/{fileName}");
    }
}
