using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/model &lt;名字&gt;</c> 打错时的「相近的模型」。
///
/// 此前判据只有 <c>m.Contains(family)</c>，family 取输入按 <c>-</c>/<c>.</c> 的首段，
/// 命中后按列表原序返回。两个后果：
/// (1) **顺序不对**——<c>deepseek-cht</c> 列出的第一个候选是 deepseek-chat 的邻居，
///     而 deepseek-v3.2 这种无关模型混在里面；
/// (2) **家族段打错时一个候选都不给**——<c>gpt4o</c>（少一个连字符）、
///     <c>deepseeek-r1</c>（e 多打一个）都只得到「模型列表中没有「X」」，
///     而列表里明明有 gpt-4o、deepseek-r1。真实模型名里 <c>/</c> 极常见
///     （<c>kilo/stealth/space-bunny-alpha</c>），首段判据把这一整类名字全挡在门外。
/// </summary>
public sealed class SuggestModelsTests
{
    private static readonly string[] Models =
    [
        "deepseek-chat", "deepseek-coder", "deepseek-math", "deepseek-reasoner",
        "deepseek-v3", "deepseek-v3.1", "deepseek-v3.2", "deepseek-r1",
        "gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini", "gpt-5",
        "claude-3-5-sonnet", "claude-3-7-sonnet", "claude-sonnet-4",
        "gemini-1.5-pro", "gemini-2.0-flash", "kilo/stealth/space-bunny-alpha",
    ];

    [Fact]
    public void TheNearestModelComesFirst()
    {
        // 改动前 family 过滤后按列表原序返回：deepseek-chat 排在 coder/math 前面，
        // 而 deepseek-v3.2 与这个输入毫无关系
        Assert.Equal("deepseek-chat", First(Program.SuggestModels(Models, "deepseek-cht")));
        Assert.Equal("gemini-2.0-flash", First(Program.SuggestModels(Models, "gemini-2-flash")));
        Assert.Equal("gpt-4o-mini", First(Program.SuggestModels(Models, "gpt-4o-min")));
    }

    [Fact]
    public void UnrelatedModelsAreNotOffered()
    {
        // 家族过滤原本顺带挡住噪音；改成重排后这个界限没了，
        // 不补距离预算就会把 gemini-1.5-pro 混进 gemini 的候选里
        var near = Program.SuggestModels(Models, "gemini-2-flash");
        Assert.DoesNotContain("gemini-1.5-pro", near);
        Assert.DoesNotContain("gpt-4o", near);
        var reasoner = Program.SuggestModels(Models, "deepseek-reasonr");
        Assert.DoesNotContain("deepseek-v3.2", reasoner);
        Assert.DoesNotContain("deepseek-math", reasoner);
    }

    [Fact]
    public void AMisspelledFamilyStillFindsTheModel()
    {
        // 回归点二：家族段打错时曾一个候选都不给
        Assert.Equal("gpt-4o", First(Program.SuggestModels(Models, "gpt4o")));
        Assert.Equal("deepseek-r1", First(Program.SuggestModels(Models, "deepseeek-r1")));
        Assert.Equal("deepseek-chat", First(Program.SuggestModels(Models, "deepseeek-chat")));
    }

    [Fact]
    public void ModelNamesContainingSlashAreReachable()
    {
        // kilo/stealth/space-bunny-alpha：首段判据（按 - 和 . 切）根本覆盖不到
        Assert.Equal("kilo/stealth/space-bunny-alpha", First(Program.SuggestModels(Models, "kilo/stealth/space-bunny-alfa")));
        // 只打后半截也是常用写法：编辑距离 13，纯粹按距离会被预算挡成一个候选都不给——
        // 那比改之前（家族匹配）更差。名字含有输入就算够近
        Assert.Equal("kilo/stealth/space-bunny-alpha", First(Program.SuggestModels(Models, "space-bunny-alpha")));
    }

    [Fact]
    public void AnAbbreviatedFamilyIsNotMistakenForATypo()
    {
        // /model gpt 是缩写（距离 3），/model deepseek 是缩写（距离 7）：
        // 两者都必须给候选，而不是被距离预算判成"离得太远"
        Assert.Contains("gpt-4o", Program.SuggestModels(Models, "gpt"));
        Assert.Contains("deepseek-chat", Program.SuggestModels(Models, "deepseek"));
    }

