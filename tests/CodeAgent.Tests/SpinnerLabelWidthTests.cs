using System;
using System.Linq;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// spinner 带 label 时的宽度预算。
///
/// <c>ShowSpinner(label)</c> 只在压缩历史时用，label 是「上下文超限，正在压缩历史…」（26 列）。
/// 原来是先按整个终端宽度裁剪 spinner 正文、**再把 label 拼到前面**：
/// 正文以为自己独占全部列数，一个字段都不丢，拼上 label 后整行溢出。
///
/// spinner 是 <c>\r</c> 原地重写的一行，一折行就会留下擦不掉的残字，
/// 输入块也会被撑成两行、之后每次重绘的光标定位都偏——这正是
/// <c>Agent.cs</c> 里那段「不能折行」注释说的后果。
/// </summary>
public class SpinnerLabelWidthTests
{
    [Fact]
    public void LabelIsSubtractedFromTheBudgetBeforeComposing()
    {
        var src = ReadAgent();
        // label 的显示宽度必须参与预算计算（innerWidth），
        // 而不是在 BuildSpinnerLine 之后才拼接
        Assert.Contains("innerWidth", src);
        Assert.Contains("columns - TextUtil.DisplayWidth(head)", src);
    }

    [Fact]
    public void FinalLineIsClampedToTheTerminalWidth()
    {
        // label 自己就比终端宽时，宁可截 label 也不能撑破行
        var src = ReadAgent();
        Assert.Contains("TextUtil.DisplayWidth(frame_text) > columns", src);
        Assert.Contains("TextUtil.TakeDisplayWidth(frame_text, columns)", src);
    }

    [Fact]
    public void BuildSpinnerLineStillDegradesWhenNarrow()
    {
        // 基本盘：正文自己也会按预算丢弃字段（排队回执 → 提示 → token → 用时）
        var narrow = AgentClass.BuildSpinnerLine("⠸", TimeSpan.FromSeconds(3), 1200, "ESC 中断", 12);
        Assert.True(narrow.Length > 0);
        Assert.DoesNotContain("已排队", narrow);
        // 极窄时退化成只有动画帧
        var tiny = AgentClass.BuildSpinnerLine("⠸", TimeSpan.FromSeconds(3), 1200, "ESC 中断", 2);
        Assert.Equal("⠸", tiny);
    }

    [Fact]
    public void ZeroWidthMeansUnknownAndKeepsEverything()
    {
        // 宽度未知（0）时不猜：宁可超宽也不要凭空少一段
        var text = AgentClass.BuildSpinnerLine("⠸", TimeSpan.FromSeconds(3), 5, "ESC 中断", 0, 7);
        Assert.Contains("已排队", text);
        Assert.Contains("ESC 中断", text);
    }

    private static string ReadAgent()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Agent", "Agent.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return System.IO.File.ReadAllText(f);
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 Agent.cs");
    }
}
