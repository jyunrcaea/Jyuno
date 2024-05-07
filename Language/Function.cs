namespace Jyuno.Language;

public interface FunctionInterface
{
    public dynamic? Execute(params dynamic?[] args);
}

public class NativeFunction : FunctionInterface
{
    public NativeFunction( Func<dynamic?[] , dynamic?> action)
    {
        func = action;
    }

    Func<dynamic?[],dynamic?> func;
    public dynamic? Execute(params dynamic?[] args) => func(args);
}

    
