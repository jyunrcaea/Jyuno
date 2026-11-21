using System.Diagnostics;

namespace Jyuno.Tests;

//1.7.0: 메모리 한도(MemoryLimit), 한 줄 안에서의 취소, 이름/구조체의 스택 보호, 공유된 리스트의 비교
public class MemoryLimitTests
{
    const long MB = 1024 * 1024;

    static Interpreter Create(long limit , params string[] lines)
    {
        Interpreter interpreter = new Runtime().Create(lines);
        interpreter.MemoryLimit = limit;
        return interpreter;
    }

    static JyunoException LimitError(Interpreter interpreter)
    {
        JyunoException error = Assert.ThrowsAny<JyunoException>(() => interpreter.Run());
        Assert.Contains("메모리 한도" , error.Message);
        return error;
    }

    //──────── 한도와 설정 ────────

    [Fact]
    public void GrowingList_StopsNearLimit()
    {
        Interpreter interpreter = Create(32 * MB , "keep = list" , "while true" , "list.push keep (mul 'x' 10000)" , "end");
        LimitError(interpreter);
        //붙잡고 있는 값은 한도를 약간(최소 측정 간격 1MB + 미리 확인하지 않는 값 하나)까지만 넘음
        Assert.InRange(interpreter.MemoryUsage , 32 * MB , 34 * MB);
    }

    [Fact]
    public void SmallScripts_RunUnderLimit()
    {
        Interpreter interpreter = Create(16 * MB , "l = list" , "repeat 10000" , "list.push l 'abc'" , "end" , "s = mul 'a' 100000" , "return (add (length l) (length s))");
        Assert.Equal(110000L , interpreter.Run());
    }

