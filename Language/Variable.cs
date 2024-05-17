namespace Jyuno.Language;

public class VariableDictionary : Dictionary<string, object?>
{
    public bool AddFunction(string name,Func<object?[] ,object?> native_function)
    {
        return this.TryAdd(name , new NativeFunction(native_function));
    }
    public bool AddVariable(string name,Func<object?> get,Action<object?> set)
    {
        return this.TryAdd(name , new NativeVariable(get , set));
    }
    public bool AddConstantVariable(string name,Func<object?> get)
    {
        return AddVariable(name , get , _ => throw new JyunoException("상수에 값을 대입할수 없습니다."));
    }
}

public interface VariableInterface
{
    public void Set(object? value);
    public object? Get();
}

public class NativeVariable : VariableInterface
{
    public NativeVariable(Func<object?> get, Action<object?> set) {
        this.set_func = set;
        this.get_func = get;
    }

    Func<object?> get_func;
    Action<object?> set_func;

    public virtual void Set(object? v) => set_func(v);
    public virtual object? Get() => get_func();
}

public class JyunoVariable : VariableInterface
{
    object? value;
    public JyunoVariable(object? value)
    {
        this.value = value;
    }
    public object? Get() => value;
    public virtual void Set(object? v) => value= v;
}

public class JyunoConstantVariable : JyunoVariable
{
    public JyunoConstantVariable(object? value) : base(value) { }
    public override void Set(object? v)
    {
        throw new JyunoException("상수를 변경할수 없습니다.");
    }
}