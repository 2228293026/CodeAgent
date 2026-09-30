using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/find</c> 的「0 命中」提示必须**是真的**。
///
/// 词边界规则（ASCII 关键字跳过"严格长在词中间"的命中）落地后，
/// 会出现一种此前不存在的情形：内容里**确实有**该字串，但全被规则滤掉。
/// 此时若还打「历史会话中没有匹配「X」的内容」——那句话是**假的**，
/// 用户会以为自己记错了，或者以为会话日志坏了。
///
/// 实测：搜 <c>lorTheme</c>，grep 在会话日志里找得到，/find 却回"没有匹配"。
/// </summary>
public sealed class FindNoMatchTruthTests
{
    [Fact]
    public void SubstringDetectorFindsAnInteriorOnlyOccurrence()
    {
        var hits = new List<(string, string)> { ("tool", "…ColorThemeTests.cs Enter…") };
        Assert.True(AgentClass.HasSubstringOccurrence(hits, "lorTheme"));
    }

    [Fact]
    public void SubstringDetectorIsCaseInsensitive()
    {
        var hits = new List<(string, string)> { ("tool", "…ColorThemeTests.cs…") };
        Assert.True(AgentClass.HasSubstringOccurrence(hits, "LORTHEME"));
    }

    [Fact]
    public void SubstringDetectorReturnsFalseWhenAbsent()
    {
        var hits = new List<(string, string)> { ("tool", "…nothing here…") };
        Assert.False(AgentClass.HasSubstringOccurrence(hits, "lorTheme"));
    }

    [Fact]
    public void SubstringDetectorHandlesEmptyInput()
    {
        Assert.False(AgentClass.HasSubstringOccurrence([], "x"));
        Assert.False(AgentClass.HasSubstringOccurrence(new List<(string, string)> { ("a", "b") }, null));
        Assert.False(AgentClass.HasSubstringOccurrence(new List<(string, string)> { ("a", "b") }, ""));
    }

    [Fact]
    public void LogContainsSubstringHandlesMissingFile()
    {
        Assert.False(AgentClass.LogContainsSubstring(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".jsonl"), "x"));
    }

    [Fact]
    public void LogContainsSubstringFindsARealOccurrence()
    {
        var path = Path.Combine(Path.GetTempPath(), "codeagent-sub-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, ["""{"role":"user","content":"看 ColorThemeTests.cs"}"""]);
        try
        {
            Assert.True(AgentClass.LogContainsSubstring(path, "lorTheme"));
            Assert.False(AgentClass.LogContainsSubstring(path, "zzz"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void TheTwoMessagesMustNotContradictThemselves()
    {
        // 「没有匹配「X」…但它只出现在…」既说没有又说有——这句话读起来是坏的
        var src = ReadSource(FindProgram());
        Assert.Contains("\"词边界：只找到「\"", src);
        Assert.Contains("\"历史会话中没有匹配「\"", src);
    }

    [Fact]
    public void TheInteriorBranchIsGatedOnTheWordBoundaryRule()
    {
        // 中文关键字不走词边界，那种情形根本不会出现，别白扫一遍磁盘
        var src = ReadSource(FindProgram());
        Assert.Contains("AgentClass.NeedsSearchWordBoundary(kw)", src);
        Assert.Contains("AgentClass.LogContainsSubstring(l, kw)", src);
    }

    private static string ReadSource(string p) => File.ReadAllText(p).Replace("\r\n", "\n");

    private static string FindProgram()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(d, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f))
                    return f;
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
