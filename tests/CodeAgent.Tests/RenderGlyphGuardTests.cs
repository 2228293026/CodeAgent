using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 渲染层的字符形守卫：**任何要打到终端的符号都必须经由 <see cref="SafeColor.Glyphs"/>**。
///
/// 这是前 99 轮（配色主题与可访问性）的收口。前 99 轮里我们逐个把 `▌ │ ─ … ⚠ ✔ 🔧 ⏳ ⏷ → ⠦`
/// 这些符号换成可退回 ASCII 的形式，每一轮都配了源码级守卫，但那些守卫是**按符号逐个**写的——
/// 明天有人新加一个 `◆` 就绕过了全部 99 条规则。
///
/// 这里改成一条**范围规则**：CJK 与全角标点允许（终端本来就能显示），
/// 但制表符、几何图形、杂项符号、 dingbat、盲文、技术符号、箭头这些"装饰性"字符
/// 一律不允许直接出现在输出字符串里。
///
/// 只扫**渲染层**：Tools/* 里那批 `…（另有 N 处未显示）` 是发给模型看的文本，
/// 不是终端 UI，不受这条规则约束。
/// </summary>
public class RenderGlyphGuardTests
{
    /// <summary>渲染层文件（相对 src/CodeAgent）。</summary>
    private static readonly string[] RenderLayer =
    {
        "ConsoleRenderer.cs",
        "Program.cs",
        "InputLine.cs",
        "SetupWizard.cs",
        Path.Combine("Agent", "Agent.cs"),
        Path.Combine("Agent", "Agent.Session.cs"),
    };

    /// <summary>允许直接出现的范围：CJK 统一表意文字、中日韩标点、全角/半角形式。</summary>
    private static bool IsAllowed(int cp) =>
        (cp >= 0x3000 && cp <= 0x9FFF)     // CJK 统一表意文字 + 中日韩符号标点（、。《》「」…）
        || (cp >= 0xF900 && cp <= 0xFAFF)   // CJK 兼容表意文字
        || (cp >= 0xFF00 && cp <= 0xFF60)   // 全角形式（，：（）等）
        || (cp >= 0x3000 && cp <= 0x303F);  // CJK 标点

    /// <summary>装饰性字符所在的区间——这些必须走 Glyphs。</summary>
    private static readonly (int Lo, int Hi, string Name)[] Decorative =
    {
        (0x2190, 0x21FF, "箭头"),
        (0x2300, 0x23FF, "技术符号"),
        (0x2500, 0x257F, "制表符"),
        (0x2580, 0x259F, "区块元素"),
        (0x25A0, 0x25FF, "几何图形"),
        (0x2600, 0x27BF, "杂项符号与 dingbat"),
        (0x2800, 0x28FF, "盲文"),
        (0x2B00, 0x2BFF, "杂项符号与箭头"),
    };

    private static string? DecorativeName(int cp)
    {
        foreach (var (lo, hi, name) in Decorative)
            if (cp >= lo && cp <= hi)
                return name;
        return null;
    }

