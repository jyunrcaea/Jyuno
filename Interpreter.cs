using Jyuno.Compiler;
using Jyuno.Language;
using System.Runtime.ExceptionServices;

namespace Jyuno;

public partial class Interpreter : IDisposable
{
    public Interpreter(Runtime runtime , IEnumerable<string> script)
    {
        this.runtime = runtime;
        foreach(var text in script)
        {
            scripts.Add(new(text));
        }
        //다른 인터프리터와 독립되어야 함.
        current = top = new Scope(null);
    }
    public bool EnableRemoveVariable { get; set; } = true;
    public bool BlockSubstitute { get; set; } = false;
    /// <summary>[실험, 1.5.2부터] 탈출 분석: 방금 만들어진 구조체 값은 다시 복사하지 않고, 읽기만 하는 내장 함수에는 복사하지 않고 넘깁니다. 실행 결과는 끈 것과 같습니다.</summary>
    public bool EscapeAnalysis { get; set; } = false;
    /// <summary>[실험, 1.5.2부터] 인라이닝: param 줄들과 return 한 줄로만 된 함수는 함수 영역과 호출 정보를 만들지 않고 바로 계산합니다. 실행 결과는 끈 것과 같습니다.</summary>
    public bool Inlining { get; set; } = false;
    public Runtime runtime { get; init; }
    public int CurrentExecuteLine { get; private set; } = -1;

    //변수 영역: 스크립트 최상위 영역, 또는 함수 호출 한번의 지역 영역
    //함수 영역의 Parent는 호출한 쪽이 아니라 함수를 만든 영역이므로, 다른 함수의 지역 변수는 보이지 않음
    sealed class Scope
    {
        public Scope(Scope? parent)
        {
            Parent = parent;
        }
        public readonly VariableDictionary Variables = new();
        public readonly Scope? Parent;
        //이 영역에 이름이 추가/교체/삭제될 때마다 증가 (이름 해석 캐시 검증용)
        public long Version;
        //다른 영역에서 이름을 찾을때 이 영역을 거쳐간 적이 있는지 (그렇다면 이 영역이 바뀔때 모든 캐시를 무효화)
        public bool Shared;
        //구조체 값 하나의 영역이라면 그 구조체 값 (필드와 구조체 함수가 이 영역에 있음)
        public StructInstance? Instance;
        //구조체 값을 담은 변수 때문에 선언된 멤버 이름(me.name 등) → 실제로 가리키는 구조체 값과 그 안의 이름
        public Dictionary<string , (StructInstance Instance, string Name)>? Aliases;
    }
    //지금 실행중인 영역 (처음에는 스크립트 최상위 영역, for 문 안에서는 반복 한번의 영역)
    Scope current;
    //스크립트 최상위 영역
    readonly Scope top;
    //이 인터프리터에서만 삭제한 전역 함수/변수 (전역 목록은 다른 인터프리터와 공유하므로 직접 지우지 않음)
    HashSet<string> removed_globals = new();
    //Shared 영역 또는 removed_globals가 바뀔 때마다 증가 (이름 해석 캐시 검증용)
    long bindings_version = 0;

    //이름 해석 캐시: 컴파일된 줄에서 이름이 나오는 자리마다 하나씩 있으며, 마지막으로 찾은 결과를 기억함
    //(전역 목록은 추가만 되고 삭제/교체되지 않으므로, 한번 찾은 전역 함수/변수는 이 인터프리터에서 삭제하기 전까지 그대로임)
    sealed class NameSite
    {
        public NameSite(string name)
        {
            Name = name;
        }
        public readonly string Name;
        //1단계: 같은 영역에서 다시 찾는 경우 (while 문 등)
        public Scope? Scope;
        public long ScopeVersion, Version;
        public object? Holder;
        //2단계: 바깥 영역이 같은 새 영역에서 찾는 경우 (for 문의 반복 한번, 같은 함수의 다른 호출 등)
        public Scope? Parent;
        public long ParentVersion;
        public object? ParentHolder;
        //3단계: 전역 목록에서 찾은 경우 (removed_globals는 늘어나기만 하므로 개수로 검증)
        public object? Global;
        public int GlobalRemoved = -1;
    }
    //영역에 이름이 추가/교체/삭제되었음을 알림
    void scope_changed(Scope scope)
    {
        scope.Version++;
        if (scope.Shared)
            bindings_version++;
    }
    //지금 영역에서 이름을 찾음 (지금 영역 → 바깥 영역 → 전역 순서)
    bool resolve(NameSite site , out object? holder)
    {
        Scope scope = current;
        if (site.Scope == scope && site.ScopeVersion == scope.Version && site.Version == bindings_version)
        {
            holder = site.Holder;
            return true;
        }
        Scope? parent = scope.Parent;
        if (parent is not null && site.Parent == parent && site.ParentVersion == bindings_version && !scope.Variables.ContainsKey(site.Name))
        {
            holder = site.ParentHolder;
            remember(site , scope , holder);
            return true;
        }
        return resolve_slow(site , out holder);
    }
    bool resolve_slow(NameSite site , out object? holder)
    {
        Scope scope = current;
        string name = site.Name;
        if (scope.Variables.TryGetValue(name , out holder))
        {
            remember(site , scope , holder);
            return true;
        }
        bool found = false;
        for (Scope? outer = scope.Parent ; outer is not null ; outer = outer.Parent)
        {
            //이 영역이 바뀌면 이 결과도 바뀔수 있음
            outer.Shared = true;
            if (outer.Variables.TryGetValue(name , out holder))
            {
                found = true;
                break;
            }
        }
        if (!found)
        {
            if (site.GlobalRemoved == removed_globals.Count)
            {
                holder = site.Global;
            }
            else
            {
                if (!search_global(name , out holder))
                    return false;
                site.Global = holder;
                site.GlobalRemoved = removed_globals.Count;
            }
        }
        remember(site , scope , holder);
        if (scope.Parent is not null)
        {
            site.Parent = scope.Parent;
            site.ParentVersion = bindings_version;
            site.ParentHolder = holder;
        }
        return true;
    }
    void remember(NameSite site , Scope scope , object? holder)
    {
        site.Scope = scope;
        site.ScopeVersion = scope.Version;
        site.Version = bindings_version;
        site.Holder = holder;
    }
    bool search_global(string name , out object? value)
    {
        if (!removed_globals.Contains(name))
        {
            //전역 목록은 다른 스레드에서 함수/변수가 추가될수 있으므로 잠금
            lock (runtime.Global)
            {
                if (runtime.Global.TryGetValue(name , out value)) return true;
            }
        }
        value = null;
        return false;
    }
    void substitute_variable(string name , object? value)
    {
        //새로 만드는 변수는 항상 지금 영역에 둠 (전역 목록은 다른 인터프리터와 공유함)
        VariableDictionary dict = current.Variables;
        if (!dict.TryAdd(name , value))
        {
            //구조체 값을 담은 변수를 덮어쓴다면 그 멤버 이름도 지움
            forget(current , name , dict[name]);
            dict[name] = value;
        }
        scope_changed(current);
    }
    bool remove_variable(string name)
    {
        //읽을 때와 같은 순서로 찾아서 삭제함 (호출한 다른 함수의 지역 변수는 삭제하지 않음)
        for (Scope? scope = current ; scope is not null ; scope = scope.Parent)
        {
            if (scope.Variables.Remove(name , out object? holder))
            {
                //구조체 값을 담은 변수라면 멤버 이름(이름.필드 등)도 함께 지움
                forget(scope , name , holder);
                scope_changed(scope);
                refresh_owner(scope);
                return true;
            }
        }
        //전역 함수/변수는 이 인터프리터에서만 보이지 않게 함
        if (removed_globals.Contains(name)) return false;
        lock (runtime.Global)
        {
            if (!runtime.Global.ContainsKey(name)) return false;
        }
        removed_globals.Add(name);
        bindings_version++;
        return true;
    }
    static bool is_true(object?[] values) => values.Length > 0 && Parser.IsTrue(values[0]);

