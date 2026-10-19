using System;
using System.IO;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 工具开始行与进度动画的去重。
///
/// 顺序执行时两条摘要会重复出现：
/// <code>
///   ▸ Read(a.cs) …            ← 开始行
///   ✔ Read(a.cs) (0.1s)       ← 结果行，摘要原样再念一遍
/// </code>
/// 两条一样的摘要把真正有信息量的第二行淹掉；更糟的是那个「…」写在滚动区里擦不掉，
/// 事后看起来像还在跑。动画本身就是更好的开始提示，此时静态开始行应当让位。
///
/// 但**重定向输出（日志）时不能省**：那时不起动画，日志需要起止两条来对照。
/// </summary>
public class ToolStartLineDedupTests
{
    [Fact]
    public void StartLineIsSuppressedOnlyWhileTheAnimationRuns()
    {
        var src = ReadSource("Agent.cs");
        var at = src.IndexOf("var progressActive =", StringComparison.Ordinal);
        Assert.True(at > 0, "执行路径里没有判定进度动画是否在跑");
        var body = src.Substring(at, Math.Min(400, src.Length - at));
        // 必须同时依赖 showLog 与 progressActive：只判 progressActive 会把
        // 「用户关掉了工具日志」这件事覆盖掉，只判 showLog 则完全没有去重
        Assert.Contains("_toolProgressCts is not null", body);
        Assert.Contains("showLog && !progressActive", body);
    }

    [Fact]
    public void TheAnimationIsArmedBeforeTheCallAndDisarmedAfter()
    {
        // 顺序：StartToolProgress → ExecuteToolCallAsync（内部读 _toolProgressCts）
        //       → finally StopToolProgress
        // 若 Start 在 Execute 之后，去重判定永远为假，缺陷原样保留。
        var src = ReadSource("Agent.cs");
        var start = src.IndexOf("StartToolProgress(SummarizeCall", StringComparison.Ordinal);
        var exec = src.IndexOf("results.Add(await ExecuteToolCallAsync", StringComparison.Ordinal);
        Assert.True(start > 0 && exec > start, "动画必须在工具执行之前起");
    }

    [Fact]
    public void RedirectedOutputStillGetsTheStartLine()
    {
        // 管道/日志不起动画（ShouldShowSpinner 为假），_toolProgressCts 保持 null，
        // 于是 showLog && !progressActive 为真——开始行照旧。
        // 这里守的是「别把去重写成了无条件删除」：那会让日志失去起止两条的对照。
        var src = ReadSource("Agent.cs");
        var at = src.IndexOf("private void StartToolProgress", StringComparison.Ordinal);
        Assert.True(at > 0);
        var body = src.Substring(at, Math.Min(300, src.Length - at));
        Assert.Contains("ShouldShowSpinner(IsOutputRedirected())", body);
    }

    private static string ReadSource(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", "Agent", file);
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return File.ReadAllText(candidate);
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/Agent/{file}");
    }
}
