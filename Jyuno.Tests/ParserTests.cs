using Jyuno.Compiler;

namespace Jyuno.Tests;

public class ParserTests
{
    [Theory]
    [InlineData("0.3", 0.3)]
    [InlineData("1.1", 1.1)]
    [InlineData("-2.5", -2.5)]
    [InlineData("3.", 3.0)]
    public void Tokenizer_ParsesRealNumbersExactly(string text, double expected)
    {
        Token token = Assert.Single(Parser.Tokenizer(text));
        Assert.Equal(TokenType.Constant, token.type);
        Assert.Equal(expected, token.value);
    }

    [Theory]
    [InlineData("-5", -5L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void Tokenizer_ParsesIntegers(string text, long expected)
    {
        Token token = Assert.Single(Parser.Tokenizer(text));
        Assert.Equal(TokenType.Constant, token.type);
        Assert.Equal(expected, token.value);
    }

    [Fact]
    public void Tokenizer_IntegerOverflow_IsError()
    {
        Token token = Parser.Tokenizer("x = 9223372036854775808").Last();
        Assert.Equal(TokenType.Error, token.type);
    }

    [Fact]
    public void Tokenizer_MinusWithoutDigit_IsPrefix()
    {
        Token[] tokens = Parser.Tokenizer("- 5").ToArray();
        Assert.Equal(TokenType.Prefix, tokens[0].type);
        Assert.Equal(5L, tokens[1].value);
    }

    [Fact]
    public void Checker_AllowsEmptyLines()
    {
        Assert.Empty(Parser.Checker(new[] { "", "x = 1", "   " }));
    }

    [Fact]
    public void Checker_ReportsGrammarErrors()
    {
        var error = Assert.Single(Parser.Checker(new[] { "x = 1", "out 'abc" }));
        Assert.Equal(1, error.Line);
    }
}
