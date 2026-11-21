using Jyuno.Analysis;
using Jyuno.Language;

namespace Jyuno.Tests;

//try ~ catch (이름) ~ tried 와 throw (1.7.0)
public class TryCatchTests
{
    static (Interpreter Interpreter, List<object?> Output) Create(Runtime runtime , string[] lines , bool optimize = false)
    {
        List<object?> output = new();
        runtime.AddFunction("out" , args =>
        {
            if (output.Count >= 1000)
                throw new InvalidOperationException("출력이 너무 많습니다. (무한 루프?)");
            output.Add(args.Length > 0 ? args[0] : null);
            return null;
        });
        Interpreter interpreter = runtime.Create(lines);
        interpreter.EscapeAnalysis = optimize;
        interpreter.Inlining = optimize;
        return (interpreter, output);
    }

    //최적화를 끈 실행과 켠 실행의 결과가 같아야 함
    static (object? Result, List<object?> Output) Run(params string[] lines)
    {
        var (plain , plain_output) = Create(new Runtime() , lines);
        object? result = plain.Run();
        var (optimized , optimized_output) = Create(new Runtime() , lines , true);
        Assert.Equal(result , optimized.Run());
        Assert.Equal(plain_output , optimized_output);
        return (result, plain_output);
    }

    //──────── 잡기 ────────

    [Fact]
    public void RuntimeError_IsCaughtAsMessage()
    {
        var (_ , output) = Run(
            "try" ,
            "   zero = 0" ,
            "   x = div 1 zero" ,
            "   out 'not reached'" ,
            "catch e" ,
            "   out e" ,
            "tried" ,
            "out 'after'");
        Assert.Equal(new object?[] { "0으로 나눌수 없습니다." , "after" } , output);
    }

    [Fact]
    public void NoError_SkipsCatchBody()
    {
        var (_ , output) = Run("try" , "out 1" , "catch e" , "out 2" , "tried" , "out 3");
        Assert.Equal(new object?[] { 1L , 3L } , output);
    }

    [Theory]
    [InlineData("42" , 42L)]
    [InlineData("'text'" , "text")]
    [InlineData("null" , null)]
    [InlineData("1.5" , 1.5)]
    [InlineData("true" , true)]
    [InlineData("(1 + 2) * 3" , 9L)]
    public void AnyValue_CanBeThrown(string value , object? expected)
    {
        var (_ , output) = Run("try" , "throw " + value , "catch e" , "out e" , "tried");
        Assert.Equal(new[] { expected } , output);
    }

    [Fact]
    public void ThrownStruct_IsCopy()
    {
        var (_ , output) = Run(
            "struct point" , "param x" , "endstruct" ,
            "p = point 1" ,
            "try" , "throw p" , "catch e" , "e.x = 5" , "tried" ,
            "out p.x" , "out e.x");
        Assert.Equal(new object?[] { 1L , 5L } , output);
    }

    [Fact]
    public void ErrorInNestedCalls_IsCaughtAndStateIsRestored()
    {
        var (result , output) = Run(
            "func f" , "param n" , "if n == 0" , "throw 'deep'" , "end" , "local x = n" , "return f (n - 1)" , "endfunc" ,
            "x = 'outer'" ,
            "total = 0" ,
            "for i (array 1 2 3)" ,
            "  try" ,
            "    for j (array 10 20)" ,
            "      total += j" ,
            "      if i == 2" , "f 5" , "end" ,
            "    end" ,
            "  catch e" ,
            "    out (e + ' ' + i)" ,
            "  tried" ,
            "end" ,
            "out x" ,
            "return total");
        Assert.Equal(new object?[] { "deep 2" , "outer" } , output);
        Assert.Equal(70L , result);
    }

    [Fact]
    public void CatchVariable_IsLocalInFunction()
    {
        var (_ , output) = Run(
            "e = 'global'" ,
            "func f" , "try" , "throw 1" , "catch e" , "out e" , "tried" , "out e" , "endfunc" ,
            "f" ,
            "out e");
        Assert.Equal(new object?[] { 1L , 1L , "global" } , output);
    }

    [Fact]
    public void VariablesCreatedInCatch_RemainAfterTried()
    {
        var (_ , output) = Run("try" , "x = undefined_name" , "catch" , "x = 0" , "tried" , "out x");
        Assert.Equal(new object?[] { 0L } , output);
    }