    public List<CommandLine> scripts = new();
    public object? ExecuteLine(string cmd)
    {
        int index = scripts.Count;
        scripts.Add(new(cmd));
        return ExecuteLine(index);
    }
    /// <summary>
    /// 한 줄만 실행합니다. 실행 위치를 옮기는 키워드(if, while, goto 등)는 Run()으로 실행할 때만 사용할수 있습니다.
    /// </summary>
    public object? ExecuteLine(int line)
    {
        int current = CurrentExecuteLine;
        CurrentExecuteLine = line;
        try
        {
            materialize_inline();
            object? result = execute_line(line , false);
            escape(result is ReturnInfo ri ? ri.value : result);
            return result;
        }
        catch (GrammarErrorException e)
        {
            return e.Error;
        }
        finally
        {
            CurrentExecuteLine = current;
        }
    }
    bool running = false;
    public object? Run(int start = 0)
    {
        if (running)
            throw new JyunoException("이미 실행중입니다.");
        running = true;
        //인라인된 호출 도중에 C# 함수가 Run()을 호출했다면 ExecuteLine()처럼 함수 영역을 먼저 만듦
        materialize_inline();
        //인라인된 호출은 호출 프레임을 만들지 않으므로, 그 안에서 다시 실행한다면 프레임을 만들어 둠 (반복문의 상태가 바깥 호출과 섞이지 않도록)
        bool frameless = inline_depth > (frames.Count > 0 ? frames.Peek().InlineDepth : 0);
        if (frameless)
        {
            inline_depth--;
            frames.Push(new CallFrame(inline_args , inline_depth));
        }
        //스크립트가 바뀌었을수 있으므로 블록 정보를 새로 계산
        blocks = null;
        top_repeat_counts.Clear();
        top_for_states.Clear();
        current = top;
        try
        {
            for(CurrentExecuteLine = start ;CurrentExecuteLine<scripts.Count ; CurrentExecuteLine++)
            {
                var ret = execute_line(CurrentExecuteLine , true);
                if (ret is ReturnInfo ri)
                {
                    escape(ri.value);
                    return ri.value;
                }
                //문법 오류가 있다면 실행을 멈추고 오류를 반환
                if (ret is GrammarError)
                    return ret;
            }
            return null;
        }
        catch (GrammarErrorException e)
        {
            //함수 안에서 발생한 문법 오류
            return e.Error;
        }
        finally
        {
            //반환되거나 예외가 발생해도 다시 실행할수 있도록 (for 문 안에서 끝났다면 최상위 영역으로 되돌림)
            running = false;
            current = top;
            top_for_states.Clear();
            CurrentExecuteLine = -1;
            if (frameless)
            {
                frames.Pop();
                inline_depth++;
            }
        }
    }
    object? execute_line(int line , bool flow)
    {
        CompiledLine compiled = compiled_line(line);
        if (compiled.Error is string error)
            return new GrammarError(line , error);
        //키워드는 인터프리터 차원에서 관리
        if (compiled.Keyword is KeywordType keyword)
            return process_keyword(compiled , keyword , line , flow);
        return compiled.Expression();
    }

