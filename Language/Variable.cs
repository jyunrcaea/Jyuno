namespace Jyuno.Language;

public class VariableDictionary : Dictionary<string, dynamic?>
{
    public bool AddFunction(string name,Func<dynamic?[] ,dynamic?> native_function)
    {
        return this.TryAdd(name , new NativeFunction(native_function));
    }
    public bool AddVariable(string name,Func<dynamic?> get,Action<dynamic?> set)
    {
        return this.TryAdd(name , new NativeVariable(get , set));
    }
    public bool AddConstantVariable(string name,Func<dynamic?> get)
    {
        return AddVariable(name , get , _ => throw new JyunoException("상수에 값을 대입할수 없습니다."));
    }
}

public interface VariableInterface
{
    public void Set(dynamic? value);
    public dynamic? Get();
}

public class NativeVariable : VariableInterface
{
    public NativeVariable(Func<dynamic?> get, Action<dynamic?> set) {
        this.set_func = set;
        this.get_func = get;
    }

    Func<dynamic?> get_func;
    Action<dynamic?> set_func;

    public virtual void Set(dynamic? v) => set_func(v);
    public virtual dynamic? Get() => get_func();
}

public class JyunoVariable : VariableInterface
{
    dynamic? value;
    public JyunoVariable(dynamic value)
    {
        this.value = value;
    }
    public dynamic? Get() => value;
    public void Set(dynamic? v) => value= v;
}