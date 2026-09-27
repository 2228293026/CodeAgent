using System;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 配置向导的选项行 / 字段行渲染。
///
/// 此前 <c>WriteLine($"  {i+1}) {label}")</c>：自定义供应商名长度不受控，折行之后
/// **编号与名称对不上**——而这个列表是靠编号选的（AskChoice 只收数字）。
/// </summary>
public sealed class SetupOptionLineTests
{
    [Fact]
    public void Option_ShowsNumberAndLabel()
    {
        var line = SetupWizard.FormatOptionLine(3, "DeepSeek", 80);
        Assert.Equal("  3) DeepSeek", line);
    }

    [Fact]
    public void Option_NumberPrefixIsNeverTruncated()
    {
        // 编号是**选择的依据**：AskChoice 只收数字，编号被截掉整个列表就废了
        var long1 = new string('x', 500);
        foreach (var w in new[] { 4, 6, 8, 10, 12, 20, 40, 80 })
        {
            var line = SetupWizard.FormatOptionLine(7, long1, w);
            Assert.Contains("7)", line);
            Assert.StartsWith("  7)", line);
        }
    }

    [Fact]
    public void Option_NeverExceedsWidth()
    {
        var long1 = string.Concat(Enumerable.Repeat("很长的供应商名称", 20));
        for (var w = 1; w <= 200; w++)
        {
            var line = SetupWizard.FormatOptionLine(12, long1, w);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"w={w} 超宽 {TextUtil.DisplayWidth(line)}");
        }
    }

    [Fact]
    public void Option_TruncatedLabelCarriesEllipsis()
    {
        var line = SetupWizard.FormatOptionLine(1, new string('x', 100), 30);
        Assert.Contains(SafeColor.Glyphs.Ellipsis, line);
    }

    [Fact]
    public void Option_ShortLabelIsUntouched()
    {
        Assert.Equal("  1) Ollama", SetupWizard.FormatOptionLine(1, "Ollama", 80));
    }

    [Fact]
    public void Option_UnknownWidthShowsEverything()
    {
        Assert.Equal("  1) " + new string('x', 300), SetupWizard.FormatOptionLine(1, new string('x', 300), 0));
    }

    [Fact]
    public void Option_NumbersStayAlignedAcrossMixedLengthLabels()
    {
        // 扫读时编号必须在同一列——否则用户要一个个数位置
        var labels = new[] { "Short", "一个中等长度的中文供应商名称", new string('x', 200) };
        var cols = new int[labels.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            var line = SetupWizard.FormatOptionLine(i + 1, labels[i], 40);
            cols[i] = line.IndexOf(')'); // 编号结束的位置
        }
        Assert.Equal(cols[0], cols[1]);
        Assert.Equal(cols[0], cols[2]);
    }

    // —— 字段行 ——

    [Fact]
    public void Field_ShowsLabelAndValue()
    {
        Assert.Equal("将更新配置文件: C:\\w\\codeagent.json",
            SetupWizard.FormatFieldLine("将更新配置文件", "C:\\w\\codeagent.json", 80));
    }

    [Fact]
    public void Field_NeverExceedsWidth()
    {
        var path = "C:\\Users\\" + new string('d', 300) + "\\codeagent.json";
        for (var w = 1; w <= 200; w++)
        {
            var line = SetupWizard.FormatFieldLine("将更新配置文件", path, w);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"w={w} 超宽");
        }
    }

    [Fact]
    public void Field_KeepsTheLabelEvenWhenTheValueIsEnormous()
    {
        // 标签是"这是什么"的信息；值截断可以接受，标签没了就不知道在说什么
        var line = SetupWizard.FormatFieldLine("将更新配置文件", new string('x', 500), 20);
        Assert.Contains("配置文件", line);
    }

    [Fact]
    public void Field_UnknownWidthShowsEverything()
    {
        var p = "C:\\a-very-long-path-indeed\\codeagent.json";
        Assert.Equal("将更新配置文件: " + p, SetupWizard.FormatFieldLine("将更新配置文件", p, 0));
    }

    [Fact]
    public void Option_AndField_AreBothUsedByTheWizard()
    {
        // 只测纯函数会漏掉「接进向导主流程」这一步
        var src = System.IO.File.ReadAllText(Find());
        Assert.Contains("FormatOptionLine(", src);
        Assert.Contains("FormatFieldLine(", src);
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "SetupWizard.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/SetupWizard.cs");
    }
}
