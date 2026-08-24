using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>FileTools(read/write/edit/list_directory)的边界测试(补充 FileToolsTests 未覆盖的场景)。</summary>
public class FileToolsEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-filetools-" + Guid.NewGuid().ToString("N"));

    public FileToolsEdgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private static AgentContext MakeContext(string dir) => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(dir),
    };

    private string PathOf(string rel) => Path.Combine(_dir, rel);

    // ===== read_file =====

    [Fact]
    public async Task ReadFile_MissingPath_Throws()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new ReadFileTool().ExecuteAsync(new JsonObject(), MakeContext(_dir), CancellationToken.None));
        Assert.Contains("path", ex.Message);
    }

    [Fact]
    public async Task ReadFile_FileNotExists_Throws()
    {
        var args = new JsonObject { ["path"] = "nope.txt" };
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new ReadFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public async Task ReadFile_EmptyFile_ReportsEmpty()
    {
        File.WriteAllText(PathOf("empty.txt"), "");
        var args = new JsonObject { ["path"] = "empty.txt" };
        var result = await new ReadFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("为空", result);
    }

    [Fact]
    public async Task ReadFile_LimitIsClampedTo5000()
    {
        // limit 超过 5000 钳制：不崩且输出行数有界
        var lines = string.Join('\n', System.Linq.Enumerable.Range(0, 6000).Select(i => $"line{i}"));
        File.WriteAllText(PathOf("many.txt"), lines);
        var args = new JsonObject { ["path"] = "many.txt", ["limit"] = 99999 };
        var result = await new ReadFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("line0", result);
        Assert.DoesNotContain("line5999", result); // 超钳制上限的行未输出
    }

    [Fact]
    public async Task ReadFile_HeaderShowsRange_WhenTruncated()
    {
        File.WriteAllText(PathOf("range.txt"), "a\nb\nc\nd\ne\n");
        var args = new JsonObject { ["path"] = "range.txt", ["limit"] = 2 };
        var result = await new ReadFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("共 5 行", result);
        Assert.Contains("已显示", result);
        Assert.DoesNotContain("\n3\te", result); // 第三行起未显示
    }

    [Fact]
    public async Task ReadFile_TooLargeFile_Throws()
    {
        // >20MB 拒绝直接读取（防撑爆内存/上下文）
        using (var fs = File.Create(PathOf("big.bin")))
            fs.SetLength(21 * 1024 * 1024);
        var args = new JsonObject { ["path"] = "big.bin" };
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new ReadFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("文件过大", ex.Message);
    }

    // ===== write_file =====

    [Fact]
    public async Task WriteFile_CreateDirsFalse_NoParent_Throws()
    {
        var args = new JsonObject { ["path"] = "no/parent/x.txt", ["content"] = "x", ["create_dirs"] = false };
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new WriteFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("父目录不存在", ex.Message); // 清晰错误，而非笼统的「写入失败」
    }

    [Fact]
    public async Task WriteFile_Overwrite_PushesUndoWithOldContent()
    {
        File.WriteAllText(PathOf("over.txt"), "old content");
        var ctx = MakeContext(_dir);
        var args = new JsonObject { ["path"] = "over.txt", ["content"] = "new content" };

        await new WriteFileTool().ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.Equal("new content", File.ReadAllText(PathOf("over.txt")));
        Assert.Equal(1, ctx.Undo.Count);

        var desc = ctx.Undo.TryUndo();
        Assert.NotNull(desc);
        Assert.Equal("old content", File.ReadAllText(PathOf("over.txt"))); // 撤销恢复旧内容
    }

    [Fact]
    public async Task WriteFile_NewFile_PushesUndoWithHadFileFalse()
    {
        var ctx = MakeContext(_dir);
        var args = new JsonObject { ["path"] = "fresh.txt", ["content"] = "hello" };

        await new WriteFileTool().ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.Equal(1, ctx.Undo.Count);

        ctx.Undo.TryUndo();
        Assert.False(File.Exists(PathOf("fresh.txt"))); // 撤销删除新建文件
    }

    [Fact]
    public async Task WriteFile_ReportsByteCount()
    {
        var args = new JsonObject { ["path"] = "bytes.txt", ["content"] = "中文内容" };
        var result = await new WriteFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("字节", result);
        Assert.Contains("bytes.txt", result);
    }

    // ===== write_file / edit_file 预览 =====

    [Fact]
    public void WritePreviewText_NewFile_ShowsScale()
    {
        var args = new JsonObject
        {
            ["path"] = Path.Combine(_dir, "brand-new.txt"),
            ["content"] = "line1\nline2\nline3",
        };
        var preview = AgentClass.WritePreviewText(args);
        Assert.Contains("新文件", preview);
        Assert.Contains("3 行", preview);
    }

    [Fact]
    public void WritePreviewText_Overwrite_ShowsDiff()
    {
        var path = PathOf("over-preview.txt");
        File.WriteAllText(path, "old line");
        var args = new JsonObject { ["path"] = path, ["content"] = "new line" };
        var preview = AgentClass.WritePreviewText(args);
        Assert.Contains("- old line", preview);
        Assert.Contains("+ new line", preview);
    }

    [Fact]
    public void WritePreviewText_IdenticalContent_NotesNoDiff()
    {
        var path = PathOf("same.txt");
        File.WriteAllText(path, "same");
        var args = new JsonObject { ["path"] = path, ["content"] = "same" };
        Assert.Contains("无差异", AgentClass.WritePreviewText(args));
    }

    // ===== edit_file =====

    [Fact]
    public async Task EditFile_MissingOldString_Throws()
    {
        File.WriteAllText(PathOf("e1.txt"), "abc");
        var args = new JsonObject { ["path"] = "e1.txt", ["new_string"] = "x" };
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new EditFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("old_string", ex.Message);
    }

    [Fact]
    public async Task EditFile_EmptyOldString_Throws()
    {
        File.WriteAllText(PathOf("e2.txt"), "abc");
        var args = new JsonObject { ["path"] = "e2.txt", ["old_string"] = "", ["new_string"] = "x" };
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new EditFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("old_string", ex.Message);
    }

    [Fact]
    public async Task EditFile_NotFound_ThrowsWithHint()
    {
        File.WriteAllText(PathOf("e3.txt"), "hello world");
        var args = new JsonObject { ["path"] = "e3.txt", ["old_string"] = "missing", ["new_string"] = "x" };
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new EditFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("未找到 old_string", ex.Message);
    }

    [Fact]
    public async Task EditFile_Duplicate_WithoutReplaceAll_Throws()
    {
        File.WriteAllText(PathOf("e4.txt"), "foo foo foo");
        var args = new JsonObject { ["path"] = "e4.txt", ["old_string"] = "foo", ["new_string"] = "bar" };
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new EditFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None));
        Assert.Contains("出现 3 次", ex.Message);
    }

    [Fact]
    public async Task EditFile_ReplaceAll_ReplacesEveryOccurrence()
    {
        File.WriteAllText(PathOf("e5.txt"), "foo foo foo");
        var args = new JsonObject { ["path"] = "e5.txt", ["old_string"] = "foo", ["new_string"] = "bar", ["replace_all"] = true };

        await new EditFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Equal("bar bar bar", File.ReadAllText(PathOf("e5.txt")));
    }

    [Fact]
    public async Task EditFile_Success_ReplacesAndPushesUndo()
    {
        File.WriteAllText(PathOf("e6.txt"), "hello old");
        var ctx = MakeContext(_dir);
        var args = new JsonObject { ["path"] = "e6.txt", ["old_string"] = "old", ["new_string"] = "new" };

        await new EditFileTool().ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.Equal("hello new", File.ReadAllText(PathOf("e6.txt")));
        Assert.Equal(1, ctx.Undo.Count);

        ctx.Undo.TryUndo();
        Assert.Equal("hello old", File.ReadAllText(PathOf("e6.txt"))); // 撤销恢复
    }

    [Fact]
    public async Task EditFile_MultilineOldString_ReplacesExactly()
    {
        File.WriteAllText(PathOf("e7.txt"), "line1\nline2\nline3\n");
        var args = new JsonObject
        {
            ["path"] = "e7.txt",
            ["old_string"] = "line1\nline2",
            ["new_string"] = "ONE\nTWO",
        };

        await new EditFileTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Equal("ONE\nTWO\nline3\n", File.ReadAllText(PathOf("e7.txt")));
    }
}