    [Fact]
    public void NestedTry_ErrorInCatchGoesOutward()
    {
        var (_ , output) = Run(
            "try" ,
            "  try" , "    throw 'a'" , "  catch e" , "    out e" , "    throw (e + 'b')" , "  tried" ,
            "  out 'not reached'" ,
            "catch e" ,
            "  out e" ,
            "tried");
        Assert.Equal(new object?[] { "a" , "ab" } , output);
    }

    [Fact]
    public void ReturnBreakGoto_LeaveTry()
    {
        var (_ , output) = Run(
            "func f" , "try" , "return 1" , "catch e" , "tried" , "return 2" , "endfunc" ,
            "out (f)" ,
            "func g" , "try" , "throw 0" , "catch e" , "return 3" , "tried" , "return 4" , "endfunc" ,
            "out (g)" ,
            "i = 0" ,
            "while true" , "try" , "i += 1" , "if i == 3" , "break" , "end" , "catch e" , "tried" , "end" ,
            "out i" ,
            "try" , "goto 33" , "catch e" , "tried" ,
            "out 'skipped'" ,
            "out 'done'");
        Assert.Equal(new object?[] { 1L , 3L , 3L , "done" } , output);
    }

    [Fact]
    public void RepeatInsideTry_IsResetAfterCatch()
    {
        var (_ , output) = Run(
            "c = 0" ,
            "repeat 2" ,
            "  try" , "    repeat 5" , "      c += 1" , "      if c == 2" , "throw 0" , "end" , "    end" , "  catch" , "  tried" ,
            "end" ,
            "out c");
        Assert.Equal(new object?[] { 7L } , output);
    }

    //──────── 다시 던지기 ────────

    [Fact]
    public void BareThrow_RethrowsOriginalErrorAndLine()
    {
        var (interpreter , output) = Create(new Runtime() , new[] { "try" , "x = div 1 0" , "catch e" , "out e" , "throw" , "tried" });
        JyunoException error = Assert.Throws<JyunoException>(() => interpreter.Run());
        Assert.Equal("0으로 나눌수 없습니다." , error.Message);
        Assert.Equal(1 , error.Line);
        Assert.Single(output);
    }

    [Fact]
    public void ThrowValue_UncaughtReachesHost()
    {
        var (interpreter , _) = Create(new Runtime() , new[] { "try" , "x = div 1 0" , "catch e" , "throw e" , "tried" });
        JyunoThrownException error = Assert.Throws<JyunoThrownException>(() => interpreter.Run());
        Assert.Equal("0으로 나눌수 없습니다." , error.Message);
        Assert.Equal("0으로 나눌수 없습니다." , error.Value);
        Assert.Equal(3 , error.Line);
    }

    [Fact]
    public void ThrownStruct_ReachesHostAsStructValue()
    {
        var (interpreter , _) = Create(new Runtime() , new[] { "struct point" , "param x" , "endstruct" , "throw (point 7)" });
        JyunoThrownException error = Assert.Throws<JyunoThrownException>(() => interpreter.Run());
        StructValue value = Assert.IsAssignableFrom<StructValue>(error.Value);
        Assert.Equal(7L , value["x"]);
        Assert.Equal("point { x = 7 }" , error.Message);
        Assert.Equal(3 , error.Line);
    }

    [Fact]
    public void Throw_WorksInExecuteLine()
    {
        Interpreter interpreter = new Runtime().Create();
        JyunoThrownException error = Assert.Throws<JyunoThrownException>(() => interpreter.ExecuteLine("throw 1 + 1"));
        Assert.Equal(2L , error.Value);
    }

    [Fact]
    public void HostFunction_CanThrowCatchableErrors()
    {
        Runtime runtime = new();
        runtime.AddFunction("fail" , args => throw new JyunoException("host says no"));
        runtime.AddFunction("fail_value" , args => throw new JyunoThrownException(args[0]));
        var (interpreter , output) = Create(runtime , new[] {
            "try" , "fail" , "catch e" , "out e" , "tried" ,
            "try" , "fail_value (list 1 2)" , "catch e" , "out (length e)" , "tried" });
        interpreter.Run();
        Assert.Equal(new object?[] { "host says no" , 2L } , output);
    }

