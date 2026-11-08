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
    //[실험] 호출 자리 특화에서 인자 배열 없이 바로 계산할수 있는 내장 함수의 종류
    internal Intrinsic Intrinsic { get; init; }
}

//[실험] 특화할수 있는 내장 함수
internal enum Intrinsic : byte { None, Add, Sub, Mul, Div, Mod, Equal, Less, Greater, And, Or, Not }

    
