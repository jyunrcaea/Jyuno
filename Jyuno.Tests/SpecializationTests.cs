namespace Jyuno.Tests;

//내장 함수 호출 자리 특화: 빠른 경로가 처리하지 못하는 값이 오거나, 이름이 다른 것을 가리키게 되어도 결과가 같아야 함
public class SpecializationTests
{
    //같은 호출 자리를 여러 번 실행한 다음, 마지막에 인자 타입을 바꿈
    static string[] TypeChangeAfterWarmup(string op , string warm , string changed) => new[] {
        $"x = {warm}",
        "i = 0",
        "while (less i 20)",
        "\tif (equal i 19)",
        $"\t\tx = {changed}",
        "\tend",
        $"\tr = {op} x x",
        "\ti = add i 1",
        "end",
        "return r",
    };

    [Theory]
    [InlineData("add", "3", "'a'", "aa")]
    [InlineData("add", "3", "0.5", 1.0)]
    [InlineData("add", "0.5", "3", 6L)]
    [InlineData("sub", "3", "2.5", 0.0)]
    [InlineData("mul", "3", "'ab'", null)]
    [InlineData("equal", "3", "'a'", true)]
    [InlineData("less", "3", "2.5", false)]
    [InlineData("and", "3", "true", true)]
    [InlineData("or", "3", "false", false)]
    [InlineData("add", "'a'", "7", 14L)]
    [InlineData("and", "true", "1", 1L)]
    public void TypeChange_AfterSpecialization(string op , string warm , string changed , object? expected)
    {
        if (expected is null)
        {
            //mul 'ab' 'ab': 반복 횟수가 정수가 아니므로 오류
            Assert.Throws<JyunoException>(() => Script.Run(TypeChangeAfterWarmup(op , warm , changed)));
            return;
        }
        var (result, _) = Script.Run(TypeChangeAfterWarmup(op , warm , changed));
        Assert.Equal(expected , result);
    }

    [Fact]
    public void DivideByZero_AfterSpecialization()
    {
        var lines = new[] {
            "d = 3",
            "i = 0",
            "while (less i 20)",
            "\tif (equal i 15)",
            "\t\td = 0",
            "\tend",
            "\tout (div 30 d)",
            "\ti = add i 1",
            "end",
        };
        var e = Assert.Throws<JyunoException>(() => Script.Run(lines));
        Assert.Equal("0으로 나눌수 없습니다." , e.Message);
    }

    [Fact]
    public void Redefinition_AfterSpecialization()
    {
        var (_, output) = Script.Run(
            "i = 0",
            "while (less i 12)",
            "\tif (equal i 10)",
            "\t\tfunc add",
            "\t\t\treturn 'user'",
            "\t\tendfunc",
            "\tend",
            "\tout (add i 1)",
            "\ti = sub i -1",
            "end");
        Assert.Equal(new object?[] { 1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L, 10L, "user", "user" } , output);
    }

    [Fact]
    public void Removal_AfterSpecialization()
    {
        var lines = new[] {
            "i = 0",
            "while (less i 12)",
            "\tif (equal i 10)",
            "\t\tmul =",
            "\tend",
            "\tout (mul i 2)",
            "\ti = sub i -1",
            "end",
        };
        var e = Assert.Throws<JyunoException>(() => Script.Run(lines));
        Assert.Equal("'mul'은/는 존재하지 않는 함수/변수/명령어 입니다." , e.Message);
    }

    [Fact]
    public void Recursion_ReentersSameCallSite()
    {
        //add 자리의 인자를 계산하는 도중에 같은 add 자리가 재귀로 다시 실행되며, 그 사이에 타입이 바뀜
        var (result, _) = Script.Run(
            "func f",
            "\tparam n",
            "\tif (less n 1)",
            "\t\treturn 0.5",
            "\tend",
            "\tif (equal n 12)",
            "\t\treturn (add 'x' (f (sub n 1)))",
            "\tend",
            "\treturn (add (f (sub n 1)) 1)",
            "endfunc",
            "return (f 20)");
        //f 0 = 0.5, f 1..11 = 0.5 + n, f 12 = 'x11.5', f 13..20 = 'x11.5' + '1' * 8
        Assert.Equal("x11.511111111" , result);
    }

    [Fact]
    public void PolymorphicSite_StillCorrect()
    {
        var (_, output) = Script.Run(
            "i = 0",
            "while (less i 30)",
            "\tk = mod i 3",
            "\tx = i",
            "\tif (equal k 1)",
            "\t\tx = 0.5",
            "\tend",
            "\tif (equal k 2)",
            "\t\tx = 'a'",
            "\tend",
            "\tout (add x 1)",
            "\ti = add i 1",
            "end");
        for (int i = 0 ; i < 30 ; i++)
        {
            object expected = (i % 3) switch { 0 => (object)(long)(i + 1), 1 => 1.5, _ => "a1" };
            Assert.Equal(expected , output[i]);
        }
    }

    [Fact]
    public void OverflowAndSpecialValues_AfterSpecialization()
    {
        var (_, output) = Script.Run(
            "x = 9223372036854775800",
            "repeat 12",
            "\tx = add x 1",
            "end",
            "out x",
            "n = div 0.0 0.0",
            "c = 0",
            "repeat 12",
            "\tif (less n 1.0)",
            "\t\tc = add c 1",
            "\tend",
            "end",
            "out c",
            "out (div -9223372036854775808 1)");
        Assert.Equal(new object?[] { long.MinValue + 4, 0L, long.MinValue } , output);
    }
}
