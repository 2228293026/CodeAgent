using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/status</c> 总览面板该包含什么。
///
/// 它汇总了模式、模型、工作区、上下文、会话、构建——但没有**改动面**和**任务进度**。
/// 用户想知道「这轮动了哪些文件」「模型还有没有事没做完」，得再敲一次
/// <c>/files</c> 和 <c>/tasks</c>。总览的意义就是省掉这几次往返。
///
/// 只加**数量**、不铺开列表：总览是拿来一眼扫的，
/// 塞进 20 条路径会把面板撑成一屏，那就不是总览了。
/// </summary>
public class StatusRowTests
{
    private static List<(string Key, string Value)> Rows(
        int changedFiles = 0, int taskDone = 0, int taskTotal = 0) =>
        Program.BuildStatusRows(
            "code", "m1", "p1", "C:/w", "main",
            1000, 128000, 3, 5, TimeSpan.FromMinutes(1), "1.0", "abc1234",
            changedFiles, taskDone, taskTotal);

    [Fact]
    public void BasicFieldsAreAlwaysThere()
    {
        var keys = Rows().Select(r => r.Key).ToList();
        Assert.Contains("模式", keys);
        Assert.Contains("模型", keys);
        Assert.Contains("工作区", keys);
        Assert.Contains("上下文", keys);
        Assert.Contains("会话", keys);
        Assert.Contains("构建", keys);
    }

    [Fact]
    public void ChangedFilesAppearOnlyWhenSomethingChanged()
    {
        // 没改过文件时不该出现「改动 0 个文件」——那是在报一个不存在的成绩
        Assert.DoesNotContain(Rows().Select(r => r.Key), k => k == "改动");
        var withChange = Rows(changedFiles: 3);
        var row = withChange.First(r => r.Key == "改动");
        Assert.Contains("3", row.Value);
        Assert.Contains("/files", row.Value); // 要看详情去哪儿
    }

    [Fact]
    public void TaskProgressAppearsOnlyWhenThereAreTasks()
    {
        Assert.DoesNotContain(Rows().Select(r => r.Key), k => k == "任务");
        var withTasks = Rows(taskDone: 2, taskTotal: 5);
        var row = withTasks.First(r => r.Key == "任务");
        Assert.Contains("2/5", row.Value);
        Assert.Contains("/tasks", row.Value);
    }

    [Fact]
    public void TheTwoNewRowsDoNotPushOutTheBasics()
    {
        // 加行不能把原有的挤掉：/status 的价值全在那几项固定的
        var keys = Rows(changedFiles: 3, taskDone: 2, taskTotal: 5).Select(r => r.Key).ToList();
        foreach (var must in new[] { "模式", "模型", "工作区", "上下文", "会话", "构建" })
            Assert.Contains(must, keys);
    }

    [Fact]
    public void ZeroTasksMeansNoRowEvenIfChangedFilesExist()
    {
        // 两个可选行的条件互相独立：改过文件不等于有任务清单
        var keys = Rows(changedFiles: 2, taskDone: 0, taskTotal: 0).Select(r => r.Key).ToList();
        Assert.Contains("改动", keys);
        Assert.DoesNotContain("任务", keys);
    }
}
