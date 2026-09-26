using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 「某设置已保存」确认行的窄屏降级。
/// 回归点：`已切换模型: openai/gpt-4.1-2025-04-14，已保存到 C:\Users\…\config.json`
/// 此前整行硬截尾部，模型名一长就**连值本身**被切掉——用户看不到自己切到了什么。
/// 降级顺序：先丢保存从句 → 再按尾部保留缩短路径；值优先完整。
/// 另一处「命令 shell 已设为」此前**根本没传宽度**，在窄终端上必然溢出。
/// </summary>
public class SettingSavedLineTests
{
    private const string Model = "openai/gpt-4.1-2025-04-14";
    private const string Path = "C:/Users/me/.codeagent/config.json";

    [Fact]
    public void WideLineKeepsEverything()
    {
        var line = Program.FormatSettingSavedLine("已切换模型", Model, Path, 100);
        Assert.Equal($"已切换模型: {Model}，已保存到 {Path}", line);
    }

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        var line = Program.FormatSettingSavedLine("已切换模型", Model, Path, 0);
        Assert.Equal($"已切换模型: {Model}，已保存到 {Path}", line);
    }

    [Fact]
    public void ModelNameIsKeptWhole()
    {
        // 核心不变式：值优先完整
        foreach (var width in new[] { 40, 50, 60 })
        {
            var line = Program.FormatSettingSavedLine("已切换模型", Model, Path, width);
            Assert.Contains(Model, line);
        }
    }

    [Fact]
    public void SaveClauseDropsBeforeTheValue()
    {
        // 「已切换模型: openai/gpt-4.1-2025-04-14」= 42 列；更窄时先丢保存从句
        var line = Program.FormatSettingSavedLine("已切换模型", Model, Path, 40);
        Assert.Equal($"已切换模型: {Model}", line);
    }

    [Fact]
    public void PathIsShortenedByTail()
    {
        var line = Program.FormatSettingSavedLine("已切换模型", "gpt", Path, 40);
        Assert.Contains("已保存到", line);
        Assert.Contains("config.json", line);
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 6, 10, 16, 20, 30, 40, 60, 100 })
        {
            foreach (var value in new[] { Model, "gpt", "", new string('v', 200) })
            {
                var line = Program.FormatSettingSavedLine("已切换模型", value, Path, width);
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
            }
        }
    }

    [Fact]
    public void FitsInsideTheConfirmLineFrame()
    {
        foreach (var width in new[] { 20, 40, 60, 100 })
        {
            var body = Program.FormatSettingSavedLine("已切换模型", Model, Path, width - 3);
            var line = Program.FormatConfirmLine(body, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }

    [Fact]
    public void CjkActionIsMeasuredByDisplayWidth()
    {
        var line = Program.FormatSettingSavedLine("已切换推理强度", "高", Path, 30);
        Assert.True(TextUtil.DisplayWidth(line) <= 30, line);
    }

    [Fact]
    public void MinimumSavedPathWidthIsSane()
    {
        Assert.InRange(Program.MinSavedPathWidth, 4, 12);
    }
}