    //──────── 잡지 않는 것 ────────

    [Fact]
    public void HostException_IsNotCaught()
    {
        Runtime runtime = new();
        runtime.AddFunction("fail" , args => throw new InvalidOperationException("abort"));
        var (interpreter , output) = Create(runtime , new[] { "try" , "fail" , "catch e" , "out 'caught'" , "tried" });
        Assert.Throws<InvalidOperationException>(() => interpreter.Run());
        Assert.Empty(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallDepthLimit_IsNotCaught(bool optimize)
    {
        var (interpreter , output) = Create(new Runtime() , new[] {
            "func r" , "param n" , "return r (n + 1)" , "endfunc" ,
            "try" , "r 0" , "catch e" , "out 'caught'" , "tried" } , optimize);
        Assert.Throws<JyunoLimitException>(() => interpreter.Run());
        Assert.Empty(output);
    }

    [Fact]
    public void MemoryLimit_IsNotCaught()
    {
        var (interpreter , output) = Create(new Runtime() , new[] {
            "try" , "keep = list" , "while true" , "list.push keep (mul 'x' 10000)" , "end" , "catch e" , "out 'caught'" , "tried" });
        interpreter.MemoryLimit = 16 * 1024 * 1024;
        JyunoLimitException error = Assert.Throws<JyunoLimitException>(() => interpreter.Run());
        Assert.Contains("메모리 한도" , error.Message);
        Assert.Empty(output);
    }

    [Fact]
    public void Cancellation_IsNotCaught()
    {
        var (interpreter , output) = Create(new Runtime() , new[] { "try" , "while true" , "end" , "catch e" , "out 'caught'" , "tried" });
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(50));
        interpreter.CancellationToken = cancel.Token;
        Assert.ThrowsAny<OperationCanceledException>(() => interpreter.Run());
        Assert.Empty(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TtlExpiry_PassesThroughTry(bool optimize)
    {
        var (interpreter , output) = Create(new Runtime() , new[] {
            "func spin" , "try" , "while true" , "end" , "catch e" , "out 'caught'" , "tried" , "endfunc" ,
            "ttl 0.05" , "try" , "while true" , "end" , "catch e" , "out 'caught'" , "tried" , "endttl" ,
            "out 1" ,
            "ttl 0.05" , "spin" , "endttl" ,
            "out 2" ,
            "try" , "ttl 0.05" , "spin" , "endttl" , "out 3" , "catch e" , "out 'caught'" , "tried" } , optimize);
        Task<object?> task = Task.Run(() => interpreter.Run());
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(new object?[] { 1L , 2L , 3L } , output);
    }

    [Fact]
    public void GrammarError_IsNotCaught()
    {
        var (interpreter , output) = Create(new Runtime() , new[] {
            "func f" , "if true" , "endfunc" ,
            "try" , "f" , "catch e" , "out 'caught'" , "tried" });
        Assert.IsType<GrammarError>(interpreter.Run());
        Assert.Empty(output);
    }

    //──────── 문법 ────────

    [Theory]
    [InlineData("try|x = 1|tried" , "try와 tried 사이에는 catch가 있어야 합니다.")]
    [InlineData("try|catch e|end" , "try 키워드는 tried 키워드로 종료 표시가 있어야 합니다.")]
    [InlineData("catch e" , "catch는 try와 tried 사이에 한번만 쓸수 있습니다.")]
    [InlineData("try|throw 1|catch e|catch f|tried" , "catch는 try와 tried 사이에 한번만 쓸수 있습니다.")]
    [InlineData("tried" , "tried와 짝이 되는 try 키워드가 없습니다.")]
    [InlineData("try 1|catch|tried" , "try 다음에는 아무것도 올수 없습니다.")]
    [InlineData("try|catch a b|tried" , "catch 다음에는 오류를 받을 변수 이름 하나만 올수 있습니다.")]
    [InlineData("try|catch 1|tried" , "catch 다음에는 오류를 받을 변수 이름 하나만 올수 있습니다.")]
    [InlineData("throw" , "값 없는 throw는 catch와 tried 사이에서만 쓸수 있습니다. (처리중인 오류를 다시 던짐)")]
    [InlineData("try|throw|catch|tried" , "값 없는 throw는 catch와 tried 사이에서만 쓸수 있습니다. (처리중인 오류를 다시 던짐)")]
    [InlineData("try|throw 1|catch|func f|throw|endfunc|f|tried" , "값 없는 throw는 catch와 tried 사이에서만 쓸수 있습니다. (처리중인 오류를 다시 던짐)")]
    [InlineData("while false|try|catch|end" , "end와 짝이 되는 if, while, repeat, for 키워드가 없습니다. (try는 tried로 닫아야 합니다)")]
    public void StructureErrors(string script , string message)
    {
        object? result = new Runtime().Create(script.Split('|')).Run();
        Assert.IsType<GrammarError>(result);
        Assert.Contains(ScriptAnalysis.Analyze(script.Split('|')).StructureErrors , o => o.Message == message);
    }

    [Fact]
    public void GotoIntoTryOrCatch_IsError()
    {
        foreach (string target in new[] { "3" , "6" })
        {
            var (interpreter , output) = Create(new Runtime() , new[] {
                "goto " + target , "try" , "out 1" , "out 2" , "catch e" , "out 3" , "out 4" , "tried" });
            JyunoException error = Assert.Throws<JyunoException>(() => interpreter.Run());
            Assert.Equal("goto로 try/catch 블록 안쪽으로 들어갈수 없습니다." , error.Message);
        }
        //catch/tried 줄로의 이동은 try 블록을 끝냄
        var (_ , ended) = Run("try" , "out 1" , "goto 4" , "out 2" , "catch e" , "out 3" , "tried" , "out 4");
        Assert.Equal(new object?[] { 1L , 4L } , ended);
    }

    [Fact]
    public void ThrowArguments()
    {
        Interpreter interpreter = new Runtime().Create(new[] { "throw 1 2" });
        Assert.Equal("throw 다음에는 던질 값 하나만 와야 합니다." , Assert.Throws<JyunoException>(() => interpreter.Run()).Message);
        Assert.Equal("'try' 키워드는 Run()으로 실행할 때만 사용할수 있습니다." , Assert.Throws<JyunoException>(() => new Runtime().Create().ExecuteLine("try")).Message);
    }

    [Fact]
    public void FunctionWithThrow_IsNotInlined_ButStillWorks()
    {
        var (_ , output) = Run(
            "func check" , "param v" , "throw v" , "endfunc" ,
            "func twice" , "param v" , "return (check v) * 2" , "endfunc" ,
            "try" , "out (twice 4)" , "catch e" , "out e" , "tried");
        Assert.Equal(new object?[] { 4L } , output);
    }

    //──────── 분석 (Jyuno.Analysis) ────────

    [Fact]
    public void Analysis_BuildsTryStatement()
    {
        string[] lines = { "try" , "  x = div 1 0" , "catch err" , "  y = err" , "  throw" , "tried" , "throw 1" };
        ScriptAnalysis analysis = ScriptAnalysis.Analyze(lines);
        Assert.Empty(analysis.StructureErrors);
        TryStatement statement = Assert.IsType<TryStatement>(analysis.Root!.Statements[0]);
        Assert.Equal(2 , statement.CatchLine);
        Assert.Equal(5 , statement.EndLine);
        Assert.Equal("err" , statement.CatchName);
        Assert.Single(statement.Body.Statements);
        Assert.Equal(2 , statement.CatchBody.Statements.Count);
        Assert.True(Assert.IsType<ThrowStatement>(statement.CatchBody.Statements[1]).IsRethrow);
        Assert.False(Assert.IsType<ThrowStatement>(analysis.Root.Statements[1]).IsRethrow);
        //catch의 이름은 지금 영역에 만들어짐
        Assert.True(analysis.TopScope.Names.ContainsKey("err"));
        Assert.True(analysis.TopScope.Names.ContainsKey("x"));
        var errors = analysis.CheckNativeJs(new Runtime().GetGlobals());
        Assert.Contains(errors , o => o.Line == 0 && o.Message.Contains("try/catch"));
        Assert.Contains(errors , o => o.Line == 6 && o.Message.Contains("throw"));
    }

    [Fact]
    public void NewKeywords_DoNotBreakDottedNames()
    {
        Runtime runtime = new();
        runtime.AddFunction("host.throw" , args => 1L);
        Assert.Equal(1L , runtime.Create(new[] { "return host.throw" }).Run());
    }
}
