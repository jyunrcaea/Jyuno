using Jyuno.Language;

namespace Jyuno.Tests;

public class RunTests
{
    [Fact]
    public void Return_GivesValue()
    {
        Assert.Equal(5L, Script.Run("return 5").Result);
        Assert.Equal("a", Script.Run("return 'a' 'b'").Result);
        Assert.Null(Script.Run("return").Result);
    }

    [Fact]
    public void Run_CanRunAgainAfterReturn()
    {
        Interpreter interpreter = new Runtime().Create(new[] { "return 1", "return 2" });
        Assert.Equal(1L, interpreter.Run());
        Assert.Equal(1L, interpreter.Run());
        Assert.Equal(-1, interpreter.CurrentExecuteLine);
    }

    [Fact]
    public void Run_CanRunAgainAfterException()
    {
        Interpreter interpreter = new Runtime().Create(new[] { "unknown_function" });
        var first = Assert.Throws<JyunoException>(() => interpreter.Run());
        var second = Assert.Throws<JyunoException>(() => interpreter.Run());
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(-1, interpreter.CurrentExecuteLine);
    }

    [Fact]
    public void Run_InsideRun_Throws()
    {
        Runtime runtime = new();
        Interpreter? interpreter = null;
        runtime.AddFunction("again", _ => interpreter!.Run());
        interpreter = runtime.Create(new[] { "again" });
        var error = Assert.Throws<JyunoException>(() => interpreter.Run());
        Assert.Contains("이미 실행중", error.Message);
    }

    [Fact]
    public void ExecuteLine_FlowKeyword_ThrowsWithoutBreakingRun()
    {
        Interpreter interpreter = new Runtime().Create(new[] { "return 7" });
        Assert.Throws<JyunoException>(() => interpreter.ExecuteLine("if 0"));
        Assert.Throws<JyunoException>(() => interpreter.ExecuteLine("goto 0"));
        Assert.Equal(-1, interpreter.CurrentExecuteLine);
        Assert.Equal(7L, interpreter.Run());
    }

    [Fact]
    public void ExecuteLine_Return_GivesReturnInfo()
    {
        Interpreter interpreter = new Runtime().Create();
        var info = Assert.IsType<ReturnInfo>(interpreter.ExecuteLine("return 3"));
        Assert.Equal(3L, info.value);
    }

    [Fact]
    public void ExecuteLine_DuringRun_KeepsRunPosition()
    {
        Runtime runtime = new();
        Interpreter? interpreter = null;
        runtime.AddFunction("inner", _ => interpreter!.ExecuteLine("x = 1"));
        interpreter = runtime.Create(new[] { "inner", "here:", "return here" });
        Assert.Equal(1, interpreter.Run());
    }

    [Fact]
    public void Label_OutsideRun_StoresItsLine()
    {
        Interpreter interpreter = new Runtime().Create(new[] { "x = 1" });
        interpreter.ExecuteLine("here:");
        Assert.Equal(1, interpreter.ExecuteLine("here"));
    }

    [Fact]
    public void Goto_ToEnd_FinishesScript()
    {
        var (_, output) = Script.Run("goto 2", "out 'skipped'");
        Assert.Empty(output);
    }

    [Fact]
    public void Goto_JumpsToLineNumber()
    {
        var (_, output) = Script.Run("goto 2", "out 'skipped'", "out 'reached'");
        Assert.Equal(new object?[] { "reached" }, output);
    }

    [Fact]
    public void Goto_JumpsToLabel()
    {
        var (_, output) = Script.Run(
            "n = 0",
            "top:",
            "n = add n 1",
            "out n",
            "if (sub 3 n)",
            "  goto top",
            "end",
            "out 'done'");
        Assert.Equal(new object?[] { 1L, 2L, 3L, "done" }, output);
    }

    [Theory]
    [InlineData("goto -1")]
    [InlineData("goto 100")]
    [InlineData("goto 'a'")]
    [InlineData("goto")]
    public void Goto_InvalidTarget_Throws(string line)
    {
        Assert.Throws<JyunoException>(() => Script.Run(line));
    }
}