    [Fact]
    public void TheShortestEquallyCloseNameComesFirst()
    {
        // /model gpt 下 gpt-5（5 列）比 gpt-4o（6 列）短，同距离（缩写分）下短名优先——
        // 名字越短说明厂商前缀越少，越接近用户心里那个名字
        var near = Program.SuggestModels(Models, "gpt", max: 3);
        Assert.Equal("gpt-5", near[0]);
        Assert.Equal(3, near.Count);
    }

    [Fact]
    public void EquallyCloseAndEquallyShortKeepsTheProviderOrder()
    {
        // 同距离且**同长度**时才回到列表顺序——而那正是 /model <编号> 的编号顺序，
        // 提示顺序与编号对不上会让人以为自己看错了列表。
        // 三个名字必须真的"够近"，否则全被滤掉、这个用例测不到排序。
        // 距离是缩写分 2 而不是 1：xx 落在 xx-1 里，属前缀变体，不是打错一个字
        var sameLength = new[] { "xx-1", "xx-2", "xx-3" };
        foreach (var m in sameLength)
            Assert.Equal(Program.AbbreviationScore, Program.ModelDistance("xx", m));
        var near = Program.SuggestModels(sameLength, "xx", max: 3);
        Assert.Equal(3, near.Count);
        Assert.Equal("xx-1", near[0]);
        Assert.Equal("xx-2", near[1]);
        Assert.Equal("xx-3", near[2]);
    }

    [Fact]
    public void CaseIsIgnoredWhenRanking()
    {
        // 各 Provider 大小写不统一（GPT-4o / gpt-4o / Gemini），排序前统一小写
        Assert.Equal("gpt-4o", First(Program.SuggestModels(Models, "GPT-4O")));
        Assert.Equal("deepseek-chat", First(Program.SuggestModels(Models, "DeepSeek-Chat")));
    }

    [Fact]
    public void AnAdjacentSwapIsStillOneEdit()
    {
        // 与 /命令、/取值 同一套 Damerau 口径：清单名里换位很常见
        Assert.Equal("claude-3-5-sonnet", First(Program.SuggestModels(Models, "claude-3-5-sonet")));
    }

    [Fact]
    public void AnExactMatchIsFirst()
    {
        foreach (var exact in new[] { "deepseek-chat", "gpt-4o", "claude-sonnet-4" })
            Assert.Equal(exact, First(Program.SuggestModels(Models, exact)));
    }

    [Fact]
    public void AVersionNumberTypoFindsTheNeighbouringVersion()
    {
        // gpt-4.0 与 gpt-4.1 只差一个字符，而 gpt-4o 也只差一个——都由距离说了算
        Assert.Contains("gpt-4.1", Program.SuggestModels(Models, "gpt-4.0"));
    }

    [Fact]
    public void SomethingUnrelatedGetsNothing()
    {
        // 离所有模型都很远时不提示：宁可不说，也不要凑数
        Assert.Empty(Program.SuggestModels(Models, "zzzzzzzzzz"));
        Assert.Empty(Program.SuggestModels(Models, "banana"));
    }

    [Fact]
    public void AnEmptyFamilyGetsNothing()
    {
        Assert.Empty(Program.SuggestModels(Models, "-x"));
        Assert.Empty(Program.SuggestModels(Models, ".y"));
    }

    [Fact]
    public void AnEmptyModelListGetsNothing()
    {
        Assert.Empty(Program.SuggestModels([], "gpt-4o"));
    }

    [Fact]
    public void ABareTailSegmentMatchesTheVendoredName()
    {
        // 真机实测（openai provider，168 个模型）：deepseek-v4-flash 这个名字**确实在列表里**，
        // 只是作为 commandcode/deepseek/deepseek-v4-flash 等名字的**后缀**。
        // 按全串距离比，它排在 zen/cnb 前缀变体之后——用户敲了正确的名字，
        // 「相近的模型」却只回三个带前缀的，正确答案被自己的候选淹没。
        Assert.Equal(0, Program.ModelDistance("deepseek-v4-flash", "commandcode/deepseek/deepseek-v4-flash"));
        // 打错一个字母时是**近**而不是相等：尾段差 1，仍在预算 3 之内。
        // 写成 0 会把「拼错的」与「完全正确的」混为一谈——
        // 而排第一的判据正是这个距离，所以这里必须量出真实值。
        Assert.Equal(1, Program.ModelDistance("deepseek-v4-flah", "cnb/deepseek-v4-flash"));
    }

