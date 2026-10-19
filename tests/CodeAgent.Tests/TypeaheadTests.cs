using System;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 回合进行中提前敲入的内容（typeahead）不能丢。
///
/// 回合内的按键监听为了抓 ESC 会消费所有按键。此前一律 `ReadKey(intercept: true)`
/// 然后扔掉——注释里写着「typeahead 会被吞」，等于承认了这个缺陷。
/// 模型跑一轮常要几十秒，这段时间用户很自然会先把下一句敲进去；
/// 敲完一整句、回车，屏幕上**一个字都没出现过**，只能重打。
/// </summary>
public class TypeaheadTests
{
    [Fact]
    public void EmptyBufferLeavesTheDraftAlone()
    {
        // 没敲任何东西时必须原样返回，包括 null——
        // 把 null 变成 "" 会让输入行以为「草稿是空的」，进而丢掉真正的回填
        Assert.Null(Program.MergeTypeahead(null));
        Assert.Equal("已有草稿", Program.MergeTypeahead("已有草稿"));
    }

    [Fact]
    public void TypedTextBecomesTheDraft()
    {
        Program.AppendTypeaheadForTest("提前想好的下一句");
        Assert.Equal("提前想好的下一句", Program.MergeTypeahead(null));
    }

    [Fact]
    public void TypedTextIsConsumedOnlyOnce()
    {
        // 取走之后必须清空：否则同一句话会被回填进每一轮
        Program.AppendTypeaheadForTest("一次性");
        Assert.Equal("一次性", Program.MergeTypeahead(null));
        Assert.Null(Program.MergeTypeahead(null));
    }

    [Fact]
    public void ExistingDraftAndTypedTextAreSeparatedByANewline()
    {
        // 先打了半句、回合中又补了半句：直接首尾相连会粘成一个词
        Program.AppendTypeaheadForTest("后半句");
        Assert.Equal("前半句\n后半句", Program.MergeTypeahead("前半句"));
    }

    [Fact]
    public void TrailingNewlineFromTheEnterKeyIsNotKept()
    {
        // 用户敲回车表示「我说完了」，那个换行不该变成草稿里的空行
        Program.AppendTypeaheadForTest("问题\n");
        Assert.Equal("问题", Program.MergeTypeahead(null));
    }

    [Fact]
    public void OnlyWhitespaceTypedIsNotADraft()
    {
        // 只敲了个回车（比如误触）：不该回填出一段空白
        Program.AppendTypeaheadForTest("\n");
        Assert.Null(Program.MergeTypeahead(null));
    }
}
