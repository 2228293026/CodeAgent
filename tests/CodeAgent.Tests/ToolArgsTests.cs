using System.Collections.Generic;
using System.Text.Json.Nodes;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class ToolArgsTests
{
    [Fact]
    public void GetString_StringValue_ReturnsIt()
    {
        var args = new JsonObject { ["path"] = "a.txt" };
        Assert.Equal("a.txt", ToolArgs.GetString(args, "path"));
    }

    [Fact]
    public void GetString_NumericValue_CoercedToString()
    {
        // 模型偶尔把字符串参数序列化为数字
        var args = new JsonObject { ["content"] = 123 };
        Assert.Equal("123", ToolArgs.GetString(args, "content"));
    }

    [Fact]
    public void GetString_MissingKey_ReturnsDefault()
    {
        Assert.Equal("def", ToolArgs.GetString(new JsonObject(), "nope", "def"));
        Assert.Equal("", ToolArgs.GetString(null, "nope"));
    }

    [Fact]
    public void GetInt_NativeNumber_Wins()
    {
        var args = new JsonObject { ["limit"] = 500 };
        Assert.Equal(500, ToolArgs.GetInt(args, "limit", 300));
    }

    [Theory]
    [InlineData("300", 300)]   // 字符串数字
    [InlineData("  42 ", 42)]  // 带空白
    [InlineData("abc", 7)]     // 非法 → 默认值
    public void GetInt_StringOrInvalid_FallsBack(string value, int expected)
    {
        var args = new JsonObject { ["limit"] = value };
        Assert.Equal(expected, ToolArgs.GetInt(args, "limit", 7));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("Y", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("N", false)]
    [InlineData("maybe", true)] // 未知字符串回退默认值（默认 true）
    public void GetBool_StringForms(string value, bool expected)
    {
        var args = new JsonObject { ["flag"] = value };
        Assert.Equal(expected, ToolArgs.GetBool(args, "flag", true));
    }

    [Fact]
    public void GetBool_NativeBool_Wins()
    {
        var args = new JsonObject { ["flag"] = false };
        Assert.False(ToolArgs.GetBool(args, "flag", true));
    }

    [Fact]
    public void GetStringDict_ParsesKeyValues()
    {
        var args = new JsonObject
        {
            ["env"] = new JsonObject { ["A"] = "1", ["B"] = "hello" },
        };
        var dict = ToolArgs.GetStringDict(args, "env");
        Assert.NotNull(dict);
        Assert.Equal(2, dict!.Count);
        Assert.Equal("1", dict["A"]);
        Assert.Equal("hello", dict["B"]);
    }

    [Fact]
    public void GetStringDict_MissingOrEmpty_ReturnsNull()
    {
        Assert.Null(ToolArgs.GetStringDict(new JsonObject(), "env"));
        Assert.Null(ToolArgs.GetStringDict(new JsonObject { ["env"] = new JsonObject() }, "env"));
    }

    [Fact]
    public void GetStringList_SingleStringOrArray_BothWork()
    {
        var single = new JsonObject { ["pattern"] = "*.cs" };
        Assert.Equal(["*.cs"], ToolArgs.GetStringList(single, "pattern"));

        var array = new JsonObject { ["pattern"] = new JsonArray("*.cs", "*.rs") };
        Assert.Equal(["*.cs", "*.rs"], ToolArgs.GetStringList(array, "pattern"));
    }

    [Fact]
    public void GetStringList_Missing_ReturnsNull()
    {
        Assert.Null(ToolArgs.GetStringList(new JsonObject(), "pattern"));
    }
}
