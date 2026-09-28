using System.Runtime.CompilerServices;

namespace Jyuno.Tests;

public class RuntimeTests
{
    [Fact]
    public void RemovingGlobalFunction_OnlyAffectsThatInterpreter()
    {
        Runtime runtime = new();
        Interpreter first = runtime.Create();
        Interpreter second = runtime.Create();

        Assert.Equal(true, first.ExecuteLine("add ="));
        Assert.Equal(false, first.ExecuteLine("add ="));
        Assert.Throws<JyunoException>(() => first.ExecuteLine("add 1 2"));

        Assert.Equal(3L, second.ExecuteLine("add 1 2"));
        Assert.Equal(3L, runtime.Create().ExecuteLine("add 1 2"));
    }

    [Fact]
    public void RemovedGlobalName_CanBeReusedAsLocalVariable()
    {
        Interpreter interpreter = new Runtime().Create();
        interpreter.ExecuteLine("add =");
        interpreter.ExecuteLine("add = 5");
        Assert.Equal(5L, interpreter.ExecuteLine("add"));
        Assert.Equal(true, interpreter.ExecuteLine("add ="));
        Assert.Throws<JyunoException>(() => interpreter.ExecuteLine("add"));
    }

    [Fact]
    public void RemovingVariables_CanBeDisabled()
    {
        Interpreter interpreter = new Runtime().Create();
        interpreter.EnableRemoveVariable = false;
        Assert.Throws<JyunoException>(() => interpreter.ExecuteLine("add ="));
    }

    [Fact]
    public async Task AddingGlobalsWhileScriptsRun_IsThreadSafe()
    {
        Runtime runtime = new();
        using CancellationTokenSource stop = new();
        int started = 0;
        Task[] scripts = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            Interpreter interpreter = runtime.Create(new[] { "add 1 2" });
            Interlocked.Increment(ref started);
            while (!stop.IsCancellationRequested)
                Assert.Equal(3L, interpreter.ExecuteLine(0));
        })).ToArray();

        //스크립트가 모두 실행되는 도중에 전역 함수를 추가
        SpinWait.SpinUntil(() => Volatile.Read(ref started) == scripts.Length);
        for (int i = 0; i < 20000; i++)
            runtime.AddFunction($"f{i}", _ => null);
        stop.Cancel();
        await Task.WhenAll(scripts);
    }

    [Fact]
    public void Dispose_RemovesInterpreterFromRuntime()
    {
        Runtime runtime = new();
        Interpreter interpreter = runtime.Create();
        Assert.Contains(interpreter, runtime.Interpreters);
        interpreter.Dispose();
        Assert.DoesNotContain(interpreter, runtime.Interpreters);
    }

    [Fact]
    public void UndisposedInterpreter_CanBeGarbageCollected()
    {
        Runtime runtime = new();
        WeakReference interpreter = CreateAndForget(runtime);
        for (int i = 0; i < 10 && interpreter.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(interpreter.IsAlive);
        Assert.Empty(runtime.Interpreters);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference CreateAndForget(Runtime runtime) => new(runtime.Create(new[] { "x = 1" }));
}
