using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class EnumFlagReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-enum-" + Guid.NewGuid().ToString("N"));

    public EnumFlagReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteSource(params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, "A.cs"), string.Join("\n", lines));

    [Fact]
    public async Task Ef_FindsEnumComparedToLiteral()
    {
        WriteSource(
            "enum Status { Idle, Running }", "class A", "{",
            "    Status state = Status.Idle;",
            "    void M()", "    {", "        if (state == 1) { Go(); }", "    }", "    void Go() { }", "}");
        var result = await new EnumFlagReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("枚举/标志报告: 1 个 .cs 文件", result);
        Assert.Contains("枚举与数字字面量比较", result);
        Assert.Contains("Status.成员名", result);
    }

    [Fact]
    public async Task Ef_IntComparedToLiteralIsNotFlagged()
    {
        // 关键误报防线：count 是 int，count == 1 是正常比较
        WriteSource("class A", "{", "    int count = 0;", "    void M()", "    {", "        if (count == 1) { Go(); }", "    }", "    void Go() { }", "}");
        var result = await new EnumFlagReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("枚举与数字字面量比较", result);
    }

    [Fact]
    public async Task Ef_FindsFlagsEnumWithEquals()
    {
        WriteSource(
            "using System;", "[Flags]", "public enum Perm { None = 0, Read = 1, Write = 2 }",
            "class A", "{", "    Perm perms = Perm.None;",
            "    void M()", "    {", "        if (perms == Perm.Write) { Go(); }", "    }", "    void Go() { }", "}");
        var result = await new EnumFlagReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("[Flags] 用 == 判断", result);
        Assert.Contains("位运算", result);
    }

    [Fact]
    public async Task Ef_FlagsBitwiseIsClean()
    {
        WriteSource(
            "using System;", "[Flags]", "public enum Perm { None = 0, Read = 1, Write = 2 }",
            "class A", "{", "    Perm perms = Perm.None;",
            "    void M()", "    {", "        if ((perms & Perm.Read) != 0) { Go(); }", "    }", "    void Go() { }", "}");
        var result = await new EnumFlagReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("[Flags] 用 == 判断", result);
    }

    [Fact]
    public async Task Ef_FindsBoolComparedToTrue()
    {
        WriteSource("class A", "{", "    void M(bool enabled)", "    {", "        if (enabled == true) { Go(); }", "    }", "    void Go() { }", "}");
        var result = await new EnumFlagReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("布尔与 true/false 比较", result);
    }

    [Fact]
    public void ThePrimitiveListExcludesNumericAndBoolTypes()
    {
        Assert.Contains("int", EnumFlagReportTool.Primitives);
        Assert.Contains("bool", EnumFlagReportTool.Primitives);
        // 这些是用户自己定义的类型，不能混进原始类型表
        Assert.DoesNotContain("Status", EnumFlagReportTool.Primitives);
    }

    [Fact]
    public async Task Ef_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new EnumFlagReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new EnumFlagReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EnumFlagReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