    [Fact]
    public void TheBareNameOutranksTheVendoredVariants()
    {
        // 只测 ModelDistance 不够：把尾段命中从 0 降成 1，排序结果一模一样，突变存活。
        // 真正被用户看见的是**候选顺序**，所以要直接断言它。
        var vendored = new[]
        {
            "zen/deepseek-v4-flash-free", "cnb/deepseek-v4-flash", "commandcode/deepseek/deepseek-v4-flash",
        };
        var near = Program.SuggestModels(vendored, "deepseek-v4-flash");
        // 尾段全相等、距离都是 0，只能靠「短名优先」分高下：
        // cnb/deepseek-v4-flash（22 列）比 commandcode/deepseek/deepseek-v4-flash（38 列）更接近字面
        Assert.Equal("cnb/deepseek-v4-flash", First(near));
        Assert.Contains("zen/deepseek-v4-flash-free", near); // 仍会出现，只是排在后面
    }

    [Fact]
    public void AVendoredNameIsAlreadyInsideTheFamilyPool()
    {
        // 曾经以为「家族过滤把 cnb/deepseek-v4-flash 挡在了家族外」，
        // 于是加了一条「家族内都不够近就扩大到全列表」的兜底规则。
        // **实测证明那是空操作**：家族段用的是**子串**匹配而不是前缀匹配，
        // cnb/deepseek-v4-flash 含 deepseek，本来就在家族池里；
        // 删掉兜底后真机输出逐字节相同，84 个测试也全绿。
        // 一条"看起来在修 bug"的分支，留下的只有注释在声称它修了什么——
        // 所以兜底被删掉了，本用例把该事实钉住，免得有人再"修"一次。
        var list = new[]
        {
            "deepseek-chat", "deepseek-reasoner", "deepseek-v3.2",
            "cnb/deepseek-v4-flash", "gpt-4o",
        };
        Assert.Equal(6, Program.ModelDistance("deepseek-v4-flah", "deepseek-chat"));
        Assert.Equal(1, Program.ModelDistance("deepseek-v4-flah", "cnb/deepseek-v4-flash"));
        Assert.Equal("cnb/deepseek-v4-flash", First(Program.SuggestModels(list, "deepseek-v4-flah")));
        // 形状守卫：兜底分支不该再回来
        var src = File.ReadAllText(Find());
        Assert.DoesNotContain("inFamily.Any(m => ModelDistance", src);
    }

    [Fact]
    public void AnAbbreviationIsNotJudgedByTheDistanceBudget()
    {
        // gpt 只有 3 个字母 → 预算 1，而缩写固定分是 2：受预算约束的话一个候选都拿不到。
        // 缩写不是"离得远"，是有意只打一部分，两者不该用同一条尺子量
        Assert.Equal(1, Math.Clamp("gpt".Length / 3, 1, 3));
        Assert.Contains("gpt-4o", Program.SuggestModels(Models, "gpt"));
        Assert.Contains("deepseek-chat", Program.SuggestModels(Models, "deepseek"));
    }

    [Fact]
    public void ARealTypoIsStillJudgedByTheBudget()
    {
        // 缩写免检不等于放宽：v4-flah 两头不沾，距离 10 ≫ 预算 2，必须一个候选都不给
        Assert.Empty(Program.SuggestModels(Models, "v4-flah"));
        Assert.Empty(Program.SuggestModels(Models, "banana"));
    }

    [Fact]
    public void APrefixVariantIsNotAsCloseAsAnExactMatch()
    {
        // zen/deepseek-v4-flash-free 里含有 deepseek-v4-flash，但它多了 -free，
        // 不是用户敲的那个名字。曾经两者都是 0 分，排序退化成列表顺序，
        // 把真正命中的 cnb/deepseek-v4-flash 挤到第三位
        Assert.Equal(0, Program.ModelDistance("deepseek-v4-flash", "cnb/deepseek-v4-flash"));
        Assert.Equal(Program.AbbreviationScore, Program.ModelDistance("deepseek-v4-flash", "zen/deepseek-v4-flash-free"));
        Assert.True(Program.ModelDistance("deepseek-v4-flash", "cnb/deepseek-v4-flash")
            < Program.ModelDistance("deepseek-v4-flash", "zen/deepseek-v4-flash-free"));
    }

