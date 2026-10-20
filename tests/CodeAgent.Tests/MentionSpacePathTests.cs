using System;
using System.IO;
using System.Linq;
using System.Threading;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>@</c> 文件引用的**自相矛盾**：菜单会把含空格的文件列出来，
/// 但选中之后路径在空格处断开。
///
/// 两套规则各写一份：
/// · <c>MentionItems</c>（列菜单）不过滤空格 → 文件会出现（如 <c>My Docs/a.txt</c>）
/// · <c>ParseMention</c> / <c>ParseMentionPaths</c> 遇空格即止 → 只认到 <c>@My</c>
///
/// 于是用户从菜单里挑了一个文件，输入行里得到 <c>@My</c>，
/// 发送时展开成「未找到文件: My」。他没有任何办法知道问题出在文件名里的空格。
///
/// 修法不是让解析器支持空格（那会把 <c>@张三 你好</c> 当成路径 <c>张三 你好</c>），
/// 而是在**列菜单时就不列**——从源头上不给出一个选不中的选项。
/// </summary>
public class MentionSpacePathTests
{
    [Fact]
    public void PathsWithSpacesAreNotOffered()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ca-mention-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "My Docs"));
        File.WriteAllText(Path.Combine(dir, "My Docs", "a.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "normal.txt"), "y");
        try
        {
            var items = InputLine.MentionItems(dir, "", 200, CancellationToken.None, out _);
            var names = items.Select(i => i.Name).ToList();
            Assert.Contains("normal.txt", names);
            // 核心断言：选不中的东西不该列出来
            Assert.DoesNotContain(names, n => n.Contains(' '));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ASpacePathNeverReachesTheExpanderAsAFullPath()
    {
        // 解析器在空格处断开，只得到 "My"。
        // 而 "My" 不像路径（没斜杠没扩展名），被 LooksLikePath 拦掉——
        // 于是**什么都不会展开**，比报一句「未找到文件: My」干净。
        //
        // 这条断言原本写的 `Assert.Contains("My", ...)` 是我猜的，实际不成立：
        // LooksLikePath 那一层在此之上又筛了一遍。真实行为更好，把预期改对。
        var expanded = AgentClass.ParseMentionPaths("看这个 @My Docs/a.txt");
        Assert.DoesNotContain("My Docs/a.txt", expanded);
        Assert.DoesNotContain("My", expanded); // 不像路径，直接忽略而不是报错
    }

    [Fact]
    public void NormalPathsAreUnaffected()
    {
        // 不能为了这一个场景把正常路径也砍掉
        var dir = Path.Combine(Path.GetTempPath(), "ca-mention2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "a.cs"), "x");
        try
        {
            var items = InputLine.MentionItems(dir, "src/", 200, CancellationToken.None, out _);
            Assert.Contains("src/a.cs", items.Select(i => i.Name));
            Assert.Contains("src/a.cs", AgentClass.ParseMentionPaths("看 @src/a.cs"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static class AgentClass
    {
        internal static System.Collections.Generic.List<string> ParseMentionPaths(string p) =>
            CodeAgent.Agent.Agent.ParseMentionPaths(p);
    }
}
