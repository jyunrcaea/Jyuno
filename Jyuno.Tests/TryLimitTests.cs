namespace Jyuno.Tests;

//1.7.0 통합: try/catch와 메모리 한도(MemoryLimit), 시간 제한 블록(ttl), 취소를 함께 쓸 때
public class TryLimitTests
{
    const long MB = 1024 * 1024;

    static Interpreter Create(long limit , bool optimize , List<object?>? output , params string[] lines)
    {
        Runtime runtime = new();
        runtime.AddFunction("out" , args =>
        {
            output?.Add(args.Length > 0 ? args[0] : null);
            return null;
        });
        Interpreter interpreter = runtime.Create(lines);
        interpreter.MemoryLimit = limit;
        interpreter.EscapeAnalysis = optimize;
        interpreter.Inlining = optimize;
        return interpreter;
    }

    static object? RunWithTimeout(Interpreter interpreter)
    {
        Task<object?> task = Task.Run(() => interpreter.Run());
        if (Task.WhenAny(task , Task.Delay(TimeSpan.FromSeconds(20))).GetAwaiter().GetResult() != task)
            throw new TimeoutException("스크립트가 끝나지 않습니다.");
        return task.GetAwaiter().GetResult();
    }

    static JyunoLimitException LimitError(Interpreter interpreter)
    {
        JyunoLimitException error = Assert.Throws<JyunoLimitException>(() => RunWithTimeout(interpreter));
        Assert.Contains("메모리 한도" , error.Message);
        return error;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandledThrownValues_AreCounted(bool optimize)
    {
        //catch 본문이 실행되는 동안 처리중인 값(이름 없는 catch라도 값 없는 throw로 다시 던질수 있으므로 붙잡음)도 셈
        //재귀마다 8MB 배열을 던지고 catch 본문에서 다시 호출: 세지 않으면 50번 × 8MB = 400MB를 붙잡고도 64MB 한도에 걸리지 않음
        Interpreter interpreter = Create(64 * MB , optimize , null ,
            "func f" , "param n" , "try" , "throw (array.new 1000000 0)" , "catch" , "if n > 0" , "f (n - 1)" , "end" , "tried" , "endfunc" ,
            "f 50");
        LimitError(interpreter);
        //던진 배열들을 셌으므로 잰 크기가 한도 가까이 (다음 8MB를 만들기 전에 거절하므로 한도보다 조금 작을수 있음)
        Assert.InRange(interpreter.MemoryUsage , 48 * MB , 80 * MB);

        //catch 변수에 넣은 값은 변수로 셈 (같은 값을 두번 세지 않음)
        Interpreter named = Create(64 * MB , optimize , null ,
            "func f" , "param n" , "try" , "throw (array.new 1000000 0)" , "catch e" , "if n > 0" , "f (n - 1)" , "end" , "tried" , "endfunc" ,
            "f 50");
        LimitError(named);

        //한도 안이라면 그대로 실행되고, catch가 끝나면 놓음
        Assert.Equal(10L , RunWithTimeout(Create(64 * MB , optimize , null ,
            "count = 0" , "repeat 10" , "try" , "throw (array.new 1000000 0)" , "catch" , "count += 1" , "tried" , "end" , "return count")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MemoryLimitInsideTry_InsideTtl_IsNotCaughtOrSwallowed(bool optimize)
    {
        //ttl 안의 try에서 한도를 넘음: catch도, ttl도 아니고 오류로 멈춤
        List<object?> output = new();
        LimitError(Create(32 * MB , optimize , output ,
            "ttl 30" , "try" , "keep = list" , "while true" , "list.push keep (mul 'x' 10000)" , "end" , "catch e" , "out 'caught'" , "tried" , "endttl" , "out 'after'"));
        Assert.Empty(output);

        //try 안의 ttl도 같음
        LimitError(Create(32 * MB , optimize , output ,
            "try" , "ttl 30" , "keep = list" , "while true" , "list.push keep (mul 'x' 10000)" , "end" , "endttl" , "catch e" , "out 'caught'" , "tried" , "out 'after'"));
        Assert.Empty(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporariesInsideTryAndCatch_AreReleasedEveryLine(bool optimize)
    {
        //try 본문과 catch 본문의 줄마다 만든 600KB 임시 값은 다음 줄로 넘어가면 놓음 (ttl 안에서도)
        Assert.Equal(400L , RunWithTimeout(Create(24 * MB , optimize , null ,
            "n = 0" ,
            "ttl 60" ,
            "try" , "repeat 200" , "s = mul 'x' 300000" , "n += 1" , "end" , "throw 'done'" ,
            "catch e" , "repeat 200" , "s = mul 'y' 300000" , "n += 1" , "end" , "tried" ,
            "endttl" ,
            "return n")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaughtErrorInsideTtl_ThenTtlExpires(bool optimize)
    {
        //ttl 안에서 오류를 잡은 뒤 catch 본문에서 시간이 지남: endttl 다음으로 빠져나가고 처리중인 오류는 정리됨
        List<object?> output = new();
        Interpreter interpreter = Create(32 * MB , optimize , output ,
            "func spin" , "while true" , "s = mul 'y' 300000" , "end" , "endfunc" ,
            "ttl 0.2" , "try" , "x = div 1 0" , "catch e" , "out e" , "spin" , "tried" , "endttl" ,
            "out 'after'" ,
            //빠져나간 뒤에도 한도는 그대로 동작
            "keep = list" , "while true" , "list.push keep (mul 'z' 10000)" , "end");
        LimitError(interpreter);
        Assert.Equal(2 , output.Count);
        Assert.Equal("after" , output[1]);
    }

    [Fact]
    public void Cancellation_InsideTryInsideTtl_IsNotCaught()
    {
        List<object?> output = new();
        Interpreter interpreter = Create(64 * MB , true , output ,
            "ttl 30" , "try" , "while true" , "s = mul 'x' 100000" , "end" , "catch e" , "out 'caught'" , "tried" , "endttl" , "out 'after'");
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(200));
        interpreter.CancellationToken = cancel.Token;
        Assert.ThrowsAny<OperationCanceledException>(() => RunWithTimeout(interpreter));
        Assert.Empty(output);
    }
}
