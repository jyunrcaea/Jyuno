namespace Jyuno.Language;

public interface FunctionInterface
{
    public object? Execute(params object?[] args);
}

public class NativeFunction : FunctionInterface
{
    public NativeFunction( Func<object?[] , object?> action)
    {
        func = action;
    }

    Func<object?[],object?> func;
    public object? Execute(params object?[] args) => func(args);
}

    
