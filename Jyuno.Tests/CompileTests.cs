namespace Jyuno.Tests;

//줄을 미리 컴파일하고, 상수를 미리 계산하고, 이름 해석 결과를 캐싱해도 결과가 이전과 같아야 함
public class CompileTests
{
    //상수 계산

    [Theory]
    [InlineData("add 1 2", 3L)]
    [InlineData("add (mul 2 3) (sub 10 4)", 12L)]
    [InlineData("add 1 2.5", 3.5)]
    [InlineData("add 'a' 1", "a1")]
    [InlineData("mul 'ab' 3", "ababab")]
    [InlineData("string 1.5", "1.5")]
    [InlineData("equal (add 1 1) 2", true)]
    [InlineData("int '12'", 12L)]
    [InlineData("add 9223372036854775807 1", long.MinValue)]
    public void ConstantExpression_SameResult(string expression, object expected)
    {
        Assert.Equal(expected, Script.Eval(expression));
    }

    [Theory]
    [InlineData("div 1 0")]
    [InlineData("mod 1 0")]
    [InlineData("sub 1")]
    [InlineData("(add 1 2")]
    public void ConstantExpression_ErrorStillThrowsWhenExecuted(string expression)
    {
        Assert.Throws<JyunoException>(() => Script.Eval(expression));
    }

    [Fact]
    public void ConstantExpression_ErrorOnlyWhenLineRuns()
    {
        var (_, output) = Script.Run(
            "if 0",
            "\tout (div 1 0)",
            "end",
            "out 'ok'");
        Assert.Equal(new object?[] { "ok" }, output);
    }

    [Fact]
    public void ConstantExpression_UsesFunctionDefinedLater()
    {
        //같은 줄이 다시 실행될 때 add가 사용자 함수로 바뀌었다면 미리 계산한 값을 쓰지 않음
        var (_, output) = Script.Run(
            "n = 0",
            "repeat 3",
            "\tout (add 1 2)",
            "\tn = sub n -1",
            "\tif (equal n 2)",
            "\t\tfunc add",
            "\t\t\treturn 100",
            "\t\tendfunc",
            "\tend",
            "end");
        Assert.Equal(new object?[] { 3L, 3L, 100L }, output);
    }

    [Fact]
    public void ConstantExpression_ShadowedByLocalInFunction()
    {
        var (_, output) = Script.Run(
            "func f",
            "\tout (mul 2 3)",
            "\tlocal mul = 7",
            "\tout mul",
            "endfunc",
            "f",
            "f",
            "out (mul 2 3)");
        Assert.Equal(new object?[] { 6L, 7L, 6L, 7L, 6L }, output);
    }

    [Fact]
    public void ConstantExpression_ShadowedByConstantVariable()
    {
        Interpreter interpreter = new Runtime().Create();
        Assert.Equal(3L, interpreter.ExecuteLine("add 1 2"));
        interpreter.ExecuteLine("5 = add");
        //변수 뒤에 인자가 있다면 변수 값만 돌려줌 (괄호 안이라면 오류)
        Assert.Equal(5L, interpreter.ExecuteLine(0));
        Assert.Throws<JyunoException>(() => interpreter.ExecuteLine("x = (add 1 2)"));
    }

    [Fact]
    public void ConstantExpression_RemovedBuiltin()
    {
        Interpreter interpreter = new Runtime().Create(new[] { "add 1 2" });
        Assert.Equal(3L, interpreter.ExecuteLine(0));
        Assert.Equal(true, interpreter.ExecuteLine("add ="));
        Assert.Throws<JyunoException>(() => interpreter.ExecuteLine(0));
    }

    [Fact]
    public void ConstantExpression_InnerFunctionShadowed()
    {
        //바깥 add는 그대로지만 안쪽 mul이 바뀐 경우
        var (_, output) = Script.Run(
            "repeat 2",
            "\tout (add 1 (mul 2 3))",
            "\tfunc mul",
            "\t\treturn 10",
            "\tendfunc",
            "end");
        Assert.Equal(new object?[] { 7L, 11L }, output);
    }

    [Fact]
    public void ConstantExpression_CollectionResultIsNotShared()
    {
        //배열/리스트를 돌려주는 계산은 실행할때마다 새로 만들어야 함
        var (_, output) = Script.Run(
            "repeat 2",
            "\ta = div 'a,b' ','",
            "\tout (a 0)",
            "\ta 0 'z'",
            "end");
        Assert.Equal(new object?[] { "a", "a" }, output);
    }

    //괄호와 실행 순서

