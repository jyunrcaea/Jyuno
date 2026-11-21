using System.Diagnostics;

namespace Jyuno.Tests;

//1.7.0 통합: 시간 제한 블록(ttl)과 메모리 한도(MemoryLimit)를 함께 쓸 때
public class TtlMemoryTests
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
        //다른 스레드에서 만든 큰 문자열을 돌려주는 C# 함수 (글자 수)
        runtime.AddFunction("hostbig" , args => Task.Run(() => new string('h' , Convert.ToInt32(args[0]))).GetAwaiter().GetResult());
        Interpreter interpreter = runtime.Create(lines);
        interpreter.MemoryLimit = limit;
        interpreter.EscapeAnalysis = optimize;
        interpreter.Inlining = optimize;
        return interpreter;
    }

    //끝나지 않으면 실패 (ttl이 빠져나가지 않거나 한도 오류가 나지 않는 경우)
    static object? RunWithTimeout(Interpreter interpreter)
    {
        Task<object?> task = Task.Run(() => interpreter.Run());
        if (Task.WhenAny(task , Task.Delay(TimeSpan.FromSeconds(20))).GetAwaiter().GetResult() != task)
            throw new TimeoutException("스크립트가 끝나지 않습니다.");
        return task.GetAwaiter().GetResult();
    }

    static JyunoException LimitError(Interpreter interpreter)
    {
        JyunoException error = Assert.ThrowsAny<JyunoException>(() => RunWithTimeout(interpreter));
        Assert.Contains("메모리 한도" , error.Message);
        return error;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MemoryLimitError_InsideTtl_IsNotSwallowed(bool optimize)
    {
        //ttl의 줄에서 한도를 넘음: 오류이므로 endttl 다음으로 빠져나가지 않음
        List<object?> output = new();
        LimitError(Create(16 * MB , optimize , output , "ttl 30" , "x = array.new 100000000 0" , "endttl" , "out 'after'"));
        Assert.Empty(output);

        //ttl 안에서 호출한 함수(시간을 확인하는 반복문으로 실행)에서 한도를 넘음
        output.Clear();
        LimitError(Create(16 * MB , optimize , output , "func f" , "keep = list" , "while true" , "list.push keep (mul 'x' 10000)" , "end" , "endfunc" , "ttl 30" , "f" , "endttl" , "out 'after'"));
        Assert.Empty(output);
    }

    [Fact]
    public void GrowingList_InsideTtl_StopsAtLimitBeforeTtlExpires()
    {
        Interpreter interpreter = Create(32 * MB , false , null , "keep = list" , "ttl 30" , "while true" , "list.push keep (mul 'x' 10000)" , "end" , "endttl");
        Stopwatch watch = Stopwatch.StartNew();
        LimitError(interpreter);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20) , watch.Elapsed.ToString());
        Assert.InRange(interpreter.MemoryUsage , 32 * MB , 34 * MB);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporariesInsideTtl_AreReleasedEveryLine(bool optimize)
    {
        //줄마다 만든 600KB의 임시 값(변수에 넣기 전까지 붙잡는 값)은 다음 줄로 넘어가면 놓아야 함
        //(ttl 블록의 반복문이 줄 단위로 놓지 않으면 200번 × 600KB = 120MB가 블록이 끝날 때까지 쌓여서 한도를 넘음)
        Assert.Equal(200L , RunWithTimeout(Create(24 * MB , optimize , null ,
            "n = 0" , "ttl 60" , "repeat 200" , "s = mul 'x' 300000" , "n += 1" , "end" , "endttl" , "return n")));

        //ttl 안에서 호출한 함수의 본문도 같음
        Assert.Equal(200L , RunWithTimeout(Create(24 * MB , optimize , null ,
            "func f" , "n = 0" , "repeat 200" , "s = mul 'x' 300000" , "n += 1" , "end" , "return n" , "endfunc" ,
            "ttl 60" , "r = f" , "endttl" , "return r")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueReturnedInsideTtl_IsCountedUntilLineEnds(bool optimize)
    {
        //함수가 돌려준 값(각 10MB)은 호출한 줄이 끝날 때까지 셈 (ttl 밖에서와 같음)
        LimitError(Create(48 * MB , optimize , null , "func big" , "return (mul 'a' 5000000)" , "endfunc" , "ttl 30" , "x = add (big) (big) (big)" , "endttl"));
        //C# 함수가 다른 스레드에서 만들어 돌려준 값도 ttl 안에서 바로 확인
        LimitError(Create(16 * MB , optimize , null , "ttl 30" , "x = hostbig 20000000" , "endttl"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TtlExpiry_WithMemoryLimit_ContinuesAndStillMeasures(bool optimize)
    {
        //함수 안의 무한 반복에서 ttl이 끝남 → 호출 정보와 붙잡은 값이 정리되고 endttl 다음 줄로
        List<object?> output = new();
        Interpreter interpreter = Create(32 * MB , optimize , output ,
            "func spin" , "local big = mul 'x' 6000000" , "while true" , "s = mul 'y' 300000" , "end" , "endfunc" ,
            "ttl 0.2" , "spin" , "endttl" ,
            "out 'after'" ,
            //함수 안에서 붙잡던 값(12MB)이 남아 있지 않아야 10MB 문자열을 만들수 있음 (mul은 만들기 전에 글자당 4바이트, 20MB를 확인)
            "x = mul 'a' 5000000" ,
            "out (length x)" ,
            //그 뒤에도 한도는 그대로 동작
            "keep = list" , "while true" , "list.push keep (mul 'z' 10000)" , "end");
        LimitError(interpreter);
        Assert.Equal(new object?[] { "after" , 5000000L } , output);
    }

    [Fact]
    public void TtlExpiry_InDeepRecursion_RestoresCallState()
    {
        //ttl 안의 깊은 재귀에서 시간이 지남: 모든 호출의 영역과 붙잡은 값을 정리한 뒤에도 같은 인터프리터를 다시 실행할수 있음
        List<object?> output = new();
        //호출마다 400KB × 50 = 20MB를 붙잡은 채로 시간이 지남. 남아 있다면 뒤의 문자열(만들기 전에 32MB를 확인)과 합쳐서 한도를 넘음
        Interpreter interpreter = Create(48 * MB , false , output ,
            "func down" , "param n" , "local s = mul 'x' 200000" , "if n > 0" , "down (n - 1)" , "end" , "while true" , "end" , "endfunc" ,
            "ttl 0.2" , "down 50" , "endttl" ,
            "out 'after'" , "return (length (mul 'a' 8000000))");
        Assert.Equal(8000000L , RunWithTimeout(interpreter));
        Assert.Equal(8000000L , RunWithTimeout(interpreter));
        Assert.Equal(new object?[] { "after" , "after" } , output);
    }

    [Fact]
    public void Cancellation_InsideTtl_WithMemoryLimit_IsNotTtlExit()
    {
        //취소는 ttl의 시간 제한과 별개로 실행을 멈춤 (endttl 다음으로 이어서 실행하지 않음)
        List<object?> output = new();
        Interpreter interpreter = Create(64 * MB , true , output , "ttl 30" , "while true" , "s = mul 'x' 100000" , "end" , "endttl" , "out 'after'");
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(200));
        interpreter.CancellationToken = cancel.Token;
        Assert.ThrowsAny<OperationCanceledException>(() => RunWithTimeout(interpreter));
        Assert.Empty(output);
    }
}
