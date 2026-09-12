using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class ApplyPatchToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-applypatch-" + Guid.NewGuid().ToString("N"));

    public ApplyPatchToolTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AgentContext MakeContext() => new() { Config = new AgentConfig(), Workspace = new Workspace(_dir) };

    private async Task<string> Apply(string patch, string? path = null, bool validateOnly = false, bool allowNewFile = false, bool allowEmpty = false, bool generous = false, bool dryRun = false, bool backup = false)
    {
        var tool = new ApplyPatchTool();
        var ctx = MakeContext();
        var args = new JsonObject { ["patch"] = patch };
        if (path is not null) args["path"] = path;
        if (validateOnly) args["validate_only"] = true;
        if (allowNewFile) args["allow_new_file"] = true;
        if (allowEmpty) args["allow_empty"] = true;
        if (generous) args["generous"] = true;
        if (dryRun) args["dry_run"] = true;
        if (backup) args["backup"] = true;
        return await tool.ExecuteAsync(args, ctx, CancellationToken.None);
    }

    [Fact]
    public async Task Apply_MultiFile_SecondFileFails_LeavesFirstFileUnmodified()
    {
        // 多文件补丁按顺序逐个写盘：第 2 个文件失败时第 1 个文件已经改完，
        // 而报错文案是「未做任何修改」——工作区被留在半应用状态，与承诺不符
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "alpha\nbeta\n");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "one\ntwo\n");
        var patch =
            "--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1,2 @@\n alpha\n-beta\n+BETA\n" +
            "--- a/b.txt\n+++ b/b.txt\n@@ -1,2 +1,2 @@\n one\n-NOPE\n+X\n";

        await Assert.ThrowsAsync<ToolException>(() => Apply(patch));

        Assert.Equal("alpha\nbeta\n", File.ReadAllText(Path.Combine(_dir, "a.txt")));
    }

    [Fact]
    public async Task Apply_SimpleLineReplacement_UpdatesFile()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "alpha\nbeta\ngamma\n");
        var patch = "@@ -1,3 +1,3 @@\n alpha\n-beta\n+BETA\n gamma\n";

        var output = await Apply(patch, "f.txt");

        Assert.Contains("已应用 1 个 hunk", output);
        Assert.Equal("alpha\nBETA\ngamma\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_AddAndRemoveLines_Works()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "a\nb\nc\n");
        // 删除第 2 行,同时在末尾新增 d
        var patch = "@@ -1,3 +1,3 @@\n a\n-b\n c\n+d\n";

        await Apply(patch, "f.txt");

        Assert.Equal("a\nc\nd\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_SingleHunk_LineDrift_KeepsSkippedLines()
    {
        // 单 hunk 的行号漂移容错会从 pos 往后找首个匹配行并把 pos 推过去，
        // 但中间被跳过的行既没写进 result 也没被消费 → 这些行从文件里静默消失
        File.WriteAllText(Path.Combine(_dir, "drift.txt"), "A\nB\nC\nD\nE\n");
        // oldStart 声称在第 1 行，实际目标 'D' 在第 4 行：触发漂移容错
        var patch = "@@ -1,1 +1,1 @@\n-D\n+X\n";

        await Apply(patch, "drift.txt");

        // 被跳过的 A/B/C 必须原样保留，只把 D 换成 X
        Assert.Equal("A\nB\nC\nX\nE\n", File.ReadAllText(Path.Combine(_dir, "drift.txt")));
    }

    [Fact]
    public async Task Apply_LargeFile_DoesNotClaimUndoItCannotPerform()
    {
        // >4MB 的文件不记录完整原文。此前 apply_patch 把 OldText 与 NewText 都设成整份原文，
        // 撤销时的 Replace(NewText, OldText) 等于 Replace(原文, 原文)——静默无操作，
        // 却仍报告「已撤销 edit_file」，用户以为改动已回滚，实际文件没变
        var big = "TARGET\n" + new string('x', 4 * 1024 * 1024) + "\n";
        File.WriteAllText(Path.Combine(_dir, "big.txt"), big);

        var tool = new ApplyPatchTool();
        var ctx = MakeContext();
        var result = await tool.ExecuteAsync(
            new JsonObject { ["patch"] = "@@ -1,1 +1,1 @@\n-TARGET\n+CHANGED\n", ["path"] = "big.txt" },
            ctx, CancellationToken.None);

        Assert.StartsWith("CHANGED\n", File.ReadAllText(Path.Combine(_dir, "big.txt")));
        Assert.Contains("未记录撤销", result);   // 如实告知无法撤销
        Assert.Equal(0, ctx.Undo.Count);         // 不入栈，避免谎报「已撤销」
    }

    [Fact]
    public async Task Apply_MultipleHunks_SingleFile()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "l1\nl2\nl3\nl4\nl5\n");
        var patch = "@@ -1,1 +1,1 @@\n-l1\n+X1\n@@ -5,1 +5,1 @@\n-l5\n+X5\n";

        await Apply(patch, "f.txt");

        Assert.Equal("X1\nl2\nl3\nl4\nX5\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_FileHeader_PicksPathFromPatch()
    {
        File.WriteAllText(Path.Combine(_dir, "x.txt"), "old\n");
        var patch = "--- a/x.txt\n+++ b/x.txt\n@@ -1 +1 @@\n-old\n+new\n";

        var output = await Apply(patch); // 不传 path,靠 +++ b/x.txt

        Assert.Contains("x.txt", output);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(_dir, "x.txt")));
    }

    [Fact]
    public async Task Apply_MultipleFiles_InOnePatch()
    {
        File.WriteAllText(Path.Combine(_dir, "m1.txt"), "A\n");
        File.WriteAllText(Path.Combine(_dir, "m2.txt"), "B\n");
        var patch = "+++ b/m1.txt\n@@ -1 +1 @@\n-A\n+A1\n" +
                    "+++ b/m2.txt\n@@ -1 +1 @@\n-B\n+B1\n";

        var output = await Apply(patch);

        Assert.Contains("m1.txt", output);
        Assert.Contains("m2.txt", output);
        Assert.Equal("A1\n", File.ReadAllText(Path.Combine(_dir, "m1.txt")));
        Assert.Equal("B1\n", File.ReadAllText(Path.Combine(_dir, "m2.txt")));
    }

    [Fact]
    public async Task Apply_ContextMismatch_ThrowsAndLeavesFileUntouched()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "alpha\nbeta\ngamma\n");
        var patch = "@@ -1,3 +1,3 @@\n alpha\n-WRONG\n gamma\n";

        var ex = await Assert.ThrowsAsync<ToolException>(() => Apply(patch, "f.txt"));

        Assert.Contains("不匹配", ex.Message);
        Assert.Equal("alpha\nbeta\ngamma\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_ValidateOnly_DoesNotWrite()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "alpha\nbeta\n");
        var patch = "@@ -1,2 +1,2 @@\n alpha\n-beta\n+XX\n";

        var output = await Apply(patch, "f.txt", validateOnly: true);

        Assert.Contains("验证通过", output);
        Assert.Contains("未写盘", output);
        Assert.Equal("alpha\nbeta\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_EmptyPatch_Throws()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => Apply("   "));
        Assert.Contains("patch", ex.Message);
    }

    [Fact]
    public async Task Apply_CrlfFile_KeepsCrlf()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "l1\r\nl2\r\nl3\r\n");
        var patch = "@@ -1,3 +1,3 @@\n l1\n-l2\n+N2\n l3\n";

        await Apply(patch, "f.txt");

        Assert.Equal("l1\r\nN2\r\nl3\r\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_NoLeadingNewlineOnNewHunk_PreservesEnding()
    {
        // 原文件以换行结尾:应用后仍保持带尾换行(避免把文件改得不带换行)
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "a\nb\n");
        var patch = "@@ -1,2 +1,2 @@\n a\n-b\n+B\n";

        await Apply(patch, "f.txt");

        Assert.Equal("a\nB\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_AllowNewFile_CreatesFileWhenTargetMissing()
    {
        // allow_new_file=true:补丁可创建新文件(常用于模型首次生成文件)
        var patch = "@@ -0,0 +1,2 @@\n+line1\n+line2\n";

        await Apply(patch, "new.txt", allowNewFile: true);

        Assert.Equal("line1\nline2", File.ReadAllText(Path.Combine(_dir, "new.txt")));
    }

    [Fact]
    public async Task Apply_AllowNewFile_ValidateOnly_ReportsWithoutWriting()
    {
        // allow_new_file + validate_only:报告将创建文件，但不实际写盘
        var patch = "@@ -0,0 +1,2 @@\n+line1\n+line2\n";

        var result = await Apply(patch, "new.txt", allowNewFile: true, validateOnly: true);

        Assert.Contains("验证通过(新建)", result);
        Assert.False(File.Exists(Path.Combine(_dir, "new.txt")));
    }

    [Fact]
    public async Task Apply_AllowNewFile_ExistingTarget_EditsFile()
    {
        // allow_new_file=true 但目标已存在：仍按 edit 处理（不是覆盖为新文件）
        File.WriteAllText(Path.Combine(_dir, "exist.txt"), "old\n");
        var patch = "@@ -1,1 +1,1 @@\n-old\n+new\n";

        await Apply(patch, "exist.txt", allowNewFile: true);

        Assert.Equal("new\n", File.ReadAllText(Path.Combine(_dir, "exist.txt")));
    }

    [Fact]
    public async Task Apply_AllowNewFile_Subdirectory_AutoCreatesParentDirs()
    {
        // allow_new_file=true:补丁创建的文件在子目录中时，父目录也应自动创建
        var patch = "@@ -0,0 +1 @@\n+hello\n";

        await Apply(patch, "subdir/deep/new.txt", allowNewFile: true);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(_dir, "subdir", "deep", "new.txt")));
    }

    [Fact]
    public async Task Apply_AllowNewFile_InvalidPatch_ThrowsWithoutCreatingFile()
    {
        // allow_new_file=true 但补丁本身无效：不应创建文件，应抛出校验错误
        var patch = "@@ -0,0 +1,1 @@\n context\n"; // 没有 +/- 前缀，无效补丁行

        var ex = await Assert.ThrowsAsync<ToolException>(() => Apply(patch, "new.txt", allowNewFile: true));

        Assert.Contains("补丁上下文不匹配", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "new.txt")));
    }

    [Fact]
    public async Task Apply_AllowNewFile_CrlfPatch_NormalizesToLf()
    {
        // allow_new_file=true:补丁中的 CRLF 被归一化为 LF（patch 格式本身用 LF）
        var patch = "@@ -0,0 +1,2 @@\r\n+line1\r\n+line2\r\n";

        await Apply(patch, "crlf_new.txt", allowNewFile: true);

        Assert.Equal("line1\nline2", File.ReadAllText(Path.Combine(_dir, "crlf_new.txt")));
    }

    [Fact]
    public async Task Apply_MissingTarget_WithoutAllowNewFile_Throws()
    {
        var patch = "@@ -0,0 +1 @@\n+x\n";

        var ex = await Assert.ThrowsAsync<ToolException>(() => Apply(patch, "nope.txt"));

        Assert.Contains("不存在", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "nope.txt")));
    }

    [Fact]
    public async Task Apply_OnlyContextLines_NoOp()
    {
        // 补丁只有上下文行（无 +/- 变更）：文件应保持不变，报告 0 变更
        var patch = "@@ -1,2 +1,2 @@\n line1\n line2\n";
        File.WriteAllText(Path.Combine(_dir, "ctx.txt"), "line1\nline2\n");

        var result = await Apply(patch, "ctx.txt");

        Assert.Equal("line1\nline2\n", File.ReadAllText(Path.Combine(_dir, "ctx.txt")));
        Assert.Contains("(-0 +0)", result); // 无增删
    }

    [Fact]
    public async Task Apply_MultipleHunks_WithGaps_Works()
    {
        // 多个 hunk 之间有间隔：应正确保留间隔内容
        var patch = "@@ -1,1 +1,1 @@\n-old1\n+new1\n@@ -3,1 +3,1 @@\n-old3\n+new3\n";
        File.WriteAllText(Path.Combine(_dir, "gap.txt"), "old1\nkeep\nold3\n");

        await Apply(patch, "gap.txt");

        Assert.Equal("new1\nkeep\nnew3\n", File.ReadAllText(Path.Combine(_dir, "gap.txt")));
    }

    [Fact]
    public async Task Apply_EmptyHunk_Throws()
    {
        // 空 hunk（无数据行）：补丁无效，应抛出错误
        var patch = "@@ -1,1 +1,1 @@\n";

        var ex = await Assert.ThrowsAsync<ToolException>(() => Apply(patch, "empty.txt"));

        Assert.Contains("没有可用的文件块", ex.Message);
    }

    [Fact]
    public async Task Apply_EmptyPatch_WithAllowEmpty_ReturnsMessage()
    {
        // allow_empty=true:无可用文件块的补丁返回提示而非报错
        var patch = "# this is just a comment, no hunks";

        var result = await Apply(patch, "empty.txt", allowEmpty: true);

        Assert.Contains("补丁为空", result);
        Assert.Contains("allow_empty=true", result);
    }

    [Fact]
    public async Task Apply_PatchWithFileHeaderButNoHunks_RejectsWithoutAllowEmpty()
    {
        // 回归：有 +++ 文件头但无 @@ hunk 的补丁在 allow_empty=false 时应拒绝，
        // 而非静默当作 no-op 应用（曾让模型以为修改成功，实际文件未动）
        var patch = "+++ b/foo.txt\n";

        var ex = await Assert.ThrowsAsync<ToolException>(() => Apply(patch, "foo.txt"));
        Assert.Contains("空补丁", ex.Message);
    }

    [Fact]
    public async Task Apply_PatchWithFileHeaderButNoHunks_AllowsWithAllowEmpty()
    {
        // allow_empty=true 时，空 hunk 的补丁不报错，但也不会实际修改文件
        File.WriteAllText(Path.Combine(_dir, "foo.txt"), "hello");
        var patch = "+++ b/foo.txt\n";

        var result = await Apply(patch, "foo.txt", allowEmpty: true);

        Assert.Contains("已应用 0 个 hunk", result);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_dir, "foo.txt"))); // 文件未变
    }

    [Fact]
    public async Task Apply_EscapedLine_InHunk_IsPreservedAsContext()
    {
        // 转义行（\ 前缀）在 unified diff 中表示「原文本身以易混淆字符开头」，
        // 应当去掉前缀后当上下文行保留，而非静默丢弃。
        File.WriteAllText(Path.Combine(_dir, "esc.txt"), @"\begin{verbatim}
old
\end{verbatim}");
        var patch = @"@@ -1,3 +1,3 @@
 \begin{verbatim}
-old
+new
 \end{verbatim}";

        await Apply(patch, "esc.txt");

        var content = File.ReadAllText(Path.Combine(_dir, "esc.txt"));
        Assert.Contains(@"\begin{verbatim}", content);
        Assert.Contains(@"\end{verbatim}", content);
        Assert.Contains("new", content);
    }

    [Fact]
    public async Task Apply_Generous_MultiHunk_WithFuzz_AppliesSuccessfully()
    {
        // generous=true + 多 hunk + 行号漂移：放宽匹配后仍能应用
        // 文件在第一个 hunk 之后多一行 lineX，导致第二个 hunk 行号偏移
        File.WriteAllText(Path.Combine(_dir, "gh.txt"), "line1\nline2\nlineX\nline3\nline4\nline5\n");
        var patch = @"@@ -1,2 +1,2 @@
 line1
-line2
+new2
@@ -3,2 +3,2 @@
 line3
-line4
+new4";

        var result = await Apply(patch, "gh.txt", generous: true);

        Assert.Contains("已应用", result);
        var text = File.ReadAllText(Path.Combine(_dir, "gh.txt"));
        Assert.Contains("new2", text);
        Assert.Contains("new4", text);
        Assert.Contains("line5", text); // 末尾内容保留
    }

    [Fact]
    public async Task Apply_GenerousFalse_MultiHunk_WithFuzz_Throws()
    {
        // generous=false + 多 hunk + 行号漂移：严格匹配失败
        File.WriteAllText(Path.Combine(_dir, "gs.txt"), "line1\nline2\nlineX\nline3\nline4\nline5\n");
        var patch = @"@@ -1,2 +1,2 @@
 line1
-line2
+new2
@@ -3,2 +3,2 @@
 line3
-line4
+new4";

        var ex = await Assert.ThrowsAsync<ToolException>(() => Apply(patch, "gs.txt", generous: false));

        Assert.Contains("上下文不匹配", ex.Message);
    }

    [Fact]
    public async Task Apply_DryRun_DoesNotModifyFile()
    {
        // dry_run=true:预览改动但不写盘
        File.WriteAllText(Path.Combine(_dir, "dry.txt"), "line1\nline2\nline3\n");
        var patch = @"@@ -1,2 +1,2 @@
 line1
-line2
+new2";

        var result = await Apply(patch, "dry.txt", dryRun: true);

        Assert.Contains("[dry_run]", result);
        Assert.Equal("line1\nline2\nline3\n", File.ReadAllText(Path.Combine(_dir, "dry.txt"))); // 文件未变
    }

    [Fact]
    public async Task Apply_DryRun_NewFile_PreventsCreation()
    {
        // dry_run=true + allow_new_file:预览新建文件但不实际创建
        var patch = @"@@ -0,0 +1,2 @@
+new line1
+new line2";

        var result = await Apply(patch, "new.txt", allowNewFile: true, dryRun: true);

        Assert.Contains("[dry_run]", result);
        Assert.False(File.Exists(Path.Combine(_dir, "new.txt"))); // 文件未创建
    }

    [Fact]
    public async Task Apply_Backup_True_CreatesBakFile()
    {
        // backup=true:应用补丁前创建 .bak 备份
        File.WriteAllText(Path.Combine(_dir, "bak.txt"), "line1\nline2\nline3\n");
        var patch = @"@@ -1,2 +1,2 @@
 line1
-line2
+new2";

        var result = await Apply(patch, "bak.txt", backup: true);

        Assert.Contains("已应用", result);
        Assert.Equal("line1\nnew2\nline3\n", File.ReadAllText(Path.Combine(_dir, "bak.txt"))); // 文件已修改
        Assert.Equal("line1\nline2\nline3\n", File.ReadAllText(Path.Combine(_dir, "bak.txt.bak"))); // 备份保留原内容
    }

    [Fact]
    public async Task Apply_Backup_False_NoBakFile()
    {
        // backup=false（默认）:不创建 .bak 备份
        File.WriteAllText(Path.Combine(_dir, "nobak.txt"), "line1\nline2\n");
        var patch = @"@@ -1,2 +1,2 @@
 line1
-line2
+new2";

        var result = await Apply(patch, "nobak.txt", backup: false);

        Assert.Contains("已应用", result);
        Assert.Equal("line1\nnew2\n", File.ReadAllText(Path.Combine(_dir, "nobak.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "nobak.txt.bak"))); // 无备份文件
    }

}