    //if/else/end 등의 짝 (Run 할때 계산)
    BlockMap? blocks = null;
    BlockMap get_blocks()
    {
        //ExecuteLine(string) 등으로 줄이 추가되었다면 다시 계산
        if (blocks is null || blocks.Count != scripts.Count)
        {
            blocks = new BlockMap(scripts);
            inline_shapes.Clear();
        }
        return blocks;
    }
    //repeat 줄 → 남은 반복 횟수 (함수 호출마다 따로 관리해야 재귀 호출에서도 섞이지 않음)
    Dictionary<int , long> top_repeat_counts = new();
    Dictionary<int , long> repeat_counts => frames.Count > 0 ? frames.Peek().RepeatCounts : top_repeat_counts;
    //for 줄 → 순회 상태 (repeat과 마찬가지로 함수 호출마다 따로 관리)
    Dictionary<int , ForState> top_for_states = new();
    Dictionary<int , ForState> for_states => frames.Count > 0 ? frames.Peek().ForStates : top_for_states;
    //for 문의 순회 상태: 순회할 원소, 지금 위치, for 문 바깥 영역
    sealed class ForState
    {
        public ForState(string name , object?[] items , Scope outer)
        {
            Name = name;
            Items = items;
            Outer = outer;
        }
        public readonly string Name;
        public readonly object?[] Items;
        public readonly Scope Outer;
        public int Index;
    }
    //반복 한번을 위한 새 영역을 만들고 원소를 변수에 넣음
    void enter_for(ForState state)
    {
        current = new Scope(state.Outer);
        declare_variable(state.Name , state.Items[state.Index]);
    }
    //for 문을 빠져나감 (for 문 바깥 영역으로 되돌림)
    void exit_for(int line)
    {
        if (for_states.Remove(line , out ForState? state))
            current = state.Outer;
    }
    //다음에 실행할 줄을 정함 (Run의 반복문에서 1을 더하므로 1을 뺌)
    void jump(int next_line) => CurrentExecuteLine = next_line - 1;
    KeywordType keyword_of(int line) => (KeywordType)scripts[line].tokens[0].value;

