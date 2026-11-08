namespace Jyuno.Tests;

//비교(less, greater)와 논리/비트(and, or, not) 연산
public class OperatorTests
{
    //less, greater

    [Theory]
    [InlineData("less 1 2", true)]
    [InlineData("less 2 1", false)]
    [InlineData("less 1 1", false)]
    [InlineData("greater 2 1", true)]
    [InlineData("greater 1 2", false)]
    [InlineData("greater 1 1", false)]
    [InlineData("less 1 1.5", true)]
    [InlineData("greater 2.5 2", true)]
    [InlineData("less -3 -2", true)]
    [InlineData("less 9223372036854775806 9223372036854775807", true)]
    [InlineData("less 'a' 'b'", true)]
    [InlineData("less 'B' 'a'", true)]
    [InlineData("greater 'b' 'a'", true)]
    [InlineData("less '' 'a'", true)]
    [InlineData("less 5", true)]
    [InlineData("greater 'a'", true)]
    public void Compare_TwoOrOne(string expression, bool expected)
    {
        Assert.Equal(expected, Script.Eval(expression));
    }

    [Theory]
    [InlineData("less 1 2 3", true)]
    [InlineData("less 1 2 3 10 100", true)]
    [InlineData("less 1 3 2", false)]
    [InlineData("less 1 2 2", false)]
    [InlineData("less 3 2 1", false)]
    [InlineData("greater 3 2 1", true)]
    [InlineData("greater 3 1 2", false)]
    [InlineData("greater 3 3 1", false)]
    [InlineData("greater '라' '다' '나' '가'", true)]
    [InlineData("greater '가' '나' '다' '라'", false)]
    [InlineData("less '가' '나' '다' '라'", true)]
    [InlineData("less 1 1.5 2", true)]
    public void Compare_ThreeOrMore_ChecksOrder(string expression, bool expected)
    {
        Assert.Equal(expected, Script.Eval(expression));
    }

    [Theory]
    [InlineData("less math.nan 1")]
    [InlineData("greater 1 math.nan")]
    [InlineData("less math.nan math.nan")]
    [InlineData("greater math.nan math.nan")]
    public void Compare_NaN_IsFalse(string expression)
    {
        Assert.Equal(false, Script.Eval(expression));
    }

    [Theory]
    [InlineData("less")]
    [InlineData("greater")]
    [InlineData("less 1 'a'")]
    [InlineData("greater 'a' 1")]
    [InlineData("less null 1")]
    [InlineData("less (array 1) (array 2)")]
    //앞에서 이미 false가 나왔더라도 비교할수 없는 값은 오류
    [InlineData("less 2 1 'a'")]
    public void Compare_Invalid_Throws(string expression)
    {
        Assert.Throws<JyunoException>(() => Script.Eval(expression));
    }

    [Fact]
    public void Compare_NativeValues()
    {
        Runtime runtime = new();
        runtime.AddFunction("small", _ => 1);
        runtime.AddFunction("letter", args => ((string)args[0]!)[0]);
        Interpreter interpreter = runtime.Create();
        Assert.Equal(true, interpreter.ExecuteLine("less (small) 2"));
        Assert.Equal(true, interpreter.ExecuteLine("less (letter 'a') (letter 'b') (letter 'c')"));
    }

    [Fact]
    public void Compare_InLoop()
    {
        var (_, output) = Script.Run(
            "i = 0",
            "while (less i 3)",
            "\tout i",
            "\ti = add i 1",
            "end");
        Assert.Equal(new object?[] { 0L, 1L, 2L }, output);
    }

    //and, or

    [Theory]
    [InlineData("and 12 10", 8L)]
    [InlineData("or 12 10", 14L)]
    [InlineData("and 10 20 30", 0L)]
    [InlineData("and 15 7 3", 3L)]
    [InlineData("or 1 2 4", 7L)]
    [InlineData("and -1 255", 255L)]
    [InlineData("or 0 0", 0L)]
    [InlineData("and 6", 6L)]
    [InlineData("or 6", 6L)]
    public void AndOr_Integers_Bitwise(string expression, long expected)
    {
        Assert.Equal(expected, Script.Eval(expression));
    }

    [Theory]
    [InlineData("and true true", true)]
    [InlineData("and true false", false)]
    [InlineData("or false true", true)]
    [InlineData("or false false", false)]
    [InlineData("and true true true", true)]
    [InlineData("and true true false", false)]
    [InlineData("or false false true", true)]
    [InlineData("or false false false", false)]
    //정수가 아닌 값이 하나라도 있다면 모두 참/거짓으로 판단
    [InlineData("and 1 2 true", true)]
    [InlineData("and 1 0 true", false)]
    [InlineData("or 0 0 'a'", true)]
    [InlineData("or 0 null ''", false)]
    [InlineData("and 1.5 'a'", true)]
    [InlineData("and 1.5 0", false)]
    [InlineData("or null", false)]
    [InlineData("and 'a'", true)]
    public void AndOr_NotAllIntegers_Logical(string expression, bool expected)
    {
        Assert.Equal(expected, Script.Eval(expression));
    }

    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("not")]
    public void Logic_NoArguments_Throws(string expression)
    {
        Assert.Throws<JyunoException>(() => Script.Eval(expression));
    }

    //not

    [Theory]
    [InlineData("not 0", -1L)]
    [InlineData("not 5", -6L)]
    [InlineData("not -1", 0L)]
    public void Not_Integer_Bitwise(string expression, long expected)
    {
        Assert.Equal(expected, Script.Eval(expression));
    }

    [Theory]
    [InlineData("not true", false)]
    [InlineData("not false", true)]
    [InlineData("not null", true)]
    [InlineData("not ''", true)]
    [InlineData("not 'a'", false)]
    [InlineData("not 0.0", true)]
    [InlineData("not 1.5", false)]
    [InlineData("not (less 1 2)", false)]
    [InlineData("not (bool 5)", false)]
    public void Not_Other_Logical(string expression, bool expected)
    {
        Assert.Equal(expected, Script.Eval(expression));
    }

    [Theory]
    [InlineData("not true false", false)]
    [InlineData("not false 'x' 1", true)]
    [InlineData("not 5 'x'", -6L)]
    public void Not_IgnoresExtraArguments(string expression, object expected)
    {
        Assert.Equal(expected, Script.Eval(expression));
    }

    //상수 계산: 내장 연산자도 미리 계산하되, 다시 정의하면 원래대로 호출해야 함

    [Fact]
    public void Operators_ConstantFolding_RespectsRedefinition()
    {
        var (_, output) = Script.Run(
            "repeat 2",
            "\tout (less 1 2 3)",
            "\tout (and 12 10)",
            "\tout (not false)",
            "\tfunc less",
            "\t\treturn 'mine'",
            "\tendfunc",
            "\tfunc and",
            "\t\treturn 'mine too'",
            "\tendfunc",
            "end");
        Assert.Equal(true, output[0]);
        Assert.Equal(8L, output[1]);
        Assert.Equal(true, output[2]);
        Assert.Equal("mine", output[3]);
        Assert.Equal("mine too", output[4]);
        Assert.Equal(true, output[5]);
        Assert.Equal(6, output.Count);
    }
}
