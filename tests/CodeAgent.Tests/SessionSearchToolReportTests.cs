using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

// 工具按 **Environment.CurrentDirectory** 解析会话目录，改进程 CWD 是全局状态：
// 放到独立 collection 里串行执行，别和别的测试互相踩。

/// <summary>
/// <c>session_search</c>（模型侧会话搜索）的两处**说错数字/说假话**。
///
/// ① <c>匹配 {printed} 个会话</c>：printed 是**已显示数**（受 max_files 封顶）。
///    20 个会话命中、只显示 3 个时，模型被告知"匹配 3 个"——它会照着复述给用户，于是少报。
///    REPL 那侧早有 moreAvailable 处理（"…仅显示前 5 个命中文件"），这里没有。
///
/// ② 0 命中时回「没有匹配 "X" 的内容」：与 REPL 的 /find 同一个毛病——
///    ASCII 关键字只出现在更长的词内部时会被词边界规则滤掉，那句话是**假话**。
/// </summary>
[Collection("CurrentDirectory")]
public sealed class SessionSearchToolReportTests
{
    private static string NewSessions(params string[] bodies)
    {
        var root = Path.Combine(Path.GetTempPath(), "codeagent-ss-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, ".codeagent", "sessions");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < bodies.Length; i++)
            File.WriteAllText(Path.Combine(dir, $"s{i}.jsonl"), bodies[i]);
        return root;
    }

    /// <summary>把进程 CWD 临时切到 <paramref name="root"/>，用完还原。
    /// 工具读的是 Environment.CurrentDirectory + config.SessionDir，不是 ctx.Workspace——
    /// 第一次写测试时按 Workspace 造数据，结果全查到仓库自己的 .codeagent 去了。</summary>
    private static async Task<string> RunIn(string root, string keyword, int maxFiles)
    {
        var original = Environment.CurrentDirectory;
        Directory.SetCurrentDirectory(root);
        try
        {
            return await Run(root, keyword, maxFiles);
        }
        finally
        {
            Environment.CurrentDirectory = original;
        }
    }

    private static async Task<string> Run(string root, string keyword, int maxFiles = 3)
    {
        var config = new AgentConfig { SessionDir = ".codeagent/sessions" };
        var ctx = new AgentContext
        {
            Config = config,
            Workspace = new Workspace(root, [], "full"),
        };
        var tool = new SessionSearchTool();
        var args = new System.Text.Json.Nodes.JsonObject
        {
            ["keyword"] = keyword,
            ["max_files"] = maxFiles,
        };
        return await tool.ExecuteAsync(args, ctx, CancellationToken.None);
    }

    [Fact]
    public async Task ReportsTheTotalWhenNotCapped()
    {
        var root = NewSessions(
            """{"role":"user","content":"看 ColorThemeTests.cs"}""",
            """{"role":"user","content":"再看 ColorTheme"}""",
            """{"role":"user","content":"第三处 Color"}""");
        try
        {
            var outp = await RunIn(root, "Color", maxFiles: 5);
            Assert.Contains("匹配 3 个会话:", outp);
            Assert.DoesNotContain("显示前", outp);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task SaysItIsShowingOnlyTheFirstFew()
    {
        // 这是本轮的核心修复：命中 5 个、只显示 2 个时，数字必须是 5 而不是 2
        var bodies = Enumerable.Range(0, 5)
            .Select(_ => """{"role":"user","content":"命中词在这里"}""").ToArray();
        var root = NewSessions(bodies);
        try
        {
            var outp = await RunIn(root, "命中词", maxFiles: 2);
            // 封顶后不再逐个读后面的日志，所以只能诚实地说"至少"——绝不报一个假的精确总数
            Assert.Contains("匹配 ≥2 个会话", outp);
            Assert.Contains("显示前 2 个", outp);
            Assert.DoesNotContain("匹配 2 个会话（", outp);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task InteriorOnlyMatchIsNotReportedAsNoMatch()
    {
        // 内容里确实有 lorTheme（ColorThemeTests 内部），但词边界规则会滤掉它
        var root = NewSessions("""{"role":"user","content":"看 ColorThemeTests.cs"}""");
        try
        {
            var outp = await RunIn(root, "lorTheme", maxFiles: 3);
            Assert.DoesNotContain("没有匹配", outp);
            Assert.Contains("更长的词内部", outp);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task GenuineNoMatchSaysNoMatch()
    {
        var root = NewSessions("""{"role":"user","content":"完全无关的内容"}""");
        try
        {
            var outp = await RunIn(root, "zzzzzz", maxFiles: 3);
            Assert.Contains("没有匹配", outp);
            Assert.DoesNotContain("更长的词内部", outp);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task CjkKeywordKeepsThePlainNoMatchMessage()
    {
        // 中文不走词边界，那种情形根本不会出现
        var root = NewSessions("""{"role":"user","content":"完全无关的内容"}""");
        try
        {
            var outp = await RunIn(root, "绝对不存在", maxFiles: 3);
            Assert.Contains("没有匹配", outp);
            Assert.DoesNotContain("更长的词内部", outp);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TheToolCountsSeparatelyFromWhatItPrints()
    {
        var src = ReadSource(FindTool());
        Assert.Contains("var matched = 0;", src);
        // 计数必须发生在封顶判断**之前**，否则 matched 会被 max_files 截断成同一个数
        var matchedAt = src.IndexOf("matched++;", StringComparison.Ordinal);
        var capAt = src.IndexOf("if (printed >= maxFiles)", StringComparison.Ordinal);
        Assert.True(matchedAt > 0 && capAt > matchedAt, "matched++ 必须在封顶判断之前");
    }

    private static string ReadSource(string p) => File.ReadAllText(p).Replace("\r\n", "\n");

    private static string FindTool()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(d, "src", "CodeAgent", "Tools", "SessionSearchTool.cs");
                if (File.Exists(f))
                    return f;
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException("找不到 SessionSearchTool.cs");
    }
}
