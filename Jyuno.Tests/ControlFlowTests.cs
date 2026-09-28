using Jyuno.Language;

namespace Jyuno.Tests;

public class ControlFlowTests
{
    [Fact]
    public void IfInsideWhile_DoesNotRestartLoop()
    {
        var (_, output) = Script.Run(
            "n = 3",
            "while n",
            "  if 1",
            "    out 'if'",
            "  end",
            "  out n",
            "  n = sub n 1",
            "end");
        Assert.Equal(new object?[] { "if", 3L, "if", 2L, "if", 1L }, output);
    }

    [Fact]
    public void Break_InsideIf_LeavesLoop()
    {
        var (_, output) = Script.Run(
            "n = 0",
            "while 1",
            "  n = add n 1",
            "  if (equal n 3)",
            "    break",
            "  end",
            "  out n",
            "end",
            "out 'done'");
        Assert.Equal(new object?[] { 1L, 2L, "done" }, output);
    }

    [Fact]
    public void Break_LeavesOnlyInnermostLoop()
    {
        var (_, output) = Script.Run(
            "repeat 2",
            "  while 1",
            "    out 'inner'",
            "    break",
            "  end",
            "  out 'outer'",
            "end");
        Assert.Equal(new object?[] { "inner", "outer", "inner", "outer" }, output);
    }

    [Fact]
    public void Repeat_RunsGivenTimes()
    {
        var (_, output) = Script.Run("repeat 3", "  out 'x'", "end");
        Assert.Equal(3, output.Count);
    }

    [Fact]
    public void NestedRepeat_RunsInnerLoopEveryTime()
    {
        var (_, output) = Script.Run(
            "repeat 2",
            "  repeat 3",
            "    out 'x'",
            "  end",
            "end");
        Assert.Equal(6, output.Count);
    }

    [Fact]
    public void Repeat_StartsOverAfterBreak()
    {
        var (_, output) = Script.Run(
            "repeat 2",
            "  n = 0",
            "  repeat 3",
            "    n = add n 1",
            "    if (equal n 2)",
            "      break",
            "    end",
            "  end",
            "  out n",
            "end");
        Assert.Equal(new object?[] { 2L, 2L }, output);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("'3'")]
    public void Repeat_WithoutPositiveInteger_SkipsBody(string count)
    {
        var (_, output) = Script.Run($"repeat {count}", "  out 'x'", "end", "out 'done'");
        Assert.Equal(new object?[] { "done" }, output);
    }

    [Fact]
    public void IfElse_RunsOneBranch()
    {
        var (_, output) = Script.Run(
            "if 0", "  out 'a'", "else", "  out 'b'", "end",
            "if 1", "  out 'c'", "else", "  out 'd'", "end");
        Assert.Equal(new object?[] { "b", "c" }, output);
    }

    [Fact]
    public void SkippedIf_SkipsNestedIfElseCompletely()
    {
        var (_, output) = Script.Run(
            "k = 1",
            "pass = 0",
            "top:",
            "if k",
            "  if 0",
            "    out 'inner-if'",
            "  else",
            "    out 'inner-else'",
            "  end",
            "  out 'outer-body'",
            "end",
            "out 'after'",
            "k = 0",
            "pass = add pass 1",
            "if (equal pass 1)",
            "  goto top",
            "end");
        Assert.Equal(new object?[] { "inner-else", "outer-body", "after", "after" }, output);
    }

    [Fact]
    public void GotoOutOfLoop_DoesNotAffectLaterEnd()
    {
        var (_, output) = Script.Run(
            "while 1",
            "  goto 3",
            "end",
            "if 1",
            "  out 'after'",
            "end",
            "out 'done'");
        Assert.Equal(new object?[] { "after", "done" }, output);
    }

    [Fact]
    public void ParenthesizedValues_AreEvaluated()
    {
        var (_, output) = Script.Run(
            "x = 1",
            "if (x)",
            "  out (x)",
            "end",
            "out (add (5) 1)",
            "out ((add 1 2))",
            "out (add ((add 1 2)) 4)");
        Assert.Equal(new object?[] { 1L, 6L, 3L, 7L }, output);
    }

    [Theory]
    [InlineData("out (add 1 2")]
    [InlineData("out (5")]
    [InlineData("out (5 6)")]
    public void UnclosedParenthesis_Throws(string line)
    {
        Assert.Throws<JyunoException>(() => Script.Run(line));
    }

    [Theory]
    [InlineData(0, "if 0", "out 1")]
    [InlineData(0, "while 1", "out 1")]
    [InlineData(0, "repeat 2", "out 1")]
    [InlineData(1, "out 1", "end")]
    [InlineData(0, "else")]
    [InlineData(0, "break")]
    [InlineData(2, "if 0", "else", "else", "end")]
    [InlineData(2, "if 0", "else", "break", "end")]
    [InlineData(2, "goto 2", "if 1", "else", "out 1")]
    [InlineData(2, "goto 2", "while 1", "break")]
    public void BrokenBlockStructure_ReturnsGrammarError(int line, params string[] script)
    {
        var (result, _) = Script.Run(script);
        var error = Assert.IsType<GrammarError>(result);
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public void GrammarError_StopsRun()
    {
        var (result, output) = Script.Run("out 1", "out 'unterminated", "out 2");
        Assert.IsType<GrammarError>(result);
        Assert.Equal(new object?[] { 1L }, output);
    }
}
