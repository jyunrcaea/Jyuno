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
    //인자가 모두 상수일때 컴파일하면서 미리 계산해도 되는지 (같은 인자에 항상 같은 값을 돌려주고 부수 효과가 없는 내장 함수만)
    internal Func<object?[] , bool>? Foldable { get; init; }
}

    