    object? process_keyword(CompiledLine compiled , KeywordType keyword , int line , bool flow)
    {
        Token[] tokens = compiled.Tokens;
        //반환
        if (keyword is KeywordType.Return)
        {
            var ret = compiled.Arguments();
            return new ReturnInfo(ret.Length is 0 ? null : ret[0]);
        }
        //지역 변수 선언 (실행 위치를 옮기지 않으므로 ExecuteLine()에서도 사용 가능)
        if (keyword is KeywordType.Local)
        {
            declare_local(compiled);
            return null;
        }
        //나머지 키워드는 실행 위치를 옮기므로 Run()으로 실행할 때만 사용 가능
        if (!flow)
            throw new JyunoException($"'{keyword.ToString().ToLowerInvariant()}' 키워드는 Run()으로 실행할 때만 사용할수 있습니다.");
        BlockMap map = get_blocks();
        if (map.Errors[line] is string error)
            return new GrammarError(line , error);
        switch (keyword)
        {
            //이동
            case KeywordType.Goto:
                var target = compiled.Arguments();
                if (target.Length is 0)
                {
                    throw new JyunoException("이동할 값을 넣지 않았습니다.");
                }
                if (!Values.TryGetInteger(target[0] , out long goto_line))
                    throw new JyunoException("실행 위치는 음이 아닌 정수여야 합니다.");
                if (goto_line < 0)
                    throw new JyunoException("실행 위치를 음수로 이동할수 없습니다.");
                if (goto_line > scripts.Count)
                    throw new JyunoException("실행 위치가 스크립트의 범위를 벗어났습니다.");
                //함수 안팎으로는 이동할수 없음
                if ((goto_line < scripts.Count ? map.Owner[goto_line] : -1) != map.Owner[line])
                    throw new JyunoException("goto로 함수 안팎을 넘나들수 없습니다.");
                //goto로 빠져나가는 for 문은 안쪽부터 정리 (마지막에 가장 바깥 for 문의 바깥 영역으로 되돌아감)
                foreach (int for_line in for_states.Keys.Where(o => !(o < goto_line && goto_line <= map.Pair[o])).OrderByDescending(o => o).ToList())
                    exit_for(for_line);
                jump((int)goto_line);
                break;
            //만약
            case KeywordType.If:
                //실행하지 않을꺼라면 else 또는 end 다음 줄로 건너뛰기
                if (!compiled.Condition())
                    jump(map.Pair[line] + 1);
                //실행할꺼면 어짜피 else 마주칠때까지 실행하면 됨
                break;
            case KeywordType.Else:
                //if가 참이어서 여기까지 왔으므로 end 다음 줄로 건너뛰기
                jump(map.Pair[line] + 1);
                break;
            case KeywordType.While:
                //실행하지 말아야 한다면 end 다음 줄로 건너뛰기
                if (!compiled.Condition())
                    jump(map.Pair[line] + 1);
                break;
            case KeywordType.Repeat:
                //repeat 줄은 반복문에 새로 들어올때만 실행되므로, 항상 횟수를 새로 정함
                var count = compiled.Arguments();
                //반복 횟수는 자연수여야만 함, 아니라면 실행하지 않음
                if (count.Length > 0 && Values.TryGetInteger(count[0] , out long n) && n > 0)
                {
                    repeat_counts[line] = n;
                }
                else
                {
                    repeat_counts.Remove(line);
                    jump(map.Pair[line] + 1);
                }
                break;
            case KeywordType.End:
                int opener = map.Pair[line];
                switch (keyword_of(opener))
                {
                    //while은 조건을 다시 검사
                    case KeywordType.While:
                        jump(opener);
                        break;
                    //repeat은 남은 횟수가 있다면 repeat 다음 줄부터 다시 실행
                    case KeywordType.Repeat:
                        if (repeat_counts.TryGetValue(opener , out long left) && left > 1)
                        {
                            repeat_counts[opener] = left - 1;
                            jump(opener + 1);
                        }
                        else
                            repeat_counts.Remove(opener);
                        break;
                    //for는 남은 원소가 있다면 새 영역에서 for 다음 줄부터 다시 실행
                    case KeywordType.For:
                        if (for_states.TryGetValue(opener , out ForState? state))
                        {
                            current = state.Outer;
                            if (++state.Index < state.Items.Length)
                            {
                                enter_for(state);
                                jump(opener + 1);
                            }
                            else
                                for_states.Remove(opener);
                        }
                        break;
                }
                break;
            case KeywordType.Break:
                //가장 안쪽 반복문의 end 다음 줄로 건너뛰기
                int loop_end = map.Pair[line];
                repeat_counts.Remove(map.Pair[loop_end]);
                exit_for(map.Pair[loop_end]);
                jump(loop_end + 1);
                break;
            //for (이름) (컬렉션): 컬렉션의 원소를 차례대로 (이름)에 넣고 for와 end 사이를 실행
            case KeywordType.For:
                if (tokens.Length < 3 || tokens[1].type is not TokenType.Name)
                    throw new JyunoException("for는 'for (이름) (배열/리스트/문자열)' 형식으로만 쓸수 있습니다.");
                //goto 등으로 순회 중인 for 줄로 다시 왔다면 이전 순회는 끝냄
                exit_for(line);
                var source = compiled.Arguments();
                if (source.Length is not 1)
                    throw new JyunoException("for 다음에는 변수 이름과 순회할 값 하나만 와야 합니다.");
                object?[] items = source[0] switch {
                    string text => text.Select(c => (object?)c.ToString()).ToArray(),
                    //순회 도중 컬렉션이 바뀌어도 영향을 받지 않도록 복사
                    System.Collections.IList list => list.Cast<object?>().ToArray(),
                    _ => throw new JyunoException($"'{source[0] ?? "null"}'은/는 순회할수 없습니다. (배열/리스트/문자열만 가능)")
                };
                if (items.Length is 0)
                {
                    jump(map.Pair[line] + 1);
                    break;
                }
                ForState for_state = new((string)tokens[1].value , items , current);
                for_states[line] = for_state;
                enter_for(for_state);
                break;
            //함수 정의: 이름에 함수를 저장하고 endfunc 다음 줄로 건너뛰기
            case KeywordType.Func:
                if (tokens.Length != 2 || tokens[1].type is not TokenType.Name)
                    throw new JyunoException("func 다음에는 함수 이름 하나만 와야 합니다.");
                substitute_variable((string)tokens[1].value , new JyunoFunction(this , line , map.Pair[line] , current));
                jump(map.Pair[line] + 1);
                break;
            //구조체 정의: 이름에 구조체를 저장하고 endstruct 다음 줄로 건너뛰기 (Struct.cs)
            case KeywordType.Struct:
                if (tokens.Length != 2 || tokens[1].type is not TokenType.Name)
                    throw new JyunoException("struct 다음에는 구조체 이름 하나만 와야 합니다.");
                substitute_variable((string)tokens[1].value , new JyunoStructType(this , (string)tokens[1].value , line , map.Pair[line] , current , map.HasInnerStruct[line]));
                jump(map.Pair[line] + 1);
                break;
            //파라미터: 몇번째 param인지에 따라 인자를 받음 (인자가 부족하면 null)
            case KeywordType.Param:
                if (tokens.Length != 2 || tokens[1].type is not TokenType.Name)
                    throw new JyunoException("param 다음에는 파라미터 이름 하나만 와야 합니다.");
                if (frames.Count is 0)
                    throw new JyunoException("param은 함수가 호출되었을 때만 사용할수 있습니다.");
                object?[] args = frames.Peek().Args;
                int index = map.ParamIndex[line];
                object? arg = index < args.Length ? args[index] : null;
                //다시 실행될수 있는 param 줄(반복문 안, goto가 있는 함수)은 인자의 원래 값을 남겨둬야 하므로 소유권을 가져가지 않음
                if (!map.ParamOnce[line])
                    escape(arg);
                declare_variable((string)tokens[1].value , arg);
                break;
            //함수/구조체의 끝 (본문은 이 줄에 도달하면 끝남)
            case KeywordType.EndFunc:
            case KeywordType.EndStruct:
                break;
        }
        return null;
    }

    //local (이름) 또는 local (이름) = (값): 바깥에 같은 이름의 변수가 있어도 지금 영역에 변수를 새로 만듦
    void declare_local(CompiledLine compiled)
    {
        Token[] tokens = compiled.Tokens;
        if (tokens.Length < 2 || tokens[1].type is not TokenType.Name)
            throw new JyunoException("local 다음에는 변수 이름이 와야 합니다.");
        //대입 연산이 금지되었는가?
        if (BlockSubstitute)
            throw new JyunoException("런타임에서 변수 대입 연산을 허용하지 않습니다.");
        string name = (string)tokens[1].value;
        object? value = null;
        if (tokens.Length > 2)
        {
            if (compiled.LocalValue is not Func<object?> local_value)
                throw new JyunoException("local은 'local (이름)' 또는 'local (이름) = (값)' 형식으로만 쓸수 있습니다.");
            value = local_value();
        }
        //이미 지금 영역에 있는 이름이라면 일반 대입과 같음 (상수/함수는 바꿀수 없음)
        if (current.Variables.TryGetValue(name , out object? existing))
        {
            if (existing is VariableInterface vi)
                assign(vi , name , value);
            else
                throw new JyunoException($"'{name}'은/는 변수가 아닙니다.");
            return;
        }
        declare_variable(name , value);
    }

