using Jyuno.Language;
using System.Runtime.CompilerServices;

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
    //인터프리터가 다른 스레드에서 읽을수 있으므로, 항상 lock (Global) 안에서 사용해야 함
    internal readonly VariableDictionary Global = new();
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
    //Jyuno의 필수 명령어 이름 (add, length 등)
    static readonly HashSet<string> essential_names = create_essential_names();
    static HashSet<string> create_essential_names()
    {
        VariableDictionary dict = new();
        JyunoCommands.AddDefault(dict);
        return dict.Keys.ToHashSet();
    }
    /// <summary>
    /// 이 런타임의 전역 이름(필수 명령어, 콘솔/수학 명령어, 프로그램이 추가한 함수/변수)을 돌려줍니다.
    /// 코드를 실행하지 않고 분석하거나 다른 언어로 변환할때 사용합니다. (예: <see cref="Analysis.ScriptAnalysis.CheckNativeJs"/>)
    /// </summary>
    public IReadOnlyDictionary<string , Analysis.GlobalName> GetGlobals()
    {
        lock (Global)
        {
            Dictionary<string , Analysis.GlobalName> result = new();
            foreach (var (name , holder) in Global)
            {
                bool builtin = essential_names.Contains(name);
                result[name] = holder switch {
                    NativeConstantVariable constant => new(name , Analysis.GlobalKind.Constant , builtin , constant.Get()),
                    VariableInterface => new(name , Analysis.GlobalKind.Variable , builtin , null),
                    _ => new(name , Analysis.GlobalKind.Function , builtin , null)
                };
            }
            return result;
        }
    }
    //Dispose 하지 않은 인터프리터도 GC가 수거할수 있도록 약한 참조로 보관 (스레드 안전)
    readonly ConditionalWeakTable<Interpreter , object> interpreters = new();
    static readonly object alive = new();
    /// <summary>
    /// 이 런타임에서 생성된 인터프리터 중, 아직 Dispose 되지 않았고 사용중인 인터프리터 목록입니다.
    /// </summary>
    public IReadOnlyCollection<Interpreter> Interpreters => interpreters.Select(pair => pair.Key).ToList();
    public Interpreter Create(string[]? script = null)
    {
        Interpreter interpret = new(this , script ?? Array.Empty<string>());
        interpreters.Add(interpret , alive);
        return interpret;
    }
    internal void RemoveInterpreter(Interpreter interpreter)
    {
        interpreters.Remove(interpreter);
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