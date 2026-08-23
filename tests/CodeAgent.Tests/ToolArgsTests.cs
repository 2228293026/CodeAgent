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

    [Theory]
    [InlineData(true, "true")]      // 布尔值转字符串
    [InlineData(3.14, "3.14")]      // 浮点值转字符串
    [InlineData("", "")]            // 空字符串
    [InlineData("  ", "  ")]        // 空白字符串（不 trim）
    public void GetString_NonStringScalars_Coerced(object value, string expected)
    {
        var args = new JsonObject { ["v"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(value))! };
        Assert.Equal(expected, ToolArgs.GetString(args, "v"));
    }

    [Theory]
    [InlineData(0, 0)]            // 原生 int 0 直接返回 0（非默认值）
    [InlineData(-3, -3)]          // 原生负 int 直接返回自身
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MinValue, int.MinValue)]
    public void GetInt_EdgeValues(int value, int expected)
    {
        var args = new JsonObject { ["n"] = value };
        Assert.Equal(expected, ToolArgs.GetInt(args, "n", 9));
    }

    [Theory]
    [InlineData("", 9)]             // 空字符串无法解析 → 默认
    [InlineData("999999999999", 9)] // 超出 int 范围 → 默认
    [InlineData("+42", 42)]         // 带符号
    public void GetInt_StringEdgeValues(string value, int expected)
    {
        var args = new JsonObject { ["n"] = value };
        Assert.Equal(expected, ToolArgs.GetInt(args, "n", 9));
    }

    [Theory]
    [InlineData(10.0, 10)]     // 浮点字面量整数值（模型常发 "limit": 10.0）
    [InlineData(300.0000, 300)]
    [InlineData(-2.0, -2)]
    public void GetInt_DoubleIntegralValue_Accepted(double value, int expected)
    {
        var args = new JsonObject { ["n"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(value))! };
        Assert.Equal(expected, ToolArgs.GetInt(args, "n", 9));
    }

    [Theory]
    [InlineData(10.5)]   // 带小数部分视为非法
    [InlineData(1.0E12)] // 整数值但超出 int 范围
    public void GetInt_DoubleNonIntegralOrOutOfRange_FallsBack(double value)
    {
        var args = new JsonObject { ["n"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(value))! };
        Assert.Equal(9, ToolArgs.GetInt(args, "n", 9));
    }

    [Fact]
    public void GetInt_DoubleNonFinite_FallsBack()
    {
        // JSON 字面量不含 NaN/∞，但内存构造的 JsonValue 可能出现：防御性回默认
        Assert.Equal(9, ToolArgs.GetInt(new JsonObject { ["n"] = JsonValue.Create(double.NaN) }, "n", 9));
        Assert.Equal(9, ToolArgs.GetInt(new JsonObject { ["n"] = JsonValue.Create(double.PositiveInfinity) }, "n", 9));
    }

    [Fact]
    public void GetInt_LongBeyondIntRange_FallsBack()
    {
        var args = new JsonObject { ["n"] = 999999999999L };
        Assert.Equal(9, ToolArgs.GetInt(args, "n", 9));
    }

    [Fact]
    public void GetInt_StringFloatIntegral_Accepted()
    {
        Assert.Equal(10, ToolArgs.GetInt(new JsonObject { ["n"] = "10.0" }, "n", 9));
        Assert.Equal(9, ToolArgs.GetInt(new JsonObject { ["n"] = "10.5" }, "n", 9));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("TRUE", true)]      // 大小写不敏感
    [InlineData("False", false)]
    [InlineData("2", false)]        // 未知字符串回退默认 false
    [InlineData("", false)]         // 空串回退默认
    [InlineData("y", true)]
    [InlineData("n", false)]
    public void GetBool_MoreForms(object value, bool expected)
    {
        var args = new JsonObject { ["flag"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(value))! };
        Assert.Equal(expected, ToolArgs.GetBool(args, "flag", false));
    }

    [Fact]
    public void GetStringDict_NonObjectValue_ReturnsNull()
    {
        // env 传了字符串/数字而非对象：应返回 null 而不是崩溃
        var args = new JsonObject { ["env"] = "not-an-object" };
        Assert.Null(ToolArgs.GetStringDict(args, "env"));
    }

    [Fact]
    public void GetStringDict_NumericValues_CoercedToString()
    {
        var args = new JsonObject
        {
            ["env"] = new JsonObject { ["PORT"] = 8080 },
        };
        var dict = ToolArgs.GetStringDict(args, "env");
        Assert.NotNull(dict);
        Assert.Equal("8080", dict!["PORT"]);
    }

    [Fact]
    public void GetStringList_EmptyArray_ReturnsNull()
    {
        var args = new JsonObject { ["pattern"] = new JsonArray() };
        Assert.Null(ToolArgs.GetStringList(args, "pattern"));
    }

    [Fact]
    public void GetStringList_MixedArray_KeepsNonEmptyStrings()
    {
        var args = new JsonObject { ["pattern"] = new JsonArray("a", "", "b", 123) };
        var list = ToolArgs.GetStringList(args, "pattern");
        Assert.NotNull(list);
        Assert.Equal(["a", "b"], list); // 空串与数字被过滤
    }

    [Fact]
    public void GetString_MissingKeyWithNullArgs_ReturnsDefault()
    {
        Assert.Equal("x", ToolArgs.GetString(null, "any", "x"));
        Assert.Equal(5, ToolArgs.GetInt(null, "any", 5));
        Assert.False(ToolArgs.GetBool(null, "any", false));
    }
}
