namespace Jyuno.Tests;

//테스트용 실행기: 스크립트를 실행하고, out 함수로 출력한 값들을 모아서 돌려줌
internal static class Script
{
    //제한 모드로 빌드할수 있는 스크립트는 바이너리(.jyc v2)로 저장/검증/연결해서도 실행하고 결과를 비교함
    public static int StaticCompared, StaticSkipped;
    static Script()
    {
        //비교한 개수 기록 (JYUNO_STATIC_REPORT 환경 변수가 있을 때)
        if (Environment.GetEnvironmentVariable("JYUNO_STATIC_REPORT") is string path)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => File.WriteAllText(path, $"compared={StaticCompared} skipped={StaticSkipped}");
    }
    public static (object? Result, List<object?> Output) Run(params string[] lines)
    {
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
            string script = string.Join("\n", lines);
            Assert.True(source.Error?.GetType() == compiled.Error?.GetType(), $"예외 종류가 다름: {source.Error?.GetType().Name} / {compiled.Error?.GetType().Name} ({compiled.Error?.Message})\n{script}");
            Assert.True(source.Error?.Message == compiled.Error?.Message, $"예외 메시지가 다름: {source.Error?.Message} / {compiled.Error?.Message}\n{script}");
            Assert.True(Equals(source.Result, compiled.Result) || (source.Result is System.Collections.IEnumerable a && compiled.Result is System.Collections.IEnumerable b && a.Cast<object?>().SequenceEqual(b.Cast<object?>())), $"결과가 다름: {source.Result} / {compiled.Result}\n{script}");
            Assert.True(source.Output.Count == compiled.Output.Count, $"출력 개수가 다름\n{script}");
            for (int i = 0; i < source.Output.Count; i++)
                Assert.True(Equals(source.Output[i], compiled.Output[i]) || (source.Output[i] is System.Collections.IEnumerable x && compiled.Output[i] is System.Collections.IEnumerable y && x.Cast<object?>().SequenceEqual(y.Cast<object?>())), $"{i}번째 출력이 다름: {source.Output[i]} / {compiled.Output[i]}\n{script}");
        }
        if (source.Error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(source.Error);
        return (source.Result, source.Output);
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

    static (object? Result, List<object?> Output) RunOnce(string[] lines, bool compiled)
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
            JyunoProgram program = Jyuno.Compiler.JyunoProgramImage.Load(image, runtime);
            run = program.Run;
        }
        else
            { Interpreter interpreter = runtime.Create(lines); run = () => interpreter.Run(); }
        Task<object?> task = Task.Run(run);
        if (Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult() != task)
            throw new TimeoutException("스크립트가 끝나지 않습니다. (무한 루프?)");
        return (task.GetAwaiter().GetResult(), output);
    }

    public static object? Eval(string expression) => new Runtime().Create().ExecuteLine(expression);
}
