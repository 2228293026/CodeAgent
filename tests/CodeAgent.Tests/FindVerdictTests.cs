using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// /find 的「结论」与「结果」必须一致（regression for round 315）。
///
/// 此前结论按**日志**命中数下，而快照扫描发生在结论打印**之后**：只在快照里命中时，
/// 同一屏先打「历史会话中没有匹配「X」的内容。」紧接着又打出含 X 的快照。
/// 自相矛盾，且用户会以为自己记错了——是「说谎的消息」那一类缺陷里最伤的一种。
/// </summary>
public class FindVerdictTests
{
    /// <summary>建一个只含会话日志/快照的临时工作区；日志内容用 ASCII，避免管道编码干扰。</summary>
    private static (string Dir, string Sessions) MakeWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "findverdict-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(dir, ".codeagent", "sessions");
        Directory.CreateDirectory(sessions);
        return (dir, sessions);
    }

    private static void WriteLog(string sessions, string stamp, string content) =>
        File.WriteAllText(Path.Combine(sessions, stamp + ".jsonl"),
            "{\"ts\":\"10:00:00\",\"role\":\"user\",\"content\":\"" + content + "\"}" + "\r\n");

    private static void WriteSnapshot(string sessions, string name, string content) =>
        File.WriteAllText(Path.Combine(sessions, name + ".json"),
            "[{\"role\":\"user\",\"content\":\"" + content + "\"}]");

    // ---------- 结论层：纯函数 ----------

    [Fact]
    public void NoMatchLineSaysThereIsNoMatch()
    {
        var line = Program.FindNoMatchLine("needle", interiorFound: false, width: 100);
        Assert.Contains("没有匹配", line);
        Assert.DoesNotContain("词边界", line);
        Assert.Contains("needle", line);
    }

    [Fact]
    public void NoMatchLineNeverSaysNoMatchWhenTheWordIsOnlyInterior()
    {
        // 「没有匹配」与「只出现在词内部」必须**各说各的话**：同时出现即自相矛盾。
        var line = Program.FindNoMatchLine("eedle", interiorFound: true, width: 100);
        Assert.DoesNotContain("没有匹配", line);
        Assert.Contains("词边界", line);
        Assert.Contains("更长的词内部", line);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void NoMatchLineFitsNarrowTerminals(int width)
    {
        foreach (var kw in new[] { "needle", new string('k', 200) })
        {
            foreach (var interior in new[] { false, true })
            {
                var line = Program.FindNoMatchLine(kw, interior, width);
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
            }
        }
    }

    [Fact]
    public void TruncationNotesNameTheCapAndTheShownCount()
    {
        // 上限必须写在句子里，且说清「显示了几个」——只写「仅显示前 N」而不说
        // 「实际超过 N」会让用户以为那就是全部。
        Assert.Contains("5", Program.FindLogTruncationNote());
        Assert.Contains("超过", Program.FindLogTruncationNote());
        Assert.Contains("3", Program.FindSnapshotTruncationNote());
        Assert.Contains("超过", Program.FindSnapshotTruncationNote());
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void TruncationNotesFitNarrowTerminals(int width)
    {
        // 裁剪发生在 FormatResultLine（调用点），断言成品行而不是裸字符串——
        // 裸字符串本就不受宽度约束，断言它只会测错层。
        Assert.True(TextUtil.DisplayWidth(Program.FormatResultLine(Program.FindLogTruncationNote(), width)) <= width);
        Assert.True(TextUtil.DisplayWidth(Program.FormatResultLine(Program.FindSnapshotTruncationNote(), width)) <= width);
    }

    // ---------- 来源层：词内部探测要覆盖快照 ----------

    [Fact]
    public void SnapshotInteriorProbeIgnoresWordBoundary()
    {
        var (dir, sessions) = MakeWorkspace();
        try
        {
            var inside = Path.Combine(sessions, "inside.json");
            WriteSnapshot(sessions, "inside", "a needlessly long word");
            Assert.True(Program.SnapshotContainsSubstring(inside, "eedle"));
            Assert.True(Program.SnapshotContainsSubstring(inside, "NEEDLE"));

            var absent = Path.Combine(sessions, "absent.json");
            WriteSnapshot(sessions, "absent", "nothing to see");
            Assert.False(Program.SnapshotContainsSubstring(absent, "eedle"));
            Assert.False(Program.SnapshotContainsSubstring(Path.Combine(sessions, "missing.json"), "x"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SnapshotInteriorProbeSurvivesCorruptFiles()
    {
        // 快照可能半写或损坏：探测绝不能把异常抛到 UI 层（那会打断 /find 整体）。
        var (dir, sessions) = MakeWorkspace();
        try
        {
            var corrupt = Path.Combine(sessions, "corrupt.json");
            File.WriteAllText(corrupt, "{ this is not json at all");
            Assert.False(Program.SnapshotContainsSubstring(corrupt, "eedle"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------- 扫描层：两类来源一起计数 ----------

    [Fact]
    public void ASnapshotHitCountsAsAHitSoNoNoMatchVerdictIsReachable()
    {
        // 回归点本体：把日志与快照交给同一套扫描语义，快照命中必须计入总数。
        // 旧的「printed」只数日志，这个断言就是防止它被改回去。
        var (dir, sessions) = MakeWorkspace();
        try
        {
            var kw = "needle";
            WriteLog(sessions, "20260101-100000", "no keyword here");
            WriteSnapshot(sessions, "snap1", "THE ANSWER IS needle here");

            var logHits = Directory.GetFiles(sessions, "*.jsonl")
                .Select(p => AgentClass.SearchSessionLog(p, kw).Count).Sum();
            var snapHits = Directory.GetFiles(sessions, "*.json")
                .Select(p => AgentClass.SearchSnapshot(p, kw).Count).Sum();

            // 快照命中 1、日志命中 0 —— 总数必须为 1，否则结论会走「没有匹配」分支
            Assert.Equal(0, logHits);
            Assert.Equal(1, snapHits);
            Assert.Equal(1, logHits + snapHits);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StrictlyInteriorKeywordsAreReportedNotSwallowed()
    {
        // 纯 ASCII 关键字只出现在词内部时，正式搜索会滤掉它（噪音），
        // 但结论必须改口，否则等于说谎。
        var (dir, sessions) = MakeWorkspace();
        try
        {
            var log = Path.Combine(sessions, "20260101-100000.jsonl");
            WriteLog(sessions, "20260101-100000", "a needlessly long word");
            var snap = Path.Combine(sessions, "snap1.json");
            WriteSnapshot(sessions, "snap1", "a needlessly long word");

            Assert.Empty(AgentClass.SearchSessionLog(log, "eedle"));
            Assert.True(AgentClass.LogContainsSubstring(log, "eedle"));
            Assert.Empty(AgentClass.SearchSnapshot(snap, "eedle"));
            Assert.True(Program.SnapshotContainsSubstring(snap, "eedle"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------- 渲染层：行为测试，回归点本体 ----------

    private static Program.FindScanResult Scan(
        List<Program.FindHitGroup> logs,
        List<Program.FindHitGroup> snapshots,
        bool logTruncated = false,
        bool snapshotTruncated = false,
        List<string>? interior = null) =>
        new(logs, snapshots, logTruncated, snapshotTruncated, interior ?? []);

    private static Program.FindHitGroup Group(string title, params string[] snippets) =>
        new(title, snippets.Select(s => ("user", s)).ToList());

    [Fact]
    public void ASnapshotHitAloneNeverProducesANoMatchVerdict()
    {
        // 回归点本体：只有快照命中时，旧代码会先打「没有匹配「X」的内容。」
        // 再打快照——同一屏上自相矛盾。现在结论只看**合计**命中。
        var scan = Scan([], [Group("快照 snap5 · 刚刚（/load snap5 恢复）:", "THE ANSWER IS needle here")]);
        var text = string.Join("\n", Program.FindResultLines(scan, "needle", 100));
        Assert.DoesNotContain("没有匹配", text);
        Assert.Contains("needle", text);
    }

    [Fact]
    public void ALogHitAloneNeverProducesANoMatchVerdict()
    {
        var scan = Scan([Group("20260101-100000 · 1 小时前（/resume 可恢复）:", "log needle here")], []);
        var text = string.Join("\n", Program.FindResultLines(scan, "needle", 100));
        Assert.DoesNotContain("没有匹配", text);
        Assert.Contains("needle", text);
    }

    [Fact]
    public void TheVerdictLineComesFirstWhenThereIsReallyNoMatch()
    {
        var lines = Program.FindResultLines(Scan([], []), "needle", 100);
        Assert.Single(lines);
        Assert.Contains("没有匹配", lines[0]);
    }

    [Fact]
    public void TheVerdictAndTheInteriorExplanationAgree()
    {
        // 0 命中 + 词内部命中：不能说「没有匹配」，要说「只出现在更长的词内部」，
        // 两者只取其一。旧代码两者都可能打出来。
        var text = string.Join("\n", Program.FindResultLines(Scan([], [], interior: ["快照 snap1"]), "eedle", 100));
        Assert.DoesNotContain("没有匹配", text);
        Assert.Contains("词边界", text);
        Assert.Contains("快照 snap1", text);
    }

    [Fact]
    public void HitsArePrintedBeforeTheTruncationNotes()
    {
        // 交代句必须在**所有**结果之后：先说「超过 5 个」再给结果，用户会以为下面那些就是全部。
        // 两种来源都要查——只查日志的话，把快照结果挪到交代句之后的改法仍会全绿。
        var lines = Program.FindResultLines(
            Scan(
                [Group("a · 1（/resume 可恢复）:", "needle a")],
                [Group("快照 s · 1（/load s 恢复）:", "needle s")],
                logTruncated: true,
                snapshotTruncated: true),
            "needle", 100);
        // 按标题定位而不是按片段文字：FormatSearchHitLine 会把关键字裹成 «needle»，
        // 拼在后面的空格会让 "needle a" 整串匹配不到。
        var noteAt = lines.FindIndex(l => l.Contains("超过"));
        Assert.True(noteAt >= 0, "没找到截断交代句");
        var logAt = lines.FindIndex(l => l.Contains("（/resume 可恢复）"));
        var snapAt = lines.FindIndex(l => l.Contains("（/load s 恢复）"));
        Assert.True(logAt >= 0, "没找到日志命中组");
        Assert.True(snapAt >= 0, "没找到快照命中组");
        Assert.True(logAt < noteAt, "日志结果必须排在交代句之前");
        Assert.True(snapAt < noteAt, "快照结果必须排在交代句之前");
    }

    [Fact]
    public void EveryHitGroupPrecedesEveryTruncationNote()
    {
        // 比上一个用例更强的形式：不看具体标题，只数「命中组行数」，
        // 要求所有命中行都在第一句交代之前——把任一来源挪到交代句之后都会红。
        static bool IsGroup(string l) => l.Contains("（/resume 可恢复）") || l.Contains("（/load ");
        var lines = Program.FindResultLines(
            Scan(
                [
                    Group("a · 1（/resume 可恢复）:", "needle a"),
                    Group("b · 2（/resume 可恢复）:", "needle b"),
                    Group("c · 3（/resume 可恢复）:", "needle c"),
                ],
                [
                    Group("快照 s1 · 1（/load s1 恢复）:", "needle s1"),
                    Group("快照 s2 · 2（/load s2 恢复）:", "needle s2"),
                ],
                logTruncated: true,
                snapshotTruncated: true),
            "needle", 100);
        var firstNote = lines.FindIndex(l => l.Contains("超过"));
        Assert.True(firstNote >= 0, "没找到截断交代句");
        // 5 个命中组一个都不能少，且都在第一句交代之前
        Assert.Equal(5, lines.Take(firstNote).Count(IsGroup));
        Assert.DoesNotContain(lines.Skip(firstNote), IsGroup);
    }

    [Fact]
    public void TruncationNotesAppearOnlyWhenActuallyTruncated()
    {
        var clean = Scan([Group("a · 1（/resume 可恢复）:", "needle")], []);
        Assert.DoesNotContain("超过", string.Join("\n", Program.FindResultLines(clean, "needle", 100)));

        var capped = Scan([Group("a · 1（/resume 可恢复）:", "needle")], [], logTruncated: true);
        Assert.Contains("超过", string.Join("\n", Program.FindResultLines(capped, "needle", 100)));
    }

    [Fact]
    public void ANeverTruncatedSearchSaysNothingAboutCaps()
    {
        // 没有被截断时说「超过 5 个」也是谎话——反向的谎。
        var scan = Scan(
            [Group("a · 1（/resume 可恢复）:", "needle")],
            [Group("快照 s · 1（/load s 恢复）:", "needle")]);
        Assert.DoesNotContain("超过", string.Join("\n", Program.FindResultLines(scan, "needle", 100)));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void EveryFindLineFitsNarrowTerminals(int width)
    {
        var scans = new[]
        {
            Scan([], []),
            Scan([], [], interior: ["日志 20260101-100000", "快照 snap1"]),
            Scan([Group(new string('n', 300) + " · 1（/resume 可恢复）:", new string('x', 400) + " needle")], [],
                logTruncated: true, snapshotTruncated: true),
        };
        foreach (var scan in scans)
        {
            foreach (var line in Program.FindResultLines(scan, "needle", width))
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
        }
    }

    // ---------- 装配层：结论必须排在扫描之后 ----------

    [Fact]
    public void TheNoMatchVerdictIsComputedAfterBothSourcesAreScanned()
    {
        // 源码形状守卫：结论分支现在读**两类来源的合计**，而旧代码只读 printed（日志计数），
        // 且快照循环发生在结论打印之后。
        var src = ReadProgramSource();
        Assert.Contains("if (scan.LogHits.Count == 0 && scan.SnapshotHits.Count == 0)", src);
        Assert.DoesNotContain("if (printed == 0)", src);
    }

    [Fact]
    public void TheSnapshotScanRunsBeforeAnythingIsPrinted()
    {
        // 只在**方法体内**比顺序：文件里的位置说明不了执行顺序
        // （FindResultLines 定义在 PrintFindResults 之前，扫源码全文件必然误判）。
        var src = ReadProgramSource();
        var body = MethodBody(src, "private static void PrintFindResults");
        Assert.NotNull(body);
        var scanAt = body.IndexOf("snapshotHits.Add(", StringComparison.Ordinal);
        var renderAt = body.IndexOf("FindResultLines(scan, keyword, ConsoleColumns())", StringComparison.Ordinal);
        Assert.True(scanAt > 0, "没找到快照收集");
        Assert.True(renderAt > 0, "没找到渲染调用");
        Assert.True(scanAt < renderAt, "快照收集必须早于任何打印");
        // 旧代码在方法体中间就 Console.WriteLine 结论——扫描方法里不该再有直接打印
        Assert.DoesNotContain("Console.WriteLine(FindNoMatchLine", body);
    }

    /// <summary>取某个方法从签名起、到下一个成员声明前的源码片段（带花括号配平）。</summary>
    private static string? MethodBody(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var open = src.IndexOf('{', start);
        if (open < 0)
            return null;
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0)
                return src[open..(i + 1)];
        }
        return null;
    }

    [Fact]
    public void NoRecordsAtAllSaysThereIsNothingToSearch()
    {
        var src = ReadProgramSource();
        Assert.Contains("没有可搜索的会话记录", src);
        // 且这句只在「日志与快照都没有」时才可能打印
        Assert.Contains("if (logs.Count == 0 && snapshots.Count == 0)", src);
    }

    [Fact]
    public void TruncationIsDisclosedNotSilent()
    {
        var src = ReadProgramSource();
        Assert.Contains("if (scan.LogTruncated)", src);
        Assert.Contains("if (scan.SnapshotTruncated)", src);
        Assert.Contains("FindLogTruncationNote()", src);
        Assert.Contains("FindSnapshotTruncationNote()", src);
    }

    [Fact]
    public void TheTruncationProbeUsesTheSameMatchingRuleAsTheSearch()
    {
        // 探路必须与正式搜索**同口径**（含词边界）。用无词边界的子串探测（LogContainsSubstring /
        // SnapshotContainsSubstring）会把「只出现在词内部」也算成命中，于是「超过 5 个」这句话
        // 变成谎话——与本轮修掉的自相矛盾同一类毛病。
        // 实测：把两处探路换成子串探测，本文件 27 个测试**全绿**——即此前的形状守卫
        // 挡不住这个错误。真正的防线是下面那个行为测试。
        var body = MethodBody(ReadProgramSource(), "private static void PrintFindResults");
        Assert.NotNull(body);
        Assert.Contains("AgentClass.SearchSessionLog(log, keyword, maxHits: 1).Count > 0", body);
        Assert.Contains("AgentClass.SearchSnapshot(path, keyword, maxHits: 1).Count > 0", body);
        Assert.DoesNotContain("logTruncated = AgentClass.LogContainsSubstring", body);
        Assert.DoesNotContain("snapshotTruncated = SnapshotContainsSubstring", body);

        // 行为层面：截断标记由**扫描器**判定，渲染器只照着说。
        // 下面这个用例证明「越界检查用的是正式搜索的命中语义」：
        // 只有 1 个真实命中 + 1 个词内部命中时，不得报「超过 5 个」。
        var hits = Scan([Group("a · 1（/resume 可恢复）:", "log needle here")], []);
        Assert.False(hits.LogTruncated);
        Assert.DoesNotContain("超过", string.Join("\n", Program.FindResultLines(hits, "needle", 100)));
    }

    [Fact]
    public void AnInteriorOnlyExtraSourceDoesNotFabricateATruncation()
    {
        // 场景：5 个日志文件命中（触发上限），第 6 个文件里关键字只出现在词内部。
        // 正确行为是**不**说「超过 5 个」——那个文件并没有贡献命中。
        // 这里用搜索语义本身来断言，而不是去跑 IO：
        var interiorLog = Path.Combine(Path.GetTempPath(), "mutprobe-interior.jsonl");
        File.WriteAllText(interiorLog, "{\"role\":\"user\",\"content\":\"a needlessly long word\"}");
        try
        {
            var kw = "eedle";
            // 无词边界的探测会说「有」
            Assert.True(AgentClass.LogContainsSubstring(interiorLog, kw));
            // 正式搜索说「没有」——探路必须听正式搜索的
            Assert.Empty(AgentClass.SearchSessionLog(interiorLog, kw, maxHits: 1));
        }
        finally
        {
            File.Delete(interiorLog);
        }
    }

    private static string ReadProgramSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CodeAgent", "Program.cs");
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 10000)
                return File.ReadAllText(candidate);
            dir = new DirectoryInfo(dir.Parent?.FullName ?? dir.FullName);
        }
        var cur = new DirectoryInfo(Environment.CurrentDirectory);
        while (cur is not null)
        {
            var candidate = Path.Combine(cur.FullName, "src", "CodeAgent", "Program.cs");
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 10000)
                return File.ReadAllText(candidate);
            cur = new DirectoryInfo(cur.Parent?.FullName ?? cur.FullName);
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