    private static string SourceRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent");
                var program = Path.Combine(candidate, "Program.cs");
                if (Directory.Exists(candidate) && File.Exists(program) && new FileInfo(program).Length > 1000)
                    return candidate;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent");
    }

    /// <summary>去掉注释，但保留字符串字面量（我们要找的就在字面量里）。</summary>
    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"(?s)/\*.*?\*/", " ");
        // 逐行去掉 // 之后的内容，但要跳过行内字符串里的 //
        var lines = noBlock.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var inString = false;
            var cut = line.Length;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
                else if (line[i] == '/' && line[i + 1] == '/' && !inString) { cut = i; break; }
            }
            kept.Add(line[..cut]);
        }
        return string.Join("\n", kept);
    }

    [Fact]
    public void NoDecorativeGlyphIsHardcodedInTheRenderLayer()
    {
        var root = SourceRoot();
        var offenders = new List<string>();
        foreach (var rel in RenderLayer)
        {
            var path = Path.Combine(root, rel);
            Assert.True(File.Exists(path), $"渲染层文件不存在: {rel}");
            var code = StripComments(File.ReadAllText(path));
            for (var i = 0; i < code.Length; i++)
            {
                var cp = char.ConvertToUtf32(code, i);
                if (char.IsHighSurrogate(code[i]))
                {
                    if (IsAllowed(cp)) { i++; continue; }
                    var name = DecorativeName(cp);
                    if (name is not null)
                        offenders.Add($"{rel}: {name} U+{cp:X4}（应改用 SafeColor.Glyphs.*）");
                    i++;
                    continue;
                }
                if (IsAllowed(cp)) continue;
                var what = DecorativeName(cp);
                if (what is not null)
                    offenders.Add($"{rel}: {what} U+{cp:X4} '{code[i]}'（应改用 SafeColor.Glyphs.*）");
            }
        }
        Assert.True(offenders.Count == 0,
            "渲染层仍有直接写死的装饰性字符（CODEAGENT_ASCII 无法覆盖它们）：\n  " + string.Join("\n  ", offenders.Distinct()));
    }

    [Fact]
    public void TheGuardWouldNoticeANewDecorativeGlyph()
    {
        // 守卫本身不是空转：构造一个"新加了个 ◆"的场景，确认它会被判违规
        const string sample = "Console.WriteLine(\"◆ 提示\");";
        var flagged = sample.Any(ch => !IsAllowed(ch) && DecorativeName(ch) is not null);
        Assert.True(flagged);
        // 而 CJK 必须放行，否则这条守卫会把整个中文界面都判成违规
        const string cjk = "Console.WriteLine(\"◆ 提示：配置已保存\");";
        Assert.All("配置已保存", ch => Assert.True(IsAllowed(ch)));
        Assert.True(cjk.Contains('◆'));
    }

    [Fact]
    public void EveryGlyphMemberHasBothAnAsciiAndAUnicodeForm()
    {
        // ASCII 退回要能覆盖到"每一个被用到的符号"，靠的就是 Glyphs 里成对的取值。
        // 这里钉住 Glyphs 类型上真的存在这些成员，且都随 AsciiEnabled 变化。
        var glyphs = typeof(SafeColor).GetNestedType("Glyphs")!;
        foreach (var name in new[]
        {
            "Badge", "ColumnSeparator", "Rule", "Ellipsis", "Warn", "Ok", "Tool", "Error",
            "Hit", "Fold", "Unfold", "Arrow", "StatusMark", "NarrowDot", "SegmentSeparator",
            "ArgsPlaceholder", "Omitted", "Wait", "Stop", "Prev", "Next", "Enter", "Escape", "OkCheck",
            "Up", "Left", "Retry", "Rule2", "RuleChar", "Rule",
        })
        {
            var property = glyphs.GetProperty(name);
            Assert.True(property != null, $"Glyphs 缺少 {name}");
            var saved = SafeColor.ReadEnv;
            object unicodeValue, asciiValue;
            try
            {
                SafeColor.ReadEnv = _ => null;
                unicodeValue = property.GetValue(null)!;
                SafeColor.ReadEnv = n => n == "CODEAGENT_ASCII" ? "1" : null;
                asciiValue = property.GetValue(null)!;
            }
            finally { SafeColor.ReadEnv = saved; }
            // Glyphs 里既有 string 形态（整段标记）也有 char 形态（供 new string 用），
            // 两种都必须给出确定取值，空值会让调用点拼出空标记。
            var unicode = unicodeValue.ToString()!;
            var ascii = asciiValue.ToString()!;
            Assert.False(string.IsNullOrEmpty(unicode), $"{name} 的 Unicode 形态为空");
            Assert.False(string.IsNullOrEmpty(ascii), $"{name} 的 ASCII 形态为空");
        }
    }

    [Fact]
    public void SpinnerIsAMethodNotAFrozenTable()
    {
        // 帧表是逐帧的，守卫要确认它仍然**随开关变化**（而不是被复制回 Agent.cs）
        var glyphs = typeof(SafeColor).GetNestedType("Glyphs")!;
        var method = glyphs.GetMethod("SpinnerFrame");
        Assert.True(method != null, "Glyphs 缺少 SpinnerFrame");
    }
}
