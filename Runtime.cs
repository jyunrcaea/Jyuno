using Jyuno.Language;

namespace Jyuno;

public class Runtime
{
    public Runtime(AddJyunoCommandType create = AddJyunoCommandType.Default)
    {
        JyunoCommand.AddDefault(Global);
        if (create.HasFlag(AddJyunoCommandType.Console))
            JyunoCommand.AddConsole(Global);
        if (create.HasFlag(AddJyunoCommandType.Math))
            JyunoCommand.AddMath(Global);
        if (create.HasFlag(AddJyunoCommandType.Runtime))
            JyunoCommand.AddRuntime(Global , this);
        if (create.HasFlag(AddJyunoCommandType.File))
            JyunoCommand.AddFile(Global);
    }
    internal VariableDictionary Global = new();
    public bool AddFunction(string name,Func<dynamic?[],dynamic?> func)
    {
        lock (Global)
        {
            return Global.AddFunction(name , func);
        }
    }
    public bool AddVariable(string name,Func<dynamic?> get, Action<dynamic?> set)
    {
        lock (Global)
        {
            return Global.AddVariable(name , get , set);
        }
    }
    public HashSet<Interpreter> Interpreters { get; } = new();
    public Interpreter Create(string[]? script = null)
    {
        Interpreter interpret = new(this , script ?? Array.Empty<string>());
        lock(Interpreters)
            Interpreters.Add(interpret);
        return interpret;
    }

    [Flags]
    public enum AddJyunoCommandType
    {
        Essential = 0,
        Console = 1,
        Math = 2,
        /// <summary>
        /// 주의, 이 플래그를 포함할 경우, 사용자의 파일 및 디렉터리를 조작할수 있습니다.
        /// </summary>
        File = 4,
        /// <summary>
        /// 주의, 이 플래그를 포함할 경우 프로그램의 악의적인 조작을 가할수도 있습니다.
        /// </summary>
        Runtime = 1024,

        Default = Essential | Console | Math,
        All = Essential | Console | Math | File | Runtime
    }
}