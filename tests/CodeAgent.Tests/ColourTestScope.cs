using System;
using CodeAgent;

namespace CodeAgent.Tests;

/// <summary>把 <see cref="SafeColor"/> 固定在「颜色开启」或「颜色关闭」状态。
///
/// 为什么需要它：<c>dotnet test</c> 的输出**本来就被重定向</c>，所以
/// <see cref="SafeColor.Enabled"/> 天然为 false。直接断言「颜色下会去掉 Markdown 标记」
/// 的测试，在本地和 CI 上跑的是**不同分支**——本地终端跑、CI 管道跑，
/// 断言却写死了一个前提。CI 抓到的失败正是这个：它测的其实是无颜色分支。
/// 用这个作用域把前提显式化，两条分支各写各的测试。</summary>
internal sealed class ColourTestScope : IDisposable
{
    private readonly Func<string, string?> _env;
    private readonly Func<bool> _redirected;

    private ColourTestScope(Func<string, string?> env, Func<bool> redirected)
    {
        _env = SafeColor.ReadEnv;
        _redirected = SafeColor.IsRedirected;
        SafeColor.ReadEnv = env;
        SafeColor.IsRedirected = redirected;
    }

    /// <summary>强制颜色开启（重定向视为否、NO_COLOR 未设置）。</summary>
    public static ColourTestScope On() =>
        new(_ => null, () => false);

    /// <summary>强制颜色关闭（等价于设置了 NO_COLOR）。</summary>
    public static ColourTestScope Off() =>
        new(name => name == "NO_COLOR" ? "1" : null, () => false);

    public void Dispose()
    {
        SafeColor.ReadEnv = _env;
        SafeColor.IsRedirected = _redirected;
    }
}