    //함수 호출 정보 (인자, 반복 횟수)
    sealed class CallFrame
    {
        public CallFrame(object?[] args , int inline_depth)
        {
            Args = args;
            InlineDepth = inline_depth;
        }
        public readonly object?[] Args;
        //이 호출이 시작될 때 진행중이던 인라인된 호출의 수 (이보다 많다면 가장 안쪽의 호출은 프레임이 없는 인라인된 호출)
        public readonly int InlineDepth;
        public readonly Dictionary<int , long> RepeatCounts = new();
        public readonly Dictionary<int , ForState> ForStates = new();
    }
    Stack<CallFrame> frames = new();
    //무한 재귀로 프로그램이 죽지 않도록 제한
    public const int MaxCallDepth = 200;

    //스크립트에서 func로 만든 함수
    sealed class JyunoFunction : FunctionInterface
    {
        public JyunoFunction(Interpreter interpreter , int line , int end , Scope scope)
        {
            this.interpreter = interpreter;
            this.line = line;
            this.end = end;
            Scope = scope;
        }
        readonly Interpreter interpreter;
        readonly int line, end;
        //함수를 만든 영역 (함수 본문에서 읽을수 있는 바깥 변수, 구조체 함수라면 구조체 값의 영역)
        public readonly Scope Scope;
        //같은 함수를 다른 영역에 만듦 (구조체 값을 복사할때 사용)
        public JyunoFunction Rebind(Scope scope) => new(interpreter , line , end , scope) { shape = shape , shape_known = shape_known };
        //[실험] 인라이닝: 본문의 모양 (한번만 계산하고, 호출할 때마다 본문의 줄이 그대로인지만 확인)
        InlineShape? shape;
        bool shape_known;
        //스크립트 안에서의 호출
        public object? Call(object?[] args)
        {
            if (interpreter.Inlining)
            {
                //프로그램이 scripts의 줄을 바꿨다면 본문의 모양을 다시 계산
                if (!shape_known || (shape is not null && !shape.Matches(interpreter.scripts)))
                {
                    shape = interpreter.inline_shape(line , end);
                    shape_known = true;
                }
                if (shape is not null)
                    return interpreter.call_inline(this , shape , args);
            }
            return interpreter.call_function(line , end , new Scope(Scope) , args);
        }
        //C# 프로그램에서의 호출: 주고받는 값은 인터프리터 밖의 값
        public object? Execute(params object?[] args)
        {
            escape(args);
            object? result = Call(args);
            escape(result);
            return result;
        }
    }
    //스크립트에서 함수를 호출 (C# 함수라면 인자와 반환값은 인터프리터 밖으로 나갔다 들어온 값)
    object? invoke_function(FunctionInterface function , object?[] args)
    {
        switch (function)
        {
            case JyunoFunction jyuno:
                return jyuno.Call(args);
            case JyunoStructType type:
                return type.Call(args);
            default:
                escape(args);
                object? result = function.Execute(args);
                escape(result);
                return result;
        }
    }
    //함수/구조체 본문(line과 end 사이)을 body 영역에서 실행 (함수 안에서 만든 변수는 함수가 끝나면 사라짐)
    object? call_function(int line , int end , Scope body , object?[] args)
    {
        if (frames.Count + inline_depth >= MaxCallDepth)
            throw new JyunoException($"함수 호출이 너무 깊습니다. (최대 {MaxCallDepth}번)");
        int current_line = CurrentExecuteLine;
        Scope caller = current;
        InlineShape? outer_frame = inline_frame;
        frames.Push(new CallFrame(args , inline_depth));
        current = body;
        inline_frame = null;
        try
        {
            for (CurrentExecuteLine = line + 1 ; CurrentExecuteLine < end ; CurrentExecuteLine++)
            {
                var ret = execute_line(CurrentExecuteLine , true);
                if (ret is ReturnInfo ri)
                    return ri.value;
                if (ret is GrammarError error)
                    throw new GrammarErrorException(error);
            }
            return null;
        }
        finally
        {
            current = caller;
            inline_frame = outer_frame;
            frames.Pop();
            CurrentExecuteLine = current_line;
        }
    }
    //[실험] 인라이닝 ---
    //param 줄들과 return 한 줄로만 된 함수의 모양 (func 줄 → 모양, 인라인할수 없다면 null)
    sealed class InlineShape
    {
        public InlineShape((int Line, int Index, string Name)[] parameters , int ret , int first , CommandLine[] lines)
        {
            Params = parameters;
            Return = ret;
            First = first;
            Lines = lines;
        }
        public readonly (int Line, int Index, string Name)[] Params;
        public readonly int Return;
        //이 모양을 계산한 줄들 (func 줄부터 endfunc 줄까지)
        public readonly int First;
        public readonly CommandLine[] Lines;
        //scripts의 줄이 모양을 계산했을 때와 같은지 (scripts는 공개된 목록이므로 프로그램이 실행 사이에 줄을 바꾸거나 끼워넣을수 있음)
        public bool Matches(List<CommandLine> scripts)
        {
            CommandLine[] lines = Lines;
            if (scripts.Count < First + lines.Length)
                return false;
            for (int i = 0 ; i < lines.Length ; i++)
            {
                if (!ReferenceEquals(scripts[First + i] , lines[i]))
                    return false;
            }
            return true;
        }
        //영역을 만들지 않고 인자를 바로 읽는 return 식 (인라인할수 없는 식이라면 null)
        public Func<object?>? Direct;
        public bool DirectCompiled;
    }
    //return 식을 인라인용으로 컴파일하는 동안: 파라미터 이름 → 인자 번호
    Dictionary<string , int>? inline_params;
    bool inline_rejected;
    //인라인된 호출의 인자 (호출마다 바꾸고 되돌림)
    object?[] inline_args = Array.Empty<object?>();
    //지금 실행중인 곳이 영역을 만들지 않고 인라인된 호출의 return 식이라면 그 함수의 모양 (그 안에서 다른 함수를 호출하는 동안에는 null)
    InlineShape? inline_frame;
    int? inline_slot(string name) => inline_params is not null && inline_params.TryGetValue(name , out int index) ? index : null;
    //파라미터의 멤버 이름 (파라미터 이름 + "." + 멤버, 파라미터 이름에는 '.'이 없음)
    (int Slot, string Member)? inline_member(string name)
    {
        if (inline_params is null)
            return null;
        int dot = name.IndexOf('.');
        if (dot > 0 && dot < name.Length - 1 && inline_params.TryGetValue(name[..dot] , out int index))
            return (index , name[(dot + 1)..]);
        return null;
    }
    //파라미터 읽기: 인라인된 호출이 진행중이라면 인자를 바로 읽고, 함수 영역이 만들어진 뒤라면 원래대로(normal) 이름을 찾음
    Expr slot_read(int index , Expr normal)
    {
        object? slot()
        {
            object?[] args = inline_args;
            return index < args.Length ? args[index] : null;
        }
        Func<object?> normal_eval = normal.Eval;
        //변수를 읽을 때와 같이 구조체 값이라면 복사본
        Func<object?> eval = () => inline_frame is not null ? read(slot()) : normal_eval();
        if (normal.Borrow is not Func<object?> normal_borrow)
            return new Expr(eval) { MayEndEarly = normal.MayEndEarly };
        Func<bool>? normal_hosted = normal.Hosted;
        return new Expr(eval) {
            Borrow = () => inline_frame is null ? normal_borrow() : EscapeAnalysis ? slot() : read(slot()) ,
            //인자를 바로 읽는 동안에는 C# 코드가 실행되지 않음
            Hosted = normal_hosted is null ? null : () => inline_frame is null && normal_hosted()
        };
    }
    //인라인된 호출(영역을 만들지 않은 호출)이 진행중일때 C# 함수가 ExecuteLine()을 호출했다면, 그 줄이 함수 영역에서 실행되도록 영역을 만듦
    //(이 뒤로는 파라미터도 이 영역의 변수에서 읽음)
    void materialize_inline()
    {
        if (inline_frame is not InlineShape shape)
            return;
        object?[] args = inline_args;
        inline_frame = null;
        current = new Scope(current);
        foreach (var (_ , index , name) in shape.Params)
        {
            object? arg = index < args.Length ? args[index] : null;
            //구조체 인자는 이미 이 호출의 것이므로 그대로 변수에 넣음
            if (arg is StructInstance instance)
                instance.Fresh = true;
            declare_variable(name , arg);
        }
    }
    //return 식을 파라미터를 인자로 바로 읽도록 컴파일 (대입/레이블/삭제/상수 선언이 있다면 함수 영역에 이름이 생기므로 인라인하지 않음)
    Func<object?>? compile_direct(InlineShape shape)
    {
        Token[] tokens = scripts[shape.Return].tokens;
        foreach (Token token in tokens)
        {
            if (token.type is TokenType.Prefix && token.value is '=' or ':')
                return null;
        }
        //파라미터 이름이 겹치거나 '.'이 있다면 구조체 파라미터의 멤버 이름과 서로 덮어쓸수 있으므로 인라인하지 않음
        Dictionary<string , int> parameters = new();
        foreach (var (_ , index , name) in shape.Params)
        {
            if (name.Contains('.') || !parameters.TryAdd(name , index))
                return null;
        }
        inline_params = parameters;
        inline_rejected = false;
        try
        {
            Expr[] arguments = compile_arguments(tokens , 1 , out _);
            if (inline_rejected)
                return null;
            if (arguments.Length is 0)
                return () => null;
            Func<object?> first = arguments[0].Eval;
            if (arguments.Length is 1)
                return first;
            Func<object?>[] rest = Array.ConvertAll(arguments[1..] , o => o.Eval);
            return () => {
                object? value = first();
                foreach (Func<object?> eval in rest)
                    eval();
                return value;
            };
        }
        finally
        {
            inline_params = null;
        }
    }
    readonly Dictionary<int , InlineShape?> inline_shapes = new();
    //인라인된 호출의 깊이 (MaxCallDepth 검사에 포함)
    int inline_depth = 0;

