namespace Jyuno.Tests;

//테스트용 실행기: 스크립트를 실행하고, out 함수로 출력한 값들을 모아서 돌려줌
internal static class Script
{
    //소스로 실행한 결과와, 미리 컴파일한 바이너리(.jyc)로 실행한 결과가 같은지도 함께 검사함
    public static (object? Result, List<object?> Output) Run(params string[] lines)
    {
        (object? Result, List<object?> Output, Exception? Error) source = Capture(() => RunOnce(lines, false));
        (object? Result, List<object?> Output, Exception? Error) image = Capture(() => RunOnce(lines, true));
        Assert.Equal(source.Error?.GetType(), image.Error?.GetType());
        Assert.Equal(source.Error?.Message, image.Error?.Message);
        Assert.Equal(source.Result, image.Result);
        Assert.Equal(source.Output, image.Output);
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
        catch (Exception e)
        {
            return (null, new(), e);
        }
    }

    static (object? Result, List<object?> Output) RunOnce(string[] lines, bool image)
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
        Interpreter interpreter = image ? runtime.Load(Jyuno.Compiler.JyunoImage.Build(lines)) : runtime.Create(lines);
        Task<object?> task = Task.Run(() => interpreter.Run());
        if (Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult() != task)
            throw new TimeoutException("스크립트가 끝나지 않습니다. (무한 루프?)");
        return (task.GetAwaiter().GetResult(), output);
    }

    public static object? Eval(string expression) => new Runtime().Create().ExecuteLine(expression);
}