    [Fact]
    public void Parenthesis_VariableWithExtraTokens_AssignsThenThrows()
    {
        Interpreter interpreter = new Runtime().Create();
        interpreter.ExecuteLine("y = 3");
        //대입은 끝까지 한 다음 괄호 오류
        Assert.Throws<JyunoException>(() => interpreter.ExecuteLine("(x = y 1)"));
        Assert.Equal(3L, interpreter.ExecuteLine("x"));
        //함수가 아닌 이름에 대입하려 했다면 그 오류가 먼저
        JyunoException error = Assert.Throws<JyunoException>(() => interpreter.ExecuteLine("(add = y 1)"));
        Assert.Contains("변수가 아닙니다", error.Message);
    }

    [Fact]
    public void Arguments_EvaluatedLeftToRight()
    {
        var (_, output) = Script.Run(
            "func f",
            "\tparam v",
            "\tout v",
            "\treturn v",
            "endfunc",
            "out (add (f 1) (f 2) (f 3))");
        Assert.Equal(new object?[] { 1L, 2L, 3L, 6L }, output);
    }

    //이름 해석 캐싱

    [Fact]
    public void NameCache_LocalShadowingInFunction()
    {
        var (_, output) = Script.Run(
            "x = 1",
            "func f",
            "\tout x",
            "\tlocal x = 2",
            "\tout x",
            "endfunc",
            "f",
            "f",
            "out x");
        Assert.Equal(new object?[] { 1L, 2L, 1L, 2L, 1L }, output);
    }

    [Fact]
    public void NameCache_LocalInOneForIterationOnly()
    {
        var (_, output) = Script.Run(
            "s = 0",
            "for x (list 1 2 3)",
            "\tif (equal x 2)",
            "\t\tlocal s = 100",
            "\tend",
            "\tout s",
            "end",
            "out s");
        Assert.Equal(new object?[] { 0L, 100L, 0L, 0L }, output);
    }

    [Fact]
    public void NameCache_VariableFromPreviousIterationIsGone()
    {
        //반복 한번에서 만든 변수는 다음 반복에서 보이지 않음
        Runtime runtime = new();
        List<object?> output = new();
        runtime.AddFunction("out", args => { output.Add(args[0]); return null; });
        Interpreter interpreter = runtime.Create(new[] {
            "for i (list 1 2)",
            "\tif (equal i 1)",
            "\t\tz = 5",
            "\tend",
            "\tout z",
            "end" });
        Assert.Throws<JyunoException>(() => interpreter.Run());
        Assert.Equal(new object?[] { 5L }, output);
    }

    [Fact]
    public void NameCache_OuterVariableReplacedBetweenCalls()
    {
        var (_, output) = Script.Run(
            "y = 1",
            "func f",
            "\tout y",
            "endfunc",
            "f",
            "5 = y",
            "f",
            "y =",
            "y = 7",
            "f");
        Assert.Equal(new object?[] { 1L, 5L, 7L }, output);
    }

    [Fact]
    public void NameCache_OuterVariableCreatedAfterFirstLookup()
    {
        //처음에는 전역 함수였던 이름을 바깥 영역에서 새로 정의
        var (_, output) = Script.Run(
            "func f",
            "\tparam a",
            "\treturn (string a)",
            "endfunc",
            "repeat 2",
            "\tout (f 1)",
            "\tfunc string",
            "\t\treturn 'mine'",
            "\tendfunc",
            "end");
        Assert.Equal(new object?[] { "1", "mine" }, output);
    }

    [Fact]
    public void NameCache_RecursionSeesOwnParameters()
    {
        var (result, _) = Script.Run(
            "func fib",
            "\tparam n",
            "\tif (equal (bool (sub n 1)) false)",
            "\t\treturn 1",
            "\tend",
            "\tif (equal n 2)",
            "\t\treturn 1",
            "\tend",
            "\treturn (add (fib (sub n 1)) (fib (sub n 2)))",
            "endfunc",
            "return (fib 15)");
        Assert.Equal(610L, result);
    }

    [Fact]
    public void NameCache_RemovedVariableInLoop()
    {
        var (_, output) = Script.Run(
            "x = 1",
            "i = 0",
            "while (equal (bool (sub i 3)) true)",
            "\tout (bool x)",
            "\tx =",
            "\tx = 2",
            "\ti = add i 1",
            "end",
            "out x");
        Assert.Equal(new object?[] { true, true, true, 2L }, output);
    }

    [Fact]
    public void NameCache_SeparateInterpreters()
    {
        Runtime runtime = new();
        Interpreter first = runtime.Create(new[] { "add 1 2" });
        Interpreter second = runtime.Create();
        second.scripts.Add(first.scripts[0]);
        Assert.Equal(3L, first.ExecuteLine(0));
        first.ExecuteLine("add =");
        Assert.Throws<JyunoException>(() => first.ExecuteLine(0));
        //같은 줄이라도 다른 인터프리터의 상태를 쓰지 않음
        Assert.Equal(3L, second.ExecuteLine(0));
        Assert.Throws<JyunoException>(() => first.ExecuteLine(0));
    }
}
