using System;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入行菜单的**表头与动作**必须与菜单里实际列的东西一致。
///
/// @ 文件引用（学自 Claude Code）接进菜单后，沿用了命令菜单的文案与行为：
///   · 表头写 "Commands … Enter run"，实际列的是文件、回车是插入；
///   · 数字键 1-9 直接把该项**当作整条提示提交**——
///     用户敲的"看下这个文件"被整段丢掉，换来一句裸路径。
/// 两者都是"提示与行为相反"，比没有提示更容易误导。
/// </summary>
public sealed class MenuHeaderTests
{
    [Fact]
    public void Header_CommandsModeSaysRun()
    {
        var h = InputLine.BuildMenuHeader(modePicker: false, mentionActive: false);
        Assert.Contains("Commands", h);
        Assert.Contains("run", h);
    }

    [Fact]
    public void Header_ModePickerKeepsItsOwnWording()
    {
        var h = InputLine.BuildMenuHeader(modePicker: true, mentionActive: false);
        Assert.Contains("Modes", h);
        Assert.DoesNotContain("Commands", h);
    }

    [Fact]
    public void Header_MentionModeSaysFilesNotCommands()
    {
        // 表头写 Commands 时，用户会以为回车会"执行"——实际是插入路径
        var h = InputLine.BuildMenuHeader(modePicker: false, mentionActive: true);
        Assert.Contains("Files", h);
        Assert.DoesNotContain("Commands", h);
    }

    [Fact]
    public void Header_MentionModeSaysInsertNotRun()
    {
        var h = InputLine.BuildMenuHeader(modePicker: false, mentionActive: true);
        Assert.Contains("insert", h);
        Assert.DoesNotContain("run", h);
    }

    [Fact]
    public void Header_AllThreeModesDiffer()
    {
        var a = InputLine.BuildMenuHeader(false, false);
        var b = InputLine.BuildMenuHeader(true, false);
        var c = InputLine.BuildMenuHeader(false, true);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void Header_AlwaysEndsWithTheCloseHint()
    {
        foreach (var (picker, mention) in new[] { (false, false), (true, false), (false, true) })
        {
            var h = InputLine.BuildMenuHeader(picker, mention);
            Assert.Contains(SafeColor.Glyphs.Escape, h);
            Assert.Contains("close", h);
        }
    }

    [Fact]
    public void Header_StaysReasonableAtEveryTerminalWidth()
    {
        foreach (var (picker, mention) in new[] { (false, false), (true, false), (false, true) })
        {
            var h = InputLine.BuildMenuHeader(picker, mention);
            // 表头本身不该长到离谱——它整块渲染，不参与按宽度截断
            Assert.True(TextUtil.DisplayWidth(h) < 100, $"表头过长: {h}");
        }
    }

    // —— 数字键动作 ——

    [Fact]
    public void Pick_CommandMenuSubmits()
    {
        Assert.Equal(InputLine.MenuPickAction.Submit, InputLine.PickAction(mentionActive: false));
    }

    [Fact]
    public void Pick_MentionMenuInsertsInsteadOfSubmitting()
    {
        // 提交 = 把裸路径当成整条提示发出去，用户敲的半句话被整段丢掉
        Assert.Equal(InputLine.MenuPickAction.Insert, InputLine.PickAction(mentionActive: true));
    }

    [Fact]
    public void Pick_InsertKeepsTheSurroundingPrompt()
    {
        // 插入路径后，用户已经敲的内容必须还在
        var text = InputLine.ApplyMention("看下这个 @a 谢谢", 5, "src/A.cs");
        Assert.Equal("看下这个 src/A.cs 谢谢", text);
    }

    [Fact]
    public void Header_MentionEnterSaysInsertNotSubmit()
    {
        // Enter 在 @ 菜单里是插入。写 "run"/"submit" 都是错的——
        // 用户会以为一按回车就把文件路径发出去了
        var h = InputLine.BuildMenuHeader(modePicker: false, mentionActive: true);
        Assert.Contains($"{SafeColor.Glyphs.Enter} insert", h);
        Assert.DoesNotContain("run", h);
        Assert.DoesNotContain("submit", h);
    }

    [Fact]
    public void EveryMenuEnterPathChecksThePickAction()
    {
        // 回归守卫：@ 菜单有四条"选中即生效"的路径（Enter / → / 数字键 / Tab 唯一匹配），
        // 每一条都必须先问 PickAction。修一条漏一条的话，总有一条把裸路径提交出去。
        var source = System.IO.File.ReadAllText(ProgramOrInputLine());
        var guards = CountOccurrences(source, "PickAction(mention.Item1 >= 0)");
        Assert.True(guards >= 4,
            $"只有 {guards} 处检查了 PickAction：Enter / → / 数字键 / Tab 四条路径至少要各一处（实际 {guards}）");
    }

    [Fact]
    public void NoPathSubmitsTheBareMention()
    {
        // 反向保护：提到 @ 的地方不该再有裸的 `mention.Item1 >= 0 ?` 三元判定，
        // 那说明某条路径绕过了 PickAction
        var source = System.IO.File.ReadAllText(ProgramOrInputLine());
        var bare = CountOccurrences(source, "mention.Item1 >= 0 ?");
        Assert.True(bare == 0, $"仍有 {bare} 处裸三元判定绕过了 PickAction");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string ProgramOrInputLine()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "InputLine.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/InputLine.cs");
    }
}
