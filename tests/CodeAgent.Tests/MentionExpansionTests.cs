using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 提示词里的 @路径 引用**真的被读进上下文**。
///
/// 上一轮只把 @ 做成了输入行的补全，模型拿到的仍然只是"@src/foo.cs"这几个字符——
/// 特性看着能用，实际什么也没发生。半成品的特性比没有更糟：用户以为文件进去了。
/// </summary>
public sealed class MentionExpansionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-expand-" + Guid.NewGuid().ToString("N"));

    public MentionExpansionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteFile(string rel, string content)
    {
        var full = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // —— 解析 ——

    [Fact]
    public void Parse_FindsPathAfterSpace()
    {
        var paths = AgentClass.ParseMentionPaths("看下 @src/a.cs 这个文件");
        Assert.Equal(new[] { "src/a.cs" }, paths);
    }

    [Fact]
    public void Parse_FindsAtLineStart()
    {
        Assert.Equal(new[] { "a.cs" }, AgentClass.ParseMentionPaths("@a.cs 解释一下"));
    }

    [Fact]
    public void Parse_IgnoresEmailLikeText()
    {
        // git@github.com 里的 @ 前面不是空白，不能解析
        Assert.Empty(AgentClass.ParseMentionPaths("clone git@github.com:me/repo.git"));
    }

    [Fact]
    public void Parse_IgnoresBareAt()
    {
        Assert.Empty(AgentClass.ParseMentionPaths("email me @ 谢谢"));
        Assert.Empty(AgentClass.ParseMentionPaths(""));
    }

    [Fact]
    public void Parse_Deduplicates()
    {
        var paths = AgentClass.ParseMentionPaths("@a.cs 和 @a.cs 还有 @b.cs");
        Assert.Equal(new[] { "a.cs", "b.cs" }, paths);
    }

    [Fact]
    public void Parse_FindsMultiple()
    {
        var paths = AgentClass.ParseMentionPaths("对比 @a.cs 与 @b/c.cs");
        Assert.Equal(new[] { "a.cs", "b/c.cs" }, paths);
    }

    [Fact]
    public void Parse_HandlesNewlineSeparated()
    {
        var paths = AgentClass.ParseMentionPaths("第一行\n@a.cs");
        Assert.Equal(new[] { "a.cs" }, paths);
    }

    [Fact]
    public void Parse_StopsPathAtWhitespace()
    {
        // @a.cs b.cs —— 只有 a.cs 是引用
        Assert.Equal(new[] { "a.cs" }, AgentClass.ParseMentionPaths("@a.cs b.cs"));
    }

    // —— 展开 ——

    [Fact]
    public async Task Expand_AppendsFileContents()
    {
        WriteFile("a.cs", "class A { }");
        var text = await AgentClass.ExpandMentions(new Workspace(_dir), 20000, "看下 @a.cs", CancellationToken.None);
        Assert.Contains("看下 @a.cs", text); // 原文保留
        Assert.Contains("class A { }", text);
        Assert.Contains("a.cs", text);
    }

    [Fact]
    public async Task Expand_NoMentionReturnsPromptUnchanged()
    {
        var prompt = "普通提示词，没有引用";
        Assert.Equal(prompt, await AgentClass.ExpandMentions(new Workspace(_dir), 20000, prompt, CancellationToken.None));
    }

    [Fact]
    public async Task Expand_MissingFileIsReportedNotSwallowed()
    {
        // 静默吞掉最糟：用户以为文件进去了，模型据此编一个"文件里没有"的内容
        var text = await AgentClass.ExpandMentions(new Workspace(_dir), 20000, "@missing.cs 解释", CancellationToken.None);
        Assert.Contains("未找到文件", text);
        Assert.Contains("missing.cs", text);
    }

    [Fact]
    public async Task Expand_BinaryFileIsReported()
    {
        var full = Path.Combine(_dir, "bin.dat");
        File.WriteAllBytes(full, new byte[] { 1, 0, 2, 0, 3 });
        var text = await AgentClass.ExpandMentions(new Workspace(_dir), 20000, "@bin.dat", CancellationToken.None);
        Assert.Contains("二进制文件", text);
    }

    [Fact]
    public async Task Expand_LargeFileIsTruncatedWithAVisibleNote()
    {
        // 半个文件比没有文件更糟：模型会以为看全了
        WriteFile("big.txt", new string('x', 60_000));
        var text = await AgentClass.ExpandMentions(new Workspace(_dir), 20000, "@big.txt", CancellationToken.None);
        Assert.Contains("已截断", text);
        Assert.Contains("60000", text);
    }

    [Fact]
    public async Task Expand_MultipleFilesAllIncluded()
    {
        WriteFile("a.cs", "AAA");
        WriteFile("b.cs", "BBB");
        var text = await AgentClass.ExpandMentions(new Workspace(_dir), 20000, "@a.cs @b.cs", CancellationToken.None);
        Assert.Contains("AAA", text);
        Assert.Contains("BBB", text);
    }

    [Fact]
    public async Task Expand_RespectsCancellation()
    {
        WriteFile("a.cs", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AgentClass.ExpandMentions(new Workspace(_dir), 20000, "@a.cs", cts.Token));
    }

    [Fact]
    public async Task Expand_ExpandsMultipleMentionsInOnePrompt()
    {
        WriteFile("a.cs", "AAA");
        var text = await AgentClass.ExpandMentions(new Workspace(_dir), 20000, "@a.cs 和 @a.cs 都要", CancellationToken.None);
        // 重复引用只展开一次（浪费上下文且让模型困惑）
        Assert.Equal(1, CountOccurrences(text, "AAA"));
    }

    [Fact]
    public void Parse_IsUsedByExpansion()
    {
        // 接线守卫：只测解析器会漏掉「RunAsync 里真的调用了它」
        var src = File.ReadAllText(FindAgent());
        Assert.Contains("ExpandMentions(", src);
        Assert.Contains("ParseMentionPaths(", src);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string FindAgent()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent");
                var f = Path.Combine(candidate, "Agent", "Agent.cs");
                if (Directory.Exists(candidate) && File.Exists(f) && new FileInfo(f).Length > 1000)
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