    [Fact]
    public void LargeAllocation_IsRejectedBeforeAllocating()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        LimitError(Create(16 * MB , "x = array.new 100000000 0"));
        LimitError(Create(16 * MB , "x = array.range 100000000"));
        LimitError(Create(16 * MB , "x = mul 'ab' 100000000"));
        LimitError(Create(16 * MB , "x = string.padleft '' 100000000"));
        LimitError(Create(16 * MB , "s = mul 'a' 5000000" , "for c s" , "end"));
        //만들기 전에 포기했으므로 거의 할당하지 않음
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * MB);
    }

    [Fact]
    public void TemporaryValuesInOneLine_AreCounted()
    {
        //변수에 넣기 전의 큰 값(같은 줄의 다른 인자, 각 10MB)도 셈: 인자 30MB + 결과 30MB > 48MB
        LimitError(Create(48 * MB , "x = string.concat (mul 'a' 5000000) (mul 'b' 5000000) (mul 'c' 5000000)"));
        //함수가 돌려준 값도 그 줄이 끝날 때까지 셈
        LimitError(Create(48 * MB , "func big" , "return (mul 'a' 5000000)" , "endfunc" , "x = add (big) (big) (big)"));
        //한도가 충분하다면 실행됨
        Assert.Equal(15000000L , Create(80 * MB , "x = string.concat (mul 'a' 5000000) (mul 'b' 5000000) (mul 'c' 5000000)" , "return (length x)").Run());
    }

    [Fact]
    public void RuntimeLimit_IsDefault_AndInterpreterOverrides()
    {
        Runtime runtime = new() { MemoryLimit = 16 * MB };
        Interpreter limited = runtime.Create(new[] { "x = array.new 100000000 0" });
        Assert.Equal(16 * MB , limited.MemoryLimit);
        Assert.Throws<JyunoException>(() => limited.Run());

        Interpreter own = runtime.Create(new[] { "x = array.new 10000000 0" , "return (length x)" });
        own.MemoryLimit = 0;
        Assert.Equal(0 , own.MemoryLimit);
        Assert.Equal(10000000L , own.Run());

        //이미 만든 인터프리터에도 적용
        Interpreter later = runtime.Create(new[] { "x = array.new 10000000 0" , "return (length x)" });
        runtime.MemoryLimit = 0;
        Assert.Equal(10000000L , later.Run());

        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.MemoryLimit = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => own.MemoryLimit = -1);
    }

    [Fact]
    public void NoLimit_DoesNotMeasure()
    {
        Interpreter interpreter = Create(0 , "l = list" , "repeat 1000" , "list.push l (mul 'x' 1000)" , "end");
        interpreter.Run();
        Assert.Equal(0 , interpreter.MemoryUsage);
    }

    [Fact]
    public void InterpreterCanRunAgain_AfterLimitError()
    {
        Interpreter interpreter = Create(8 * MB , "x = array.new 100000000 0");
        LimitError(interpreter);
        interpreter.MemoryLimit = 0;
        Assert.Equal(3L , interpreter.ExecuteLine("(add 1 2)"));
    }

    //──────── 한도와 관계없는 최대 길이 ────────

    [Fact]
    public void HugeFormatWidth_IsErrorWithoutLimit()
    {
        Interpreter interpreter = Create(0 , "x = string.format '{0:D999999999}' 1");
        JyunoException error = Assert.ThrowsAny<JyunoException>(() => interpreter.Run());
        Assert.Contains("너무 깁니다" , error.Message);
        Assert.Equal("00042" , Script.Eval("string.format '{0:D5}' 42"));
    }

    [Fact]
    public void ConcatOverMaxLength_IsJyunoError()
    {
        Interpreter interpreter = Create(0 , "s = mul 'a' 500000000" , "t = s + s");
        Assert.ThrowsAny<JyunoException>(() => interpreter.Run());
    }

    //──────── 한 줄 안에서의 취소 ────────

    static void AssertCancels(Interpreter interpreter)
    {
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(300));
        interpreter.CancellationToken = cancel.Token;
        Task<object?> task = Task.Run(() => interpreter.Run());
        Assert.True(Task.WhenAny(task , Task.Delay(TimeSpan.FromSeconds(20))).GetAwaiter().GetResult() == task , "취소되지 않습니다.");
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
    }

    [Fact]
    public void InlinedExponentialRecursion_CanBeCancelled()
    {
        Interpreter interpreter = Create(0 , "func f" , "param n" , "return n < 1 ? 0 : (f (n - 1)) + (f (n - 1))" , "endfunc" , "x = f 100");
        interpreter.Inlining = true;
        AssertCancels(interpreter);
    }

    [Fact]
    public void SharedListToString_CanBeCancelled()
    {
        AssertCancels(Create(0 , "a = list 1" , "repeat 40" , "a = list a a" , "end" , "s = string a"));
    }

    //──────── 공유된 리스트, 구조체의 해시 ────────

    [Fact]
    public void StructWithSharedLists_ComparesQuickly()
    {
        Stopwatch watch = Stopwatch.StartNew();
        Interpreter interpreter = Create(0 , "struct box" , "param v" , "endstruct" , "a = list 1" , "repeat 40" , "a = list a a" , "end" ,
            "b = list 1" , "repeat 40" , "b = list b b" , "end" , "x = box a" , "y = box b" , "return (x == y)");
        Assert.Equal(true , interpreter.Run());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10) , $"{watch.Elapsed}");
        //다른 원소가 하나라도 있다면 다름
        Interpreter different = Create(0 , "struct box" , "param v" , "endstruct" , "a = list 1" , "repeat 30" , "a = list a a" , "end" ,
            "b = list 2" , "repeat 30" , "b = list b b" , "end" , "return ((box a) == (box b))");
        Assert.Equal(false , different.Run());
    }

    [Fact]
    public void CyclicListInStruct_StillComparesEqual()
    {
        Interpreter interpreter = Create(0 , "struct s" , "param items" , "endstruct" , "l = list" , "list.push l l" , "a = s l" , "b = a" , "return (equal a b)");
        Assert.Equal(true , interpreter.Run());
    }

    [Fact]
    public void DistinctOfManyStructs_IsFastAndCorrect()
    {
        Stopwatch watch = Stopwatch.StartNew();
        Interpreter interpreter = Create(0 , "struct p" , "param v" , "endstruct" , "l = list" , "for i (array.range 40000)" , "list.push l (p (mod i 20000))" , "end" ,
            "return (length (list.distinct l))");
        Assert.Equal(20000L , interpreter.Run());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10) , $"{watch.Elapsed}");
    }

    [Fact]
    public void StructHash_TreatsEqualNumbersAsSame()
    {
        //1과 1.0, 0.0과 -0.0은 같은 값이므로 중복 제거됨
        Assert.Equal(1L , Create(0 , "struct p" , "param v" , "endstruct" , "return (length (list.distinct (list (p 1) (p 1.0))))").Run());
        Assert.Equal(1L , Create(0 , "struct p" , "param v" , "endstruct" , "return (length (list.distinct (list (p 0.0) (p (0.0 * -1)))))").Run());
        Assert.Equal(2L , Create(0 , "struct p" , "param v" , "endstruct" , "return (length (list.distinct (list (p 1) (p '1'))))").Run());
    }

    //──────── 이름의 스택 보호 ────────

    [Fact]
    public void VeryLongDottedName_DoesNotCrashOrExplode()
    {
        string name = string.Join("." , Enumerable.Repeat("a" , 20000));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Interpreter interpreter = Create(0 , "a = 1" , "x = " + name);
        Assert.ThrowsAny<JyunoException>(() => interpreter.Run());
        //점 앞의 이름을 모두 만들어두지 않음 (이전에는 이름 길이의 제곱만큼 할당)
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * MB);
    }

    [Fact]
    public void DeepMemberChain_AtDeepCall_IsErrorInsteadOfCrash()
    {
        //구조체 값을 따라가는 긴 멤버 이름을 깊은 함수 호출 안에서 찾음 (1MB 스택)
        List<string> lines = new() { "struct node" , "param next" , "v = 1" , "endstruct" , "c = node null" , "repeat 3000" , "c = node c" , "end" };
        lines.AddRange(new[] { "func probe" , "param n" , "if (n > 0)" , "return (probe (n - 1))" , "end" , "tmp =" ,
            "return c." + string.Concat(Enumerable.Repeat("next." , 2900)) + "v" , "endfunc" , "tmp = 1" , "return (probe 900)" });
        Interpreter interpreter = Create(0 , lines.ToArray());
        interpreter.CallDepthLimit = 1000;
        Exception? error = null;
        object? result = null;
        Thread thread = new(() => {
            try { result = interpreter.Run(); }
            catch (Exception e) { error = e; }
        } , 1024 * 1024);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        //스택이 충분했다면 값을 찾고, 부족했다면 Jyuno 오류 (프로세스가 죽지 않음)
        Assert.True(error is null ? Equals(result , 1L) : error is JyunoException , error?.ToString());
    }
}