    [Fact]
    public void AnAbbreviationIsStillOfferedDespiteItsPenalty()
    {
        // 惩罚不等于排除：/model gpt 仍要给候选，只是排在精确命中之后
        Assert.Equal(Program.AbbreviationScore, Program.ModelDistance("gpt", "gpt-4o"));
        Assert.Contains("gpt-4o", Program.SuggestModels(Models, "gpt"));
        Assert.Contains("deepseek-chat", Program.SuggestModels(Models, "deepseek"));
    }

    [Fact]
    public void ADeepPathIsMatchedByItsTailNotBlindly()
    {
        // 尾段完全相等 → 命中（名字就在那儿，只是前面带了厂商前缀）
        Assert.Equal(0, Program.ModelDistance("deepseek-v4-flash", "commandcode/deepseek/deepseek-v4-flash"));
        // 输入是名字的前缀时：deepseek-v4-flash-pro 的尾段与输入差 4（超预算 3），
        // 但整名含输入 → 缩写分 2。按旧口径写成 0 是错的，实测就是 2
        Assert.Equal(Program.AbbreviationScore, Program.ModelDistance("deepseek-v4-flash", "deepseek-v4-flash-pro"));
        // 但尾段对不上、也不构成缩写时，距离必须为正——
        // "deepsee" 落在 "deepseek-v4-flash" 里，属于家族缩写（2）；
        // "v4-flah" 两头都不沾，距离 10、远超预算 2
        Assert.Equal(Program.AbbreviationScore, Program.ModelDistance("deepsee", "commandcode/deepseek/deepseek-v4-flash"));
        Assert.Equal(10, Program.ModelDistance("v4-flah", "commandcode/deepseek/deepseek-v4-flash"));
    }

    [Fact]
    public void ASubstringAbbreviationIsStillFree()
    {
        // 缩写不"免费"（它拿固定分而非 0），但仍在预算内，因此照常被给出
        Assert.Equal(Program.AbbreviationScore, Program.ModelDistance("deepseek", "commandcode/deepseek/deepseek-v4-flash"));
        Assert.Equal(Program.AbbreviationScore, Program.ModelDistance("deepsee", "commandcode/deepseek/deepseek-v4-flash"));
    }

    [Fact]
    public void AnUnrelatedNameStaysFar()
    {
        // 离得远的不能被尾段规则捞进来：预算之外的输入一个候选都不该有
        Assert.Equal(10, Program.ModelDistance("v4-flah", "commandcode/deepseek/deepseek-v4-flash"));
        Assert.Equal(6, Program.ModelDistance("banana", "gpt-4o"));
    }

    [Fact]
    public void AFullyQualifiedNameStillMatches()
    {
        // 用户也可能照抄完整名：kilo/stealth/space-bunny-alfa 与全名只差一个字母。
        // 尾段相距 15（预算外），但全串距离 2 兜住了——两者取小，所以不是被尾段规则否决的。
        Assert.Equal(2, Program.ModelDistance("kilo/stealth/space-bunny-alfa", "kilo/stealth/space-bunny-alpha"));
        Assert.True(Program.ModelDistance("kilo/stealth/space-bunny-alfa", "kilo/stealth/space-bunny-alpha") <= 3);
    }

    [Fact]
    public void ResultsAreBounded()
    {
        var near = Program.SuggestModels(Models, "deepseek", max: 2);
        Assert.True(near.Count <= 2, string.Join(",", near));
    }

    [Fact]
    public void SuggestionsAreDeterministic()
    {
        // 同距离必须按字典序，否则每次提示顺序不同，像在随机
        for (var i = 0; i < 5; i++)
            Assert.Equal(Program.SuggestModels(Models, "gpt4o"), Program.SuggestModels(Models, "gpt4o"));
    }

    [Fact]
    public void TheModelCommandUsesTheRankedHelper()
    {
        // 形状守卫：只测 SuggestModels 证明不了 /model 真的调用了它
        var src = System.IO.File.ReadAllText(Find());
        Assert.Contains("var near = SuggestModels(knownModels, modelArg);", src);
        Assert.Contains("$\"  相近的模型: {string.Join(\"、\", near)}\"", src);
    }

    private static string First(IReadOnlyList<string> xs) => xs.Count > 0 ? xs[0] : "(空)";

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
