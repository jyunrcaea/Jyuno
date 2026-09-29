using Jyuno.Language;

namespace Jyuno.Tests;

public class CommandTests
{
    [Theory]
    [InlineData("mod 7 3", 1L)]
    [InlineData("div 7 2", 3L)]
    [InlineData("div 7.0 2", 3.5)]
    [InlineData("sub 5 2", 3L)]
    [InlineData("sub 5 1.5", 3.5)]
    [InlineData("sub 5.5 1", 4.5)]
    [InlineData("mul 2 3", 6L)]
    [InlineData("mul 'ab' 3", "ababab")]
    [InlineData("mul 'ab' -1", "")]
    [InlineData("add 3 -5", -2L)]
    [InlineData("math.sin 0", 0.0)]
    [InlineData("math.pow 2 10", 1024.0)]
    [InlineData("math.log 100", 2.0)]
    [InlineData("math.log 100 10", 2.0)]
    [InlineData("math.log2 8", 3.0)]
    [InlineData("int 3.7", 3L)]
    [InlineData("int -3.7", -3L)]
    [InlineData("int '42'", 42L)]
    [InlineData("int", 0L)]
    [InlineData("double 3", 3.0)]
    [InlineData("double '2.5'", 2.5)]
    [InlineData("string 2.5", "2.5")]
    [InlineData("bool 0", false)]
    [InlineData("bool ''", false)]
    [InlineData("bool 1", true)]
    [InlineData("bool", false)]
    [InlineData("equal '1' 1", false)]
    [InlineData("equal 1 1.0", true)]
    [InlineData("equal 'a' 'a' 'a'", true)]
    public void Command_ReturnsExpectedValue(string expression, object expected)
    {
        Assert.Equal(expected, Script.Eval(expression));
    }

    [Theory]
    [InlineData("int 'abc'")]
    [InlineData("int (math.pow 10 30)")]
    [InlineData("int math.nan")]
    public void Int_CannotConvert_ReturnsNull(string expression)
    {
        Assert.Null(Script.Eval(expression));
    }

    [Theory]
    [InlineData("mod 7 null")]
    [InlineData("mod 7 0")]
    [InlineData("div 1 0")]
    [InlineData("div 1 null")]
    [InlineData("math.pow 2")]
    [InlineData("math.log")]
    [InlineData("math.sin 'a'")]
    [InlineData("mul 'ab' 'c'")]
    [InlineData("int null")]
    public void Command_InvalidInput_ThrowsJyunoException(string expression)
    {
        Assert.Throws<JyunoException>(() => Script.Eval(expression));
    }

    [Fact]
    public void NullException_IsNewEachTime()
    {
        Assert.NotSame(JyunoCommands.null_exception, JyunoCommands.null_exception);
    }
}