    InlineShape? inline_shape(int line , int end)
    {
        if (inline_shapes.TryGetValue(line , out InlineShape? shape) && (shape is null || shape.Matches(scripts)))
            return shape;
        BlockMap map = get_blocks();
        shape = null;
        List<(int, int, string)> parameters = new();
        int ret = -1;
        bool ok = end < map.Count && map.Pair[line] == end;
        for (int l = line + 1 ; ok && l < end ; l++)
        {
            Token[] tokens = scripts[l].tokens;
            if (tokens.Length is 0)
                continue;
            //return 다음에는 실행되지 않는 줄이 있어도 되지만, 단순하게 하기 위해 인라인하지 않음
            if (ret >= 0 || map.Errors[l] is not null || compiled_line(l).Error is not null || tokens[0].type is not TokenType.Keyword)
            {
                ok = false;
                break;
            }
            switch ((KeywordType)tokens[0].value)
            {
                case KeywordType.Param when tokens.Length is 2 && tokens[1].type is TokenType.Name:
                    parameters.Add((l , map.ParamIndex[l] , (string)tokens[1].value));
                    break;
                case KeywordType.Return:
                    ret = l;
                    break;
                default:
                    ok = false;
                    break;
            }
        }
        if (ok && ret >= 0)
            shape = new InlineShape(parameters.ToArray() , ret , line , scripts.GetRange(line , end - line + 1).ToArray());
        inline_shapes[line] = shape;
        return shape;
    }

