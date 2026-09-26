using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 需要捕获控制台输出的测试类共用的 xUnit 集合。
///
/// 为什么必须有：<c>Console.SetOut</c> 是**进程全局**的，而 xUnit 默认并行跑不同类。
/// 两个类同时捕获输出时，A 设的 writer 会被 B 抢走（反之亦然），
/// 表现为「某个测试随机地拿到空字符串」——本地可能过，CI 上换一批就挂。
/// 把它们收进同一个集合，xUnit 会保证串行执行。
///
/// 加入本集合的前提：类里**只**通过 <c>Console.SetOut</c> 捕获输出。
/// </summary>
[CollectionDefinition("ConsoleOutput", DisableParallelization = true)]
public class ConsoleOutputCollection
{
}
