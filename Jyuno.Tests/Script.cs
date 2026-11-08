namespace Jyuno.Tests;

//테스트용 실행기: 스크립트를 실행하고, out 함수로 출력한 값들을 모아서 돌려줌
internal static class Script
{
    public static (object? Result, List<object?> Output) Run(params string[] lines)
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
        Interpreter interpreter = runtime.Create(lines);
        Task<object?> task = Task.Run(() => interpreter.Run());
        if (Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult() != task)
            throw new TimeoutException("스크립트가 끝나지 않습니다. (무한 루프?)");
        return (task.GetAwaiter().GetResult(), output);
    }

    public static object? Eval(string expression) => new Runtime().Create().ExecuteLine(expression);
}
