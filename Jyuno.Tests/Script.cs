using Jyuno.Compiler;
namespace Jyuno.Tests;

//테스트용 실행기: 스크립트를 실행하고, out 함수로 출력한 값들을 모아서 돌려줌
internal static class Script
{
    //제한 모드로 빌드할수 있는 스크립트는 바이너리(.jyc v2)로 저장/검증/연결해서도 실행하고 결과를 비교함
    public static int StaticCompared, StaticSkipped, IlCompiled, IlFallback;
    static Script()
    {
        //비교한 개수 기록 (JYUNO_STATIC_REPORT 환경 변수가 있을 때)
        if (Environment.GetEnvironmentVariable("JYUNO_STATIC_REPORT") is string path)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => File.WriteAllText(path, $"compared={StaticCompared} skipped={StaticSkipped} il={IlCompiled} il_fallback={IlFallback}");
    }
    public static (object? Result, List<object?> Output) Run(params string[] lines)
    {
        //[실험] 테스트 스크립트 모으기 (타입 검사 통과율 측정용)
        if (Environment.GetEnvironmentVariable("JYUNO_DUMP_SCRIPTS") is string dump)
            lock (typeof(Script)) File.AppendAllText(dump, System.Text.Json.JsonSerializer.Serialize(string.Join("\n", lines)) + "\n");
        var source = Capture(() => RunOnce(lines, false));
        (object? Result, List<object?> Output, Exception? Error) compiled;
        try
        {
            compiled = Capture(() => RunOnce(lines, true));
        }
        catch (Jyuno.Compiler.JyunoBuildException)
        {
            Interlocked.Increment(ref StaticSkipped);
            compiled = source;
        }
        if (!ReferenceEquals(compiled.Output, source.Output))
        {
            Interlocked.Increment(ref StaticCompared);
            Compare(lines, source, compiled, "제한 모드");
            //[실험] 같은 프로그램을 타입 추론 + IL 컴파일로도 실행
            var il = Capture(() => RunOnce(lines, true, JyunoEngine.Il));
            Compare(lines, source, il, "IL");
        }
        if (source.Error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(source.Error);
        return (source.Result, source.Output);
    }

    static void Compare(string[] lines, (object? Result, List<object?> Output, Exception? Error) source, (object? Result, List<object?> Output, Exception? Error) compiled, string engine)
    {
        string script = engine + "\n" + string.Join("\n", lines);
        Assert.True(source.Error?.GetType() == compiled.Error?.GetType(), $"예외 종류가 다름: {source.Error?.GetType().Name} / {compiled.Error?.GetType().Name} ({compiled.Error?.Message})\n{script}");
        Assert.True(source.Error?.Message == compiled.Error?.Message, $"예외 메시지가 다름: {source.Error?.Message} / {compiled.Error?.Message}\n{script}");
        Assert.True(lines.Any(o => o.Contains("math.random") || o.Contains("math.csprng")) || Equals(source.Result, compiled.Result) || (source.Result is System.Collections.IEnumerable a && compiled.Result is System.Collections.IEnumerable b && a.Cast<object?>().SequenceEqual(b.Cast<object?>())), $"결과가 다름: {source.Result} / {compiled.Result}\n{script}");
        Assert.True(source.Output.Count == compiled.Output.Count, $"출력 개수가 다름\n{script}");
        //난수를 쓰는 스크립트는 값이 실행마다 다르므로 개수만 비교 (값은 각 테스트가 검사)
        if (lines.Any(o => o.Contains("math.random") || o.Contains("math.csprng")))
            return;
        for (int i = 0; i < source.Output.Count; i++)
            Assert.True(Equals(source.Output[i], compiled.Output[i]) || (source.Output[i] is System.Collections.IEnumerable x && compiled.Output[i] is System.Collections.IEnumerable y && x.Cast<object?>().SequenceEqual(y.Cast<object?>())), $"{i}번째 출력이 다름: {source.Output[i]} / {compiled.Output[i]}\n{script}");
    }

    static (object? Result, List<object?> Output, Exception? Error) Capture(Func<(object?, List<object?>)> run)
    {
        try
        {
            var (result, output) = run();
            return (result, output, null);
        }
        catch (Jyuno.Compiler.JyunoBuildException)
        {
            throw;
        }
        catch (Exception e)
        {
            return (null, new(), e);
        }
    }

    static (object? Result, List<object?> Output) RunOnce(string[] lines, bool compiled, JyunoEngine engine = JyunoEngine.Closure)
    {
        Runtime runtime = new();
        List<object?> output = new();
        runtime.AddFunction("out", args =>
        {
            //무한 루프에 빠지더라도 테스트가 멈추지 않도록
            if (output.Count >= 1000)
                throw new InvalidOperationException("출력이 너무 많습니다. (무한 루프?)");
            output.Add(args.Length > 0 ? args[0] : null);
            return null;
        });
        Func<object?> run;
        if (compiled)
        {
            byte[] image = Jyuno.Compiler.JyunoProgramImage.Build(lines, runtime);
            JyunoProgram program = Jyuno.Compiler.JyunoProgramImage.Load(image, runtime, engine);
            if (engine is JyunoEngine.Il)
                Interlocked.Increment(ref program.Engine == "Il" ? ref IlCompiled : ref IlFallback);
            run = program.Run;
        }
        else
            { Interpreter interpreter = runtime.Create(lines); run = () => interpreter.Run(); }
        //스레드 풀이 아닌 별도 스레드에서 실행 (한 테스트가 세 번 실행하므로, 스레드 풀에서 기다리면 병렬 테스트 중에 풀이 모자라서 시간 초과가 날수 있음)
        object? result = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        Thread thread = new(() =>
        {
            try { result = run(); }
            catch (Exception e) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        }) { IsBackground = true };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("스크립트가 끝나지 않습니다. (무한 루프?)");
        error?.Throw();
        return (result, output);
    }

    public static object? Eval(string expression) => new Runtime().Create().ExecuteLine(expression);
}
