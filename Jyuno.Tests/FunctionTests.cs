using Jyuno.Language;

namespace Jyuno.Tests;

public class FunctionTests
{
    [Fact]
    public void Function_ReturnsValue()
    {
        var (result, _) = Script.Run(
            "func add2",
            "  param a",
            "  param b",
            "  return (add a b)",
            "endfunc",
            "return (add2 1 2)");
        Assert.Equal(3L, result);
    }

    [Fact]
    public void Param_CanComeAfterCode()
    {
        var (_, output) = Script.Run(
            "func f",
            "  param a",
            "  out a",
            "  param b",
            "  out b",
            "endfunc",
            "f 1 2");
        Assert.Equal(new object?[] { 1L, 2L }, output);
    }

    [Fact]
    public void MissingArguments_AreNull()
    {
        var (_, output) = Script.Run(
            "func f",
            "  param a",
            "  param b",
            "  param c",
            "  out a",
            "  out b",
            "  out c",
            "endfunc",
            "f 1",
            "f");
        Assert.Equal(new object?[] { 1L, null, null, null, null, null }, output);
    }

    [Fact]
    public void ExtraArguments_AreIgnored()
    {
        var (result, _) = Script.Run("func f", "  param a", "  return a", "endfunc", "return (f 1 2 3)");
        Assert.Equal(1L, result);
    }

    [Fact]
    public void Function_WithoutReturn_ReturnsNull()
    {
        var (result, output) = Script.Run("func f", "  out 'called'", "endfunc", "return (f)");
        Assert.Null(result);
        Assert.Equal(new object?[] { "called" }, output);
    }

    [Fact]
    public void Function_IsNotRunWhenDefined()
    {
        var (_, output) = Script.Run("func f", "  out 'body'", "endfunc", "out 'after'");
        Assert.Equal(new object?[] { "after" }, output);
    }

    [Fact]
    public void Recursion_Works()
    {
        var (result, _) = Script.Run(
            "func fact",
            "  param n",
            "  if (equal n 0)",
            "    return 1",
            "  end",
            "  return (mul n (fact (sub n 1)))",
            "endfunc",
            "return (fact 10)");
        Assert.Equal(3628800L, result);
    }

    [Fact]
    public void RecursiveCalls_KeepTheirOwnRepeatCounts()
    {
        var (_, output) = Script.Run(
            "func r",
            "  param d",
            "  repeat 2",
            "    out d",
            "    if d",
            "      r (sub d 1)",
            "    end",
            "  end",
            "endfunc",
            "r 1");
        Assert.Equal(new object?[] { 1L, 0L, 0L, 1L, 0L, 0L }, output);
    }

    [Fact]
    public void LocalVariables_DisappearAfterReturn()
    {
        Assert.Throws<JyunoException>(() => Script.Run("func f", "  x = 1", "endfunc", "f", "return x"));
    }

    [Fact]
    public void Params_DoNotOverwriteCallerVariables()
    {
        var (result, _) = Script.Run("a = 5", "func f", "  param a", "endfunc", "f 1", "return a");
        Assert.Equal(5L, result);
    }

    [Fact]
    public void LoopsAndBreak_WorkInsideFunction()
    {
        var (result, _) = Script.Run(
            "func count",
            "  param limit",
            "  n = 0",
            "  while 1",
            "    n = add n 1",
            "    if (equal n limit)",
            "      break",
            "    end",
            "  end",
            "  return n",
            "endfunc",
            "return (count 4)");
        Assert.Equal(4L, result);
    }

    [Fact]
    public void Goto_WorksInsideFunction()
    {
        var (_, output) = Script.Run(
            "func f",
            "  n = 0",
            "  top:",
            "  n = add n 1",
            "  out n",
            "  if (sub 2 n)",
            "    goto top",
            "  end",
            "endfunc",
            "f",
            "f");
        Assert.Equal(new object?[] { 1L, 2L, 1L, 2L }, output);
    }

    [Theory]
    [InlineData("func f", "  goto 3", "endfunc", "out 1")]
    [InlineData("goto 2", "func f", "  out 1", "endfunc")]
    public void Goto_CannotCrossFunctionBoundary(params string[] script)
    {
        Assert.Throws<JyunoException>(() => Script.Run(script.Concat(new[] { "f" }).ToArray()));
    }

    [Fact]
    public void FunctionDefinedByRun_CanBeCalledWithExecuteLine()
    {
        Interpreter interpreter = new Runtime().Create(new[] { "func double2", "  param x", "  return (mul x 2)", "endfunc" });
        interpreter.Run();
        Assert.Equal(10L, interpreter.ExecuteLine("double2 5"));
        Assert.Equal(-1, interpreter.CurrentExecuteLine);
    }

    [Fact]
    public void InfiniteRecursion_Throws()
    {
        var error = Assert.Throws<JyunoException>(() => Script.Run("func f", "  f", "endfunc", "f"));
        Assert.Contains("너무 깊습니다", error.Message);
    }

    [Fact]
    public void GrammarErrorInsideFunction_IsReturnedByRun()
    {
        var (result, _) = Script.Run("func f", "  out 'abc", "endfunc", "f");
        var error = Assert.IsType<GrammarError>(result);
        Assert.Equal(1, error.Line);
    }

    [Theory]
    [InlineData(0, "func f", "  out 1")]
    [InlineData(0, "endfunc")]
    [InlineData(0, "param a")]
    [InlineData(1, "func f", "  end", "endfunc", "f")]
    [InlineData(1, "func f", "  if 1", "endfunc", "f")]
    [InlineData(2, "while 1", "  func f", "    break", "  endfunc", "  f", "end")]
    public void BrokenFunctionStructure_ReturnsGrammarError(int line, params string[] script)
    {
        var (result, _) = Script.Run(script);
        var error = Assert.IsType<GrammarError>(result);
        Assert.Equal(line, error.Line);
    }

    [Theory]
    [InlineData("func")]
    [InlineData("func 1")]
    [InlineData("func f g")]
    public void Func_WithoutSingleName_Throws(string line)
    {
        Assert.Throws<JyunoException>(() => Script.Run(line, "endfunc"));
    }
}
