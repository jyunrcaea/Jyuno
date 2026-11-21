using System.Diagnostics;
using Jyuno.Analysis;
using Jyuno.Language;

namespace Jyuno.Tests;

//시간 제한 블록 ttl (초) ~ endttl (1.7.0)
public class TtlTests
{
    sealed class Result
    {
        public object? Value;
        public List<object?> Output = new();
        public TimeSpan Elapsed;
    }

    static Interpreter Create(Runtime runtime , List<object?> output , string[] lines , bool optimize)
    {
        runtime.AddFunction("out" , args =>
        {
            if (output.Count >= 1000)
                throw new InvalidOperationException("출력이 너무 많습니다. (무한 루프?)");
            output.Add(args.Length > 0 ? args[0] : null);
            return null;
        });
        runtime.AddFunction("sleep" , args =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(Convert.ToDouble(args[0])));
            return null;
        });
        Interpreter interpreter = runtime.Create(lines);
        interpreter.EscapeAnalysis = optimize;
        interpreter.Inlining = optimize;
        return interpreter;
    }

    //스크립트를 실행하고 출력과 실행 시간을 돌려줌 (끝나지 않으면 실패)
    static Result Run(bool optimize , params string[] lines)
    {
        Result result = new();
        Interpreter interpreter = Create(new Runtime() , result.Output , lines , optimize);
        Stopwatch watch = Stopwatch.StartNew();
        Task<object?> task = Task.Run(() => interpreter.Run());
        if (Task.WhenAny(task , Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult() != task)
            throw new TimeoutException("스크립트가 끝나지 않습니다. (ttl이 빠져나가지 않음?)");
        result.Value = task.GetAwaiter().GetResult();
        result.Elapsed = watch.Elapsed;
        return result;
    }
    static Result Run(params string[] lines) => Run(false , lines);

    [Fact]
    public void UserExample_RunsBodyOnce()
    {
        var r = Run("sum = 0" , "t = 5" , "ttl t" , "   sum += 1" , "endttl" , "out sum");
        Assert.Equal(new object?[] { 1L } , r.Output);
        //본문이 일찍 끝나면 기다리지 않음
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(3) , r.Elapsed.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InfiniteLoop_LeavesAfterLimit(bool optimize)
    {
        var r = Run(optimize ,
            "i = 0" ,
            "ttl 0.1" ,
            "  while true" ,
            "    i += 1" ,
            "  end" ,
            "  out 'unreachable'" ,
            "endttl" ,
            "out 'after'" ,
            "return i");
        Assert.Equal(new object?[] { "after" } , r.Output);
        Assert.True((long)r.Value! > 0);
        Assert.True(r.Elapsed >= TimeSpan.FromMilliseconds(100) , r.Elapsed.ToString());
    }

    [Fact]
    public void Limit_IsMeasuredFromTtlLine()
    {
        //ttl 줄에 오기 전의 시간은 세지 않음
        var r = Run("sleep 150" , "n = 0" , "ttl 0.1" , "n += 1" , "endttl" , "out n");
        Assert.Equal(new object?[] { 1L } , r.Output);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0.5")]
    public void NonPositiveLimit_SkipsBody(string limit)
    {
        var r = Run("x = 0" , $"ttl {limit}" , "x = 1" , "endttl" , "out x");
        Assert.Equal(new object?[] { 0L } , r.Output);
    }

    [Fact]
    public void LimitExpression_UsesOperatorsAndCalls()
    {
        var r = Run("t = 5" , "ttl t * 2" , "out 'a'" , "endttl" , "ttl (div 1.0 10)" , "out 'b'" , "endttl" , "ttl math.pi" , "out 'c'" , "endttl");
        Assert.Equal(new object?[] { "a" , "b" , "c" } , r.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FunctionCalledInside_FramesAreCleanedUp(bool optimize)
    {
        var r = Run(optimize ,
            "func spin" ,
            "  param tag" ,
            "  local count = 0" ,
            "  while true" ,
            "    count += 1" ,
            "  end" ,
            "endfunc" ,
            "func depth" ,
            "  param n" ,
            "  if n > 0" ,
            "    return depth (n - 1)" ,
            "  end" ,
            "  return spin 'x'" ,
            "endfunc" ,
            "func sum" ,
            "  param n" ,
            "  return n > 0 ? (sum (n - 1)) + n : 0" ,
            "endfunc" ,
            "count = 'outer'" ,
            "ttl 0.05" ,
            "  depth 20" ,
            "endttl" ,
            "out count" ,
            //빠져나간 함수 호출이 남아있다면 호출 깊이 제한(200)에 걸림
            "out (sum 190)");
        Assert.Equal(new object?[] { "outer" , 18145L } , r.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InlinedExponentialRecursion_IsLimited(bool optimize)
    {
        //인라인된 호출은 줄을 실행하지 않으므로 호출마다 확인함
        var r = Run(optimize ,
            "func f" ,
            "  param n" ,
            "  return n < 1 ? 0 : (f (n - 1)) + (f (n - 1))" ,
            "endfunc" ,
            "x = 'before'" ,
            "ttl 0.05" ,
            "  x = f 60" ,
            "endttl" ,
            "out x");
        Assert.Equal(new object?[] { "before" } , r.Output);
    }

    [Fact]
    public void StructConstructorInside_IsLimited()
    {
        var r = Run(
            "struct forever" ,
            "  param n" ,
            "  while true" ,
            "    n += 1" ,
            "  end" ,
            "endstruct" ,
            "v = null" ,
            "ttl 0.05" ,
            "  v = forever 0" ,
            "endttl" ,
            "out v");
        Assert.Equal(new object?[] { null } , r.Output);
    }

    [Fact]
    public void NestedTtl_OuterLimitAlsoStopsInner()
    {
        var r = Run(
            "ttl 0.05" ,
            "  ttl 100" ,
            "    while true" ,
            "    end" ,
            "  endttl" ,
            "  out 'inner done'" ,
            "endttl" ,
            "out 'outer done'");
        Assert.Equal(new object?[] { "outer done" } , r.Output);
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(5) , r.Elapsed.ToString());
    }

    [Fact]
    public void NestedTtl_InnerLimitLeavesOnlyInner()
    {
        var r = Run(
            "ttl 100" ,
            "  ttl 0.03" ,
            "    while true" ,
            "    end" ,
            "  endttl" ,
            "  out 'inner done'" ,
            "endttl" ,
            "out 'outer done'");
        Assert.Equal(new object?[] { "inner done" , "outer done" } , r.Output);
    }

    [Fact]
    public void NestedTtl_InCalledFunction_OuterLimitUnwindsBoth()
    {
        var r = Run(
            "func work" ,
            "  ttl 100" ,
            "    while true" ,
            "    end" ,
            "  endttl" ,
            "  out 'work done'" ,
            "endfunc" ,
            "ttl 0.05" ,
            "  work" ,
            "  out 'after work'" ,
            "endttl" ,
            "out 'done'");
        Assert.Equal(new object?[] { "done" } , r.Output);
    }

    [Fact]
    public void TtlInsideLoop_CanBeEnteredAgain()
    {
        var r = Run(
            "n = 0" ,
            "while n < 3" ,
            "  ttl 0.02" ,
            "    while true" ,
            "    end" ,
            "  endttl" ,
            "  n += 1" ,
            "end" ,
            "out n");
        Assert.Equal(new object?[] { 3L } , r.Output);
        Assert.True(r.Elapsed >= TimeSpan.FromMilliseconds(60) , r.Elapsed.ToString());
    }

    [Fact]
    public void ExpiredInsideFor_ForVariablesDoNotLeak()
    {
        var r = Run(
            "x = 'outer'" ,
            "ttl 0.05" ,
            "  for x (array 1 2 3)" ,
            "    inner = 1" ,
            "    while true" ,
            "    end" ,
            "  end" ,
            "endttl" ,
            "out x" ,
            //for 안에서 만든 변수는 없음 (지우기에 실패하면 false)
            "out (inner =)" ,
            "sum = 0" ,
            "for y (array 1 2 3)" ,
            "  sum += y" ,
            "end" ,
            "out sum");
        Assert.Equal(new object?[] { "outer" , false , 6L } , r.Output);
    }

    [Fact]
    public void ExpiredInsideRepeat_RepeatStartsOverNextTime()
    {
        var r = Run(
            "k = 0" ,
            "repeat 2" ,
            "  ttl 0.02" ,
            "    repeat 5" ,
            "      k += 1" ,
            "      if k == 2" ,
            "        while true" ,
            "        end" ,
            "      end" ,
            "    end" ,
            "  endttl" ,
            "end" ,
            "out k");
        //두번째 ttl에서는 안쪽 repeat를 처음부터 다시 5번 실행
        Assert.Equal(new object?[] { 7L } , r.Output);
    }

    [Fact]
    public void ExpiredInsideFunctionFor_CallerForContinues()
    {
        var r = Run(
            "func spin" ,
            "  for z (array 1 2)" ,
            "    while true" ,
            "    end" ,
            "  end" ,
            "endfunc" ,
            "total = 0" ,
            "for i (array 1 2 3)" ,
            "  ttl 0.01" ,
            "    spin" ,
            "  endttl" ,
            "  total += i" ,
            "end" ,
            "out total");
        Assert.Equal(new object?[] { 6L } , r.Output);
    }

    [Fact]
    public void BreakAndGoto_LeaveTtl()
    {
        var r = Run(
            "while true" ,
            "  ttl 0.01" ,
            "    break" ,
            "  endttl" ,
            "end" ,
            "ttl 0.01" ,
            "  goto 9" ,
            "endttl" ,
            "out 'skipped'" ,
            //빠져나간 ttl의 제한 시간이 남아있다면 이후의 함수 호출에서 제한이 지났다고 판단할 것
            "sleep 50" ,
            "func f" ,
            "  return 1" ,
            "endfunc" ,
            "out (f)" ,
            "out 'ok'");
        Assert.Equal(new object?[] { 1L , "ok" } , r.Output);
    }

    [Fact]
    public void GotoInsideTtl_LoopsUntilLimit()
    {
        var r = Run(
            "n = 0" ,
            "ttl 0.05" ,
            "again:" ,
            "  n += 1" ,
            "  goto again" ,
            "endttl" ,
            "out (n > 10)");
        Assert.Equal(new object?[] { true } , r.Output);
    }

    [Fact]
    public void GotoToEndttl_LeavesNormally()
    {
        var r = Run("ttl 10" , "goto 3" , "out 'skipped'" , "endttl" , "out 'after'");
        Assert.Equal(new object?[] { "after" } , r.Output);
    }

    [Fact]
    public void Return_InsideTtl()
    {
        var r = Run(
            "func f" ,
            "  ttl 10" ,
            "    return 5" ,
            "  endttl" ,
            "  return 0" ,
            "endfunc" ,
            "out (f)" ,
            "ttl 10" ,
            "  return 'top'" ,
            "endttl");
        Assert.Equal(new object?[] { 5L } , r.Output);
        Assert.Equal("top" , r.Value);
    }

    [Fact]
    public void ErrorInsideTtl_IsNotSwallowed()
    {
        var interpreter = Create(new Runtime() , new() , new[] { "ttl 10" , "  x = div 1 0" , "endttl" } , false);
        var error = Assert.Throws<JyunoException>(() => interpreter.Run());
        Assert.Equal(1 , error.Line);
        //실행이 끝나면 ttl도 끝남 (다시 실행할수 있음)
        Assert.Throws<JyunoException>(() => interpreter.Run());
    }

    [Fact]
    public void CancellationToken_StillStopsInsideTtl()
    {
        var interpreter = Create(new Runtime() , new() , new[] { "ttl 100" , "  while true" , "  end" , "endttl" } , false);
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(100));
        interpreter.CancellationToken = cancel.Token;
        Task<object?> task = Task.Run(() => interpreter.Run());
        Assert.True(Task.WhenAny(task , Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult() == task , "취소되지 않습니다.");
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
    }

    [Fact]
    public void ExecuteLine_CallingFunctionWithTtl()
    {
        var output = new List<object?>();
        var interpreter = Create(new Runtime() , output , new[] { "func f" , "  ttl 0.02" , "    while true" , "    end" , "  endttl" , "  return 'left'" , "endfunc" } , false);
        interpreter.Run();
        Assert.Equal("left" , interpreter.ExecuteLine("f"));
        //ttl은 실행 위치를 옮기므로 Run()으로만 실행할수 있음
        Assert.Throws<JyunoException>(() => interpreter.ExecuteLine("ttl 1"));
        Assert.Throws<JyunoException>(() => interpreter.ExecuteLine("endttl"));
    }

    [Fact]
    public void NestedRunFromHost_IsLimited()
    {
        //ExecuteLine으로 호출한 함수의 ttl 안에서 C# 함수가 Run()을 다시 호출하면 그 실행도 제한을 받음
        var output = new List<object?>();
        Runtime runtime = new();
        Interpreter? interpreter = null;
        runtime.AddFunction("rerun" , args => interpreter!.Run(5));
        interpreter = Create(runtime , output , new[] {
            "func f" ,
            "  ttl 0.05" ,
            "    rerun" ,
            "  endttl" ,
            "  return 'left'" ,
            "while true" ,
            "end" ,
            "endfunc" } , false);
        interpreter.Run();
        Assert.Equal("left" , Task.Run(() => interpreter.ExecuteLine("f")).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
        Assert.Equal(-1 , interpreter.LastErrorLine);
    }

    [Theory]
    [InlineData("ttl")]
    [InlineData("ttl 'abc'")]
    [InlineData("ttl null")]
    [InlineData("ttl 1 2")]
    [InlineData("ttl (double 'NaN')")]
    public void InvalidLimit_IsError(string line)
    {
        var interpreter = Create(new Runtime() , new() , new[] { line , "endttl" } , false);
        var error = Assert.Throws<JyunoException>(() => interpreter.Run());
        Assert.Equal(0 , error.Line);
    }

    [Theory]
    [InlineData(0 , "ttl 1" , "out 1")]
    [InlineData(0 , "endttl")]
    [InlineData(1 , "ttl 1" , "while 1" , "endttl")]
    [InlineData(0 , "if 1" , "ttl 1" , "end")]
    [InlineData(1 , "ttl 1" , "if 1" , "endttl")]
    [InlineData(1 , "ttl 1" , "else" , "endttl")]
    [InlineData(1 , "ttl 1" , "break" , "endttl")]
    [InlineData(1 , "func f" , "endttl" , "endfunc" , "f")]
    public void BrokenBlockStructure_ReturnsGrammarError(int line , params string[] script)
    {
        var r = Run(script);
        var error = Assert.IsType<GrammarError>(r.Value);
        Assert.Equal(line , error.Line);
    }

    [Fact]
    public void TtlAndEndttl_AreReservedWords()
    {
        //다른 키워드처럼 이름으로 쓸수 없음
        Assert.IsType<GrammarError>(Run("ttl = 1").Value);
        Assert.IsType<GrammarError>(Run("endttl = 1").Value);
    }

    //──────── 분석 (Jyuno.Analysis) ────────

    [Fact]
    public void Analysis_TtlStatement()
    {
        var a = ScriptAnalysis.Analyze(new[] { "x = 0" , "ttl t * 2" , "  y = 1" , "  if x" , "  end" , "endttl" , "out y" });
        Assert.Empty(a.StructureErrors);
        var ttl = Assert.IsType<TtlStatement>(a.Root!.Statements[1]);
        Assert.Equal(StatementKind.Ttl , ttl.Kind);
        Assert.Equal(5 , ttl.EndLine);
        Assert.Equal("*" , Assert.IsType<BinaryExpression>(Assert.Single(ttl.Arguments)).Operator);
        Assert.Equal(2 , ttl.Body.Statements.Count);
        //ttl은 새 영역을 만들지 않음
        Assert.Same(a.TopScope , ttl.Body.Statements[0].Scope);
        Assert.Contains("y" , a.TopScope.Names.Keys);
        Assert.Contains("y" , ScriptAnalysis.NamesCreatedIn(a.Root.Statements));
    }

    [Fact]
    public void Analysis_GotoTargetsAroundTtl()
    {
        var a = ScriptAnalysis.Analyze(new[] { "ttl 1" , "goto 3" , "x = 1" , "endttl" , "goto 2" });
        var to_end = Assert.Single(Assert.Single(a.TopGotos , o => o.Line == 1).Targets);
        Assert.Equal(GotoTargetKind.After , to_end.Kind);
        Assert.IsType<TtlStatement>(to_end.Statement);
        //블록 안쪽으로 들어가는 goto
        Assert.Empty(Assert.Single(a.TopGotos , o => o.Line == 4).Targets);
    }

    [Fact]
    public void Analysis_StructureErrors()
    {
        Assert.NotEmpty(ScriptAnalysis.Analyze(new[] { "ttl 1" }).StructureErrors);
        Assert.NotEmpty(ScriptAnalysis.Analyze(new[] { "ttl 1" , "end" }).StructureErrors);
        Assert.NotEmpty(ScriptAnalysis.Analyze(new[] { "endttl" }).StructureErrors);
    }

    [Fact]
    public void Analysis_NativeJsRejectsTtl()
    {
        var errors = ScriptAnalysis.Analyze(new[] { "ttl 1" , "  x = 1" , "endttl" }).CheckNativeJs(new Runtime().GetGlobals());
        var error = Assert.Single(errors);
        Assert.Equal(0 , error.Line);
        Assert.Contains("ttl" , error.Message);
    }
}
