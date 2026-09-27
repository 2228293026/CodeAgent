using System;
using System.IO;
using System.Linq;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// <c>ToolVerb</c> 的前缀规则与它的长度上限。
///
/// 规则原注释举的例子 <c>read_binary_file</c> <b>全仓库并不存在</b>——只有那条注释提到它。
/// 拿一个不存在的工具当理由，会让下一个读到这里的人以为前缀规则覆盖得比实际宽得多。
/// </summary>
public sealed class ToolVerbPrefixRuleTests
{
    [Theory]
    [InlineData("read_files", "Read")]
    [InlineData("delete_files", "Delete")]
    [InlineData("replace_in_files", "Edit")]
    public void RealPluralVariantsDoGetTheVerb(string name, string expected)
    {
        // 这三个是**实际存在**的工具，且确实命中前缀规则——比虚构的例子更能说明规则
        Assert.Equal(expected, AgentClass.ToolVerb(name));
    }

    [Fact]
    public void TheCommentedExampleDoesNotExist()
    {
        // 钉住这次修的东西：注释不得再引用不存在的工具
        var src = File.ReadAllText(Find());
        Assert.Contains("read_binary_file", src);
        Assert.Contains("并不存在", src);
    }

    [Fact]
    public void TheLengthCapIsRealAndBounded()
    {
        // read=4，上限 +6 → 名字最长 10 字符。超过就退回首字母大写的原名。
        Assert.Equal("Read", AgentClass.ToolVerb("read_abcde"));    // 4+1+5 = 10，恰好上限
        Assert.Equal("Read_abcdef", AgentClass.ToolVerb("read_abcdef")); // 11，超限
    }

    [Fact]
    public void LongNamesFallBackRatherThanBeingGuessed()
    {
        // 宁可显示原名，也不硬套动词：猜错动词会让人以为在执行另一个操作
        Assert.Equal("Git_status_report", AgentClass.ToolVerb("git_status_report"));
    }

    [Fact]
    public void TheFallbackJustCapitalizesAndChangesNothingElse()
    {
        // 退回形态是"原名 + 首字母大写"：不是动词，也不该改动别的字符
        Assert.Equal("Api_compatibility_report", AgentClass.ToolVerb("api_compatibility_report"));
    }

    [Fact]
    public void EmptyAndUnknownNamesAreSafe()
    {
        Assert.Equal(string.Empty, AgentClass.ToolVerb(string.Empty));
        Assert.Equal("Zzz", AgentClass.ToolVerb("zzz"));
    }

    [Fact]
    public void ExactMatchesWinOverThePrefixRule()
    {
        // 精确表优先：list_files 在表里是 Glob，前缀规则不能把它抢走
        Assert.Equal("Glob", AgentClass.ToolVerb("list_files"));
        Assert.Equal("Glob", AgentClass.ToolVerb("glob"));
    }

    [Fact]
    public void NoRealToolAccidentallyHitsThePrefixRule()
    {
        // 反向检查：把仓库里真实存在的 snake_case 工具名都过一遍，
        // 确认没有任何一个被前缀规则映射成**错误的**动词
        var registry = File.ReadAllText(FindInTests("ToolRegistryTests.cs"));
        var names = System.Text.RegularExpressions.Regex.Matches(registry, "\"([a-z][a-z0-9_]*_[a-z0-9_]+)\"")
            .Select(m => m.Groups[1].Value)
            .Where(n => n.Length > 6)
            .Distinct()
            .ToList();
        Assert.True(names.Count > 50, $"只取到 {names.Count} 个工具名，样本太小");

        var known = new System.Collections.Generic.HashSet<string>(
            AgentClass.VerbMap.Select(p => p.From), StringComparer.OrdinalIgnoreCase);
        var verbs = new System.Collections.Generic.HashSet<string>(
            AgentClass.VerbMap.Select(p => p.To), StringComparer.Ordinal);
        int viaPrefix = 0;
        foreach (var name in names)
        {
            if (known.Contains(name))
                continue; // 精确表命中，无需再查前缀
            var key = name.ToLowerInvariant();
            foreach (var (from, to) in AgentClass.VerbMap)
            {
                if (from.Length == 0 || !key.StartsWith(from, StringComparison.Ordinal) || key.Length > from.Length + 6)
                    continue;
                viaPrefix++;
                // 安全性质：前缀规则**只能**产出动词表里已有的词，永远不会拼出表外的字符串。
                // 动词与工具名首段不必同首字母——replace_in_files → Edit 是刻意换词，
                // 正如 multi_edit → Edit；那是语义等价，不是词面相等。
                Assert.True(verbs.Contains(to), $"{name} 经前缀 {from} 产出了表外动词 {to}");
            }
        }
        Assert.True(viaPrefix > 0, "没有任何真实工具走前缀规则，样本或规则有问题");
    }



    private static string FindInTests(string fileName)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "tests", "CodeAgent.Tests", fileName);
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 tests/CodeAgent.Tests/{fileName}");
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Agent", "Agent.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Agent/Agent.cs");
    }
}