    //함수 본문을 줄 단위로 실행하지 않고 바로 계산 (영역, 인자, 실행 위치, 오류는 call_function과 같음)
    object? call_inline(JyunoFunction function , InlineShape shape , object?[] args)
    {
        if (frames.Count + inline_depth >= MaxCallDepth)
            throw new JyunoException($"함수 호출이 너무 깊습니다. (최대 {MaxCallDepth}번)");
        int current_line = CurrentExecuteLine;
        Scope caller = current;
        if (!shape.DirectCompiled)
        {
            shape.Direct = compile_direct(shape);
            shape.DirectCompiled = true;
        }
        InlineShape? outer_frame = inline_frame;
        if (shape.Direct is Func<object?> direct)
        {
            //파라미터가 받는 구조체 인자는 param처럼 소유권을 가져오거나 복사함 (인라인된 본문의 구조체 함수가 필드를 바꿀수 있으므로)
            int count = Math.Min(args.Length , shape.Params.Length);
            for (int i = 0 ; i < count ; i++)
            {
                if (args[i] is not StructInstance)
                    continue;
                args = (object?[])args.Clone();
                for (int j = i ; j < count ; j++)
                {
                    if (args[j] is StructInstance instance)
                        args[j] = take(instance);
                }
                break;
            }
            object?[] outer_args = inline_args;
            //파라미터가 아닌 이름은 함수를 만든 영역에서 찾음 (함수 영역에는 파라미터만 있으므로 결과가 같음)
            current = function.Scope;
            inline_args = args;
            inline_frame = shape;
            inline_depth++;
            try
            {
                CurrentExecuteLine = shape.Return;
                return direct();
            }
            finally
            {
                current = caller;
                inline_args = outer_args;
                inline_frame = outer_frame;
                inline_depth--;
                CurrentExecuteLine = current_line;
            }
        }
        object?[] caller_args = inline_args;
        current = new Scope(function.Scope);
        inline_args = args;
        inline_frame = null;
        inline_depth++;
        try
        {
            foreach (var (line , index , name) in shape.Params)
            {
                CurrentExecuteLine = line;
                declare_variable(name , index < args.Length ? args[index] : null);
            }
            CurrentExecuteLine = shape.Return;
            object?[] ret = compiled_line(shape.Return).Arguments();
            return ret.Length is 0 ? null : ret[0];
        }
        finally
        {
            current = caller;
            inline_args = caller_args;
            inline_frame = outer_frame;
            inline_depth--;
            CurrentExecuteLine = current_line;
        }
    }

    //함수 안에서 발생한 문법 오류를 Run()/ExecuteLine()까지 전달
    sealed class GrammarErrorException : JyunoException
    {
        public GrammarErrorException(GrammarError error) : base(error.ToString())
        {
            Error = error;
        }
        public readonly GrammarError Error;
    }

    //스크립트를 한번 훑어서 if/while/repeat/for/func 블록의 짝을 미리 계산 (중첩된 블록과 break를 정확히 처리하기 위함)
    internal sealed class BlockMap
    {
        //if → else(없다면 end), else → end, while/repeat/for → end, end → 여는 키워드, break → 반복문의 end, func ↔ endfunc, struct ↔ endstruct
        public readonly int[] Pair;
        //블록 구조가 잘못된 줄의 오류 메시지
        public readonly string?[] Errors;
        //각 줄이 속한 가장 안쪽 func/struct 줄 (함수 밖이라면 -1)
        public readonly int[] Owner;
        //param 줄이 함수의 몇번째 파라미터인지
        public readonly int[] ParamIndex;
        //param 줄이 함수 호출 한번에 최대 한번만 실행되는지 (반복문 안이 아니고, 함수에 goto가 없음)
        public readonly bool[] ParamOnce;
        //struct 줄: 그 구조체 안에(구조체 함수 안 포함) 다른 struct가 있는지
        public readonly bool[] HasInnerStruct;
        public int Count => Pair.Length;

        public BlockMap(List<CommandLine> scripts) : this(scripts.Select(o => o.tokens).ToArray()) { }

