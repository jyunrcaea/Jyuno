using Jyuno.Language;

namespace Jyuno;

public class Runtime
{
    public Runtime(AddJyunoCommandType create = AddJyunoCommandType.Default)
    {
        JyunoCommands.AddDefault(Global);
        if (create.HasFlag(AddJyunoCommandType.Console))
            JyunoCommands.AddConsole(Global);
        if (create.HasFlag(AddJyunoCommandType.Math))
            JyunoCommands.AddMath(Global);
        if (create.HasFlag(AddJyunoCommandType.File))
            JyunoCommands.AddFile(Global);
    }
    internal VariableDictionary Global = new();
    public bool AddFunction(string name,Func<object?[],object?> func)
    {
        lock (Global)
        {
            return Global.AddFunction(name , func);
        }
    }
    public bool AddVariable(string name,Func<object?> get, Action<object?> set)
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
        /// <summary>
        /// Jyuno의 필수 명령어 (제외할수 없습니다.)
        /// </summary>
        Essential = 0,
        Console = 1,
        Math = 2,
        /// <summary>
        /// 주의, 이 플래그를 포함할 경우, 사용자의 파일 및 디렉터리를 조작할수 있습니다.
        /// </summary>
        File = 4,
        /// <summary>
        /// 사용자의 보안에 해를 끼치지 않는 Jyuno의 기본적인 기능이 포함되어있습니다. (콘솔 입출력, 수학 등)
        /// </summary>
        Default = Essential | Console | Math,
        /// <summary>
        /// Jyuno에서 제공할수 있는 모든 명령어가 포함됩니다. 사용자의 보안에 악영향을 끼칠수 있으므로 주의하세요.
        /// </summary>
        All = Essential | Console | Math | File
    }
}