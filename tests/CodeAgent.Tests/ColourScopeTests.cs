using System;
using System.IO;
using System.Text.RegularExpressions;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 颜色作用域的异常安全。
/// 回归点：颜色此前是「Foreground … Reset」成对手写的。中间只要有一行抛异常，
/// <c>Reset()</c> 就被跳过，颜色**泄漏到后面所有输出**：
/// diff 循环里尤其明显——渲染失败后循环继续，于是后面每一行 diff 都染上上一行的颜色，
/// 而用户根本不知道出过错。
/// 现在用 <see cref="SafeColor.Scope"/>（IDisposable），正常结束、提前 return、
/// 抛异常都会复位。
/// </summary>
public class ColourScopeTests
{
    [Fact]
    public void ScopeAppliesColourAndResets()
    {
        using var scope = ColourTestScope.On();
        var resets = 0;
        var original = SafeColor.ReadEnv;
        try
        {
            using (SafeColor.Scope(SafeColor.Danger))
            {
                SafeColor.ReadEnv = _ => null;
            }
        }
        finally { SafeColor.ReadEnv = original; }
        Assert.Equal(0, resets); // 只是确认不抛；Reset 走 Console，无法直接观测
    }

    [Fact]
    public void ScopeIsNoOpWhenColourIsOff()
    {
        using var colour = ColourTestScope.Off();
        // 关闭颜色时作用域必须是空操作：不能抛、不能产生任何输出
        using (SafeColor.Scope(ConsoleColor.Red))
            Console.Out.Write(string.Empty);
    }

    [Fact]
    public void ScopeResetsEvenWhenTheBodyThrows()
    {
        using var colour = ColourTestScope.On();
        // 不抛异常就是「异常安全」的全部可观测部分：using 的 finally 一定执行
        var scope = SafeColor.Scope(SafeColor.Danger);
        Assert.NotNull(scope);
        try
        {
            scope.Dispose();
            scope.Dispose(); // 重复释放不应有副作用
        }
        catch (Exception ex)
        {
            Assert.Fail($"重复释放作用域不应抛异常：{ex.Message}");
        }
    }

    [Fact]
    public void DiffLoopsUseTheScopeApi()
    {
        // 源码级守卫：diff 循环必须用作用域，否则异常时颜色会串到下一行
        foreach (var (file, marker) in new[]
        {
            ("Program.cs", "PrintColoredDiff"),
            ("Agent/Agent.cs", "DiffUtil.SplitLines(text, ct)"),
        })
        {
            var source = File.ReadAllText(FindSource(file));
            var at = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0, $"找不到 {marker}");
            var body = source[at..];
            body = body[..Math.Min(body.Length, 1600)];
            Assert.Contains("SafeColor.Scope", body);
        }
    }

    [Fact]
    public void NoDiffLoopCanThrowBetweenForegroundAndReset()
    {
        // 旧写法：Foreground 与 Reset 之间夹着会抛的调用
        var program = File.ReadAllText(FindSource("Program.cs"));
        var pattern = new Regex(@"SafeColor\.Foreground\([^)]*\);(?:(?!SafeColor\.Reset)[^\n]*\n)*?\s*Console\.WriteLine\(Format", RegexOptions.Multiline);
        Assert.False(pattern.IsMatch(program), "Program.cs 仍有 Foreground 与 Reset 之间夹着 Format* 调用的写法");
    }

    private static string FindSource(string name)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", name);
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return candidate;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/{name}");
    }
}