        public BlockMap(IReadOnlyList<Token[]> lines)
        {
            Pair = new int[lines.Count];
            Errors = new string?[lines.Count];
            Owner = new int[lines.Count];
            ParamIndex = new int[lines.Count];
            ParamOnce = new bool[lines.Count];
            HasInnerStruct = new bool[lines.Count];
            HashSet<int> has_goto = new();
            Array.Fill(Pair , -1);
            Stack<int> opened = new();
            List<(int line, int loop)> breaks = new();
            Dictionary<int , int> param_counts = new();
            KeywordType? keyword_at(int line)
            {
                Token[] tokens = lines[line];
                return tokens.Length > 0 && tokens[0].type is TokenType.Keyword ? (KeywordType)tokens[0].value : null;
            }
            //func와 struct는 본문을 따로 실행하는 블록 (goto/break로 넘나들수 없고, param의 순서를 따로 셈)
            bool is_body(int line) => keyword_at(line) is KeywordType.Func or KeywordType.Struct;
            //닫히지 않은 블록 표시
            void unclosed(int line)
            {
                if (keyword_at(line) is KeywordType.Func)
                {
                    Errors[line] = "func 키워드는 endfunc 키워드로 종료 표시가 있어야 합니다.";
                    return;
                }
                if (keyword_at(line) is KeywordType.Struct)
                {
                    Errors[line] = "struct 키워드는 endstruct 키워드로 종료 표시가 있어야 합니다.";
                    return;
                }
                Errors[line] = "모든 조건문(if, while 등) 키워드는 end 키워드로 종료 표시가 있어야 합니다.";
                //else가 있는 if라면 else도 오류
                if (Pair[line] >= 0)
                    Errors[Pair[line]] = Errors[line];
            }

            for (int line = 0 ; line < lines.Count ; line++)
            {
                Owner[line] = opened.FirstOrDefault(is_body , -1);
                KeywordType? keyword = keyword_at(line);
                if (keyword is null)
                    continue;
                if (keyword is KeywordType.Func or KeywordType.Struct || Grammar.Conditionals.Contains(keyword.Value))
                {
                    if (keyword is KeywordType.Struct)
                    {
                        foreach (int o in opened)
                        {
                            if (keyword_at(o) is KeywordType.Struct)
                                HasInnerStruct[o] = true;
                        }
                    }
                    opened.Push(line);
                    continue;
                }
                switch (keyword)
                {
                    case KeywordType.Else:
                        //가장 안쪽 블록이 아직 else가 없는 if여야 함
                        if (opened.TryPeek(out int owner) && keyword_at(owner) is KeywordType.If && Pair[owner] < 0)
                            Pair[owner] = line;
                        else
                            Errors[line] = "else는 if와 end 사이에 한번만 쓸수 있습니다.";
                        break;
                    case KeywordType.End:
                        //func/struct는 end가 아닌 endfunc/endstruct로 닫아야 함
                        if (!opened.TryPeek(out int opener) || is_body(opener))
                        {
                            Errors[line] = "end와 짝이 되는 if, while, repeat, for 키워드가 없습니다.";
                            break;
                        }
                        opened.Pop();
                        //else가 있는 if라면 else가 end를 가리킴
                        if (Pair[opener] >= 0)
                            Pair[Pair[opener]] = line;
                        else
                            Pair[opener] = line;
                        Pair[line] = opener;
                        break;
                    case KeywordType.EndFunc:
                    case KeywordType.EndStruct:
                        bool is_func = keyword is KeywordType.EndFunc;
                        int func = Owner[line];
                        if (func < 0 || (keyword_at(func) is KeywordType.Func) != is_func)
                        {
                            Errors[line] = is_func ? "endfunc와 짝이 되는 func 키워드가 없습니다." : "endstruct와 짝이 되는 struct 키워드가 없습니다.";
                            break;
                        }
                        //함수 안에서 닫히지 않은 블록
                        while (opened.Peek() != func)
                            unclosed(opened.Pop());
                        opened.Pop();
                        Pair[func] = line;
                        Pair[line] = func;
                        break;
                    case KeywordType.Break:
                        //가장 안쪽 반복문 (함수 밖의 반복문은 제외, 반복문의 end는 아직 모르므로 나중에 연결)
                        int loop = -1;
                        foreach (int o in opened)
                        {
                            KeywordType? kind = keyword_at(o);
                            if (kind is KeywordType.Func or KeywordType.Struct)
                                break;
                            if (kind is not KeywordType.If)
                            {
                                loop = o;
                                break;
                            }
                        }
                        if (loop < 0)
                            Errors[line] = "break는 반복문(while, repeat, for) 안에서만 쓸수 있습니다.";
                        else
                            breaks.Add((line , loop));
                        break;
                    case KeywordType.Param:
                        //함수의 몇번째 param인지 기록
                        if (Owner[line] < 0)
                        {
                            Errors[line] = "param은 func와 endfunc(또는 struct와 endstruct) 사이에서만 쓸수 있습니다.";
                            break;
                        }
                        param_counts.TryGetValue(Owner[line] , out int index);
                        ParamIndex[line] = index;
                        param_counts[Owner[line]] = index + 1;
                        ParamOnce[line] = true;
                        foreach (int o in opened)
                        {
                            KeywordType? kind = keyword_at(o);
                            if (kind is KeywordType.Func or KeywordType.Struct)
                                break;
                            if (kind is not KeywordType.If)
                                ParamOnce[line] = false;
                        }
                        break;
                    case KeywordType.Goto:
                        has_goto.Add(Owner[line]);
                        break;
                }
            }
            for (int line = 0 ; line < lines.Count ; line++)
            {
                if (ParamOnce[line] && has_goto.Contains(Owner[line]))
                    ParamOnce[line] = false;
            }
            //end/endfunc로 닫히지 않은 블록
            foreach (int line in opened)
                unclosed(line);
            foreach (var (line , loop) in breaks)
            {
                if (Errors[loop] is null)
                    Pair[line] = Pair[loop];
                else
                    Errors[line] = Errors[loop];
            }
        }
    }

    public void Dispose()
    {
        runtime.RemoveInterpreter(this);
    }
}

public class CommandLine
{
    public CommandLine(string str)
    {
        script = str;
    }
    public string script;
    public Token[] tokens {
        get => tok ?? Parse();
    }

    private Token[] Parse()
    {
        return tok = Parser.Tokenizer(script).ToArray();
    }
    private Token[]? tok = null;
    //처음 실행할때 컴파일한 결과
    internal Interpreter.CompiledLine? compiled = null;
}
