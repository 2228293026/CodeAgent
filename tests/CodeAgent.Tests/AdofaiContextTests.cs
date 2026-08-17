using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class AdofaiContextTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "adofai-detect-" + Guid.NewGuid().ToString("N"));

    public AdofaiContextTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    private string Touch(string relative, string content = "")
    {
        var path = Path.Combine(_tmp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Detect_InfoJsonWithEntry_ReturnsTrue()
    {
        // ADOFAI mod 的 Info.json：AssemblyName + EntryMethod 是入口声明特征
        Touch("Info.json", """{"Id":"JipperX","AssemblyName":"JipperX.Loader.UMM.dll","EntryMethod":"JipperX.Loader.UmmEntry.Load"}""");
        Assert.True(AdofaiContext.Detect(_tmp));
    }

    [Fact]
    public void Detect_RootAssemblyCSharp_ReturnsTrue()
    {
        // adofai-libs 引用库：根目录就是游戏反编译 DLL
        Touch("Assembly-CSharp.dll");
        Assert.True(AdofaiContext.Detect(_tmp));
    }

    [Fact]
    public void Detect_LibsAssemblyCSharp_ReturnsTrue()
    {
        // mod 工程：libs/ 下引用游戏程序集
        Touch("libs/Assembly-CSharp.dll");
        Assert.True(AdofaiContext.Detect(_tmp));
    }

    [Fact]
    public void Detect_UnrelatedProject_ReturnsFalse()
    {
        // 普通项目（如本仓库）不应误判
        Touch("src/Program.cs", "class Program { }");
        Assert.False(AdofaiContext.Detect(_tmp));
    }

    [Fact]
    public void Detect_InfoJsonWithoutEntry_ReturnsFalse()
    {
        // 有 Info.json 但无入口声明：不是 ADOFAI mod
        Touch("Info.json", """{"Id":"Something","Version":"1.0.0"}""");
        Assert.False(AdofaiContext.Detect(_tmp));
    }

    [Fact]
    public void ExtraModes_ContainExpectedModes()
    {
        var names = AdofaiContext.ExtraModes.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("moddev", names);
        Assert.Contains("harmony", names);
        Assert.Contains("assetbundle", names);
        Assert.Equal(3, AdofaiContext.ExtraModes.Count);
    }

    [Fact]
    public void ExtraSystemPrompt_NotBlankAndMentionsAdofai()
    {
        Assert.False(string.IsNullOrWhiteSpace(AdofaiContext.ExtraSystemPrompt));
        Assert.Contains("ADOFAI", AdofaiContext.ExtraSystemPrompt);
    }

    [Fact]
    public void FindKnowledgeBase_InCurrentDir_ReturnsPath()
    {
        var kb = Touch("AdofaiKnowledge.md", "# ADOFAI API 知识库");
        Assert.Equal(kb, AdofaiContext.FindKnowledgeBase(_tmp));
    }

    [Fact]
    public void FindKnowledgeBase_InParentAdofaiLibs_ReturnsPath()
    {
        // mod 项目（如 JipperOverlayer）在 D:/Projects 下，知识库在兄弟目录 adofai-libs/
        var parent = Path.GetDirectoryName(_tmp)!;
        var libsDir = Path.Combine(parent, "adofai-libs");
        Directory.CreateDirectory(libsDir);
        var kb = Path.Combine(libsDir, "AdofaiKnowledge.md");
        File.WriteAllText(kb, "# ADOFAI API 知识库");
        try
        {
            Assert.Equal(kb, AdofaiContext.FindKnowledgeBase(_tmp));
        }
        finally
        {
            Directory.Delete(libsDir, recursive: true);
        }
    }

    [Fact]
    public void FindKnowledgeBase_Absent_ReturnsNull()
    {
        Assert.Null(AdofaiContext.FindKnowledgeBase(_tmp));
    }
}
