using System;
using System.Diagnostics;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 守住 <see cref="GitTestEnvironment"/> 真的装上了。
///
/// 那段初始化写在 <c>[ModuleInitializer]</c> 里、外面包着 <c>try/catch</c>：
/// 装不上时它安静退化成「继承机器配置」，测试照旧全绿——
/// 直到某台机器上有个签名器或分页器把整轮测试挂住，而现场什么线索都没有。
///
/// 所以这里不问「代码有没有调过 SetEnvironmentVariable」，
/// 而是起一个真的 git 子进程问它看到了什么：
/// 路径写错、文件带 BOM、键名拼错，都只会在子进程里现形。
/// </summary>
public class GitTestEnvironmentTests
{
    [Fact]
    public void TheIsolatedGlobalConfigIsWhatGitActuallySees()
    {
        // --global 读的正是 GIT_CONFIG_GLOBAL 指向的那一层，与仓库 local 配置无关：
        // GitGpgStatusReportToolTests 里写 local commit.gpgsign=true 的断言不受影响。
        Assert.Equal("false", GitConfig("commit.gpgsign"));
        Assert.Equal("false", GitConfig("tag.gpgsign"));
        Assert.Equal("false", GitConfig("log.showSignature"));
    }

    [Fact]
    public void TheConfigCarriesAnIdentitySoCommitsNeedNoHostSetup()
    {
        // 断言隔离层写入的确切值，而不是「非空」：开发机上本来就有真实的
        // user.name/user.email，只判非空的话，隔离被拆掉后它们照样能被继承到，
        // 测试虚假通过。.invalid 是保留域名，不可能来自任何人的机器配置。
        Assert.Equal("CodeAgent Tests", GitConfig("user.name"));
        Assert.Equal("tests@example.invalid", GitConfig("user.email"));
    }

    [Fact]
    public void CredentialPromptsFailInsteadOfWaitingForInput()
    {
        // 在空输入上等待是挂起，不是失败：CI 上没人会来敲这一行。
        Assert.Equal("0", Environment.GetEnvironmentVariable("GIT_TERMINAL_PROMPT"));
    }

    /// <summary>
    /// 起一个真的 git 子进程问值。子进程继承来的环境、配置文件编码、键名拼写
    /// 只要有一处不对，这里就拿不到 <c>false</c>，而是拿到空值或机器上的真值。
    /// </summary>
    private static string? GitConfig(string key)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add("--global");
        psi.ArgumentList.Add("--get");
        psi.ArgumentList.Add(key);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("起不了 git 进程");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15000), $"git config {key} 没在 15 秒内返回");
        // 0 = 有这个键；1 = 没有。其余退出码说明 git 本身没跑起来，别把失败当「没配置」。
        Assert.True(process.ExitCode is 0 or 1, $"git config {key} 失败：{stderr}");

        var value = stdout.Trim();
        return value.Length > 0 ? value : null;
    }
}
