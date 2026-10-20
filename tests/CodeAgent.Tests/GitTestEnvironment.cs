using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace CodeAgent.Tests;

/// <summary>
/// 测试进程级的 git 隔离环境。
///
/// 背景：测试会在临时仓库里真正执行 <c>git commit</c> / <c>git tag</c> / <c>git log</c>，
/// 而 <c>GitStatusTool.RunGitAsync</c> 起的子进程会完整继承开发机的用户级 gitconfig。
/// 机器上任何一个开关都可能让子进程去调起外部程序或等待输入：
/// <c>commit.gpgsign</c> 找签名器、<c>log.showSignature</c> 逐条验签、
/// <c>core.pager</c> 等分页器、<c>core.fsmonitor</c> 起守护进程、凭据提示等 stdin。
/// 症状是整轮测试卡在某个 Git* 用例上、最后靠 hang dump 收尾，且与被测代码无关。
///
/// 因此这里不做“某个开关必然导致挂起”的断言——具体卡在哪个系统调用上，
/// 从 CI 日志里是看不出来的。要的是把整层机器配置换成确定性的，
/// 而不是逐个枚举可疑开关：只要子进程继承的是这份最小配置，
/// 机器上装了什么签名器、分页器、钩子都不再影响测试。
///
/// 处理方式是给测试进程换一份“干净”的用户级配置：
/// <list type="bullet">
/// <item>写入 <c>gpgsign = false</c>（commit / tag），不触发签名交互；</item>
/// <item>写入 <c>log.showSignature = false</c>，不让 <c>git log</c> 为每条提交
/// 去调签名后端（命令行显式 <c>--show-signature</c> 优先级更高，不受影响）；</item>
/// <item>写入固定的测试身份，使未显式传 <c>-c user.name</c> 的提交依然可用
/// （CI 上没有用户级配置时更稳定）；</item>
/// <item>通过 <c>GIT_CONFIG_GLOBAL</c> 只替换“用户级”这一层。
/// 系统级（如 <c>init.defaultBranch</c>）与仓库本地配置的优先级不受影响，
/// 因此测试里那些显式写入 local <c>commit.gpgsign = true</c> 的断言仍然成立。</item>
/// <item>顺手设 <c>GIT_TERMINAL_PROMPT=0</c>：万一还有别的路径要凭据，直接失败而不是等输入。</item>
/// </list>
///
/// 这里刻意不使用 <c>GIT_CONFIG_COUNT</c> / <c>git -c</c>：它们属于命令级作用域，
/// 优先级高于仓库本地配置，会把测试自己写的 local <c>gpgsign</c> 一并压掉。
/// </summary>
internal static class GitTestEnvironment
{
    private static string? _configPath;

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            // 按进程号隔离：并行的多个 testhost 各写各的，互不覆盖。
            var path = Path.Combine(
                Path.GetTempPath(),
                $"codeagent-tests-global-gitconfig-{Environment.ProcessId}");

            var content = new StringBuilder()
                .AppendLine("[commit]")
                .AppendLine("\tgpgsign = false")
                .AppendLine("[tag]")
                .AppendLine("\tgpgsign = false")
                .AppendLine("[log]")
                .AppendLine("\tshowSignature = false")
                .AppendLine("[user]")
                .AppendLine("\tname = CodeAgent Tests")
                .AppendLine("\temail = tests@example.invalid")
                .ToString();

            // git 不接受带 BOM 的配置文件，必须用 UTF8Encoding(false) 写出。
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            _configPath = path;
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", path);
            // 任何遗漏的凭据提示都不要等输入，直接失败。
            Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");

            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Cleanup();
        }
        catch
        {
            // 隔离环境搭不起来时退化为使用机器配置，
            // 绝不能让模块初始化异常拖垮整个测试程序集的加载。
        }
    }

    private static void Cleanup()
    {
        var path = _configPath;
        if (path is null)
            return;
        _configPath = null;
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 退出路径上无处上报，残留一个几字节的临时文件无害。
        }
    }
}
