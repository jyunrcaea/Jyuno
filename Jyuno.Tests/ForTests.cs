namespace Jyuno.Tests;

public class ForTests
{
    [Theory]
    [InlineData("length 'hello'", 5L)]
    [InlineData("length ''", 0L)]
    [InlineData("length (array 1 2 3)", 3L)]
    [InlineData("length (list)", 0L)]
    [InlineData("length (div 'a,b,c' ',')", 3L)]
    public void Length_ReturnsCount(string expression, long expected)
    {
        Assert.Equal(expected, Script.Eval(expression));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("length null")]
    [InlineData("length 12")]
    public void Length_Invalid_Throws(string expression)
    {
        Assert.Throws<JyunoException>(() => Script.Eval(expression));
    }

    [Fact]
    public void For_IteratesCollection()
    {
        var (_, output) = Script.Run(
            "for x (array 1 2 3)",
            "\tout x",
            "end",
            "out 'done'");
        Assert.Equal(new object?[] { 1L, 2L, 3L, "done" }, output);
    }

    [Fact]
    public void For_CollectionFromVariableAndFunction()
    {
        var (_, output) = Script.Run(
            "words = div 'a b c' ' '",
            "for w words",
            "out w",
            "end",
            "for n (list 4 5)",
            "out n",
            "end");
        Assert.Equal(new object?[] { "a", "b", "c", 4L, 5L }, output);
    }

    [Fact]
    public void For_String_IteratesCharactersAsStrings()
    {
        var (_, output) = Script.Run(
            "for c 'ab'",
            "out c",
            "end");
        Assert.Equal(new object?[] { "a", "b" }, output);
    }

    [Fact]
    public void For_Empty_SkipsBody()
    {
        var (_, output) = Script.Run(
            "for x (array)",
            "out x",
            "end",
            "out 'after'");
        Assert.Equal(new object?[] { "after" }, output);
    }

    [Fact]
    public void For_Variable_IsLocalToLoop()
    {
        var (_, output) = Script.Run(
            "x = 'outside'",
            "for x (array 1 2)",
            "y = mul x 10",
            "out y",
            "end",
            "out x",
            "out (y =)");
        Assert.Equal(new object?[] { 10L, 20L, "outside", false }, output);
    }

    [Fact]
    public void For_CanChangeOuterVariable()
    {
        var (result, _) = Script.Run(
            "sum = 0",
            "for x (array 1 2 3 4)",
            "sum = add sum x",
            "end",
            "return sum");
        Assert.Equal(10L, result);
    }

    [Fact]
    public void For_NewScopeEachIteration()
    {
        var (_, output) = Script.Run(
            "for x (array 1 2)",
            "out (seen =)",
            "seen = true",
            "end");
        Assert.Equal(new object?[] { false, false }, output);
    }

    [Fact]
    public void For_Nested()
    {
        var (_, output) = Script.Run(
            "for a (array 1 2)",
            "for b (array 10 20)",
            "out (add a b)",
            "end",
            "end");
        Assert.Equal(new object?[] { 11L, 21L, 12L, 22L }, output);
    }

    [Fact]
    public void For_Break()
    {
        var (_, output) = Script.Run(
            "for x (array 1 2 3)",
            "if (equal x 2)",
            "break",
            "end",
            "out x",
            "end",
            "out (x =)");
        Assert.Equal(new object?[] { 1L, false }, output);
    }

    [Fact]
    public void For_BreakInner_KeepsOuter()
    {
        var (_, output) = Script.Run(
            "for a (array 1 2)",
            "for b (array 10 20)",
            "break",
            "end",
            "out a",
            "end");
        Assert.Equal(new object?[] { 1L, 2L }, output);
    }

    [Fact]
    public void For_CollectionChangedDuringLoop_UsesSnapshot()
    {
        var (_, output) = Script.Run(
            "l = array 1 2 3",
            "for x l",
            "l 2 99",
            "out x",
            "end");
        Assert.Equal(new object?[] { 1L, 2L, 3L }, output);
    }

    [Fact]
    public void For_IndexWrite_WithLength()
    {
        var (result, _) = Script.Run(
            "l = array 1 2 3",
            "i = 0",
            "for x l",
            "l i (mul x x)",
            "i = add i 1",
            "end",
            "return l");
        Assert.Equal(new long[] { 1, 4, 9 }, result);
    }

    [Fact]
    public void For_ReturnInsideFunction()
    {
        var (result, output) = Script.Run(
            "func find",
            "param items",
            "param target",
            "for x items",
            "if (equal x target)",
            "return true",
            "end",
            "end",
            "return false",
            "endfunc",
            "out (find (array 1 2 3) 2)",
            "out (find (array 1 2 3) 5)",
            "out (x =)");
        Assert.Equal(new object?[] { true, false, false }, output);
    }

    [Fact]
    public void For_ReturnAtTopLevel_RestoresScope()
    {
        Interpreter interpreter = new Runtime().Create(new[] {
            "for x (array 1 2)",
            "return x",
            "end" });
        Assert.Equal(1L, interpreter.Run());
        //최상위 영역으로 되돌아왔으므로 x는 남아있지 않음
        Assert.Equal(false, interpreter.ExecuteLine("x ="));
    }

    [Fact]
    public void For_GotoOut_RestoresScope()
    {
        var (_, output) = Script.Run(
            "for a (array 1 2)",
            "for b (array 3 4)",
            "goto 5",
            "end",
            "end",
            "out (a =)",
            "out (b =)");
        Assert.Equal(new object?[] { false, false }, output);
    }

    [Fact]
    public void For_FunctionInside_SeesLoopVariable()
    {
        var (_, output) = Script.Run(
            "for x (array 1 2)",
            "func twice",
            "return (mul x 2)",
            "endfunc",
            "out (twice)",
            "end",
            "out (twice =)");
        Assert.Equal(new object?[] { 2L, 4L, false }, output);
    }

    [Theory]
    [InlineData("for x 3")]
    [InlineData("for x null")]
    [InlineData("for 1 (array 1)")]
    [InlineData("for x")]
    public void For_Invalid_Throws(string line)
    {
        Assert.Throws<JyunoException>(() => Script.Run(line, "end"));
    }

    [Fact]
    public void For_WithoutEnd_IsGrammarError()
    {
        var (result, _) = Script.Run("for x (array 1)", "out x");
        Assert.IsType<Language.GrammarError>(result);
    }
}
