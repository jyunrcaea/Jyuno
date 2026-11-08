using Jyuno.Compiler;
using Jyuno.Language;
using System.Runtime.ExceptionServices;

namespace Jyuno;

public class Interpreter : IDisposable
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
    }
    //지금 실행중인 영역 (처음에는 스크립트 최상위 영역, for 문 안에서는 반복 한번의 영역)
    Scope current;
    //스크립트 최상위 영역
    readonly Scope top;
    //이 인터프리터에서만 삭제한 전역 함수/변수 (전역 목록은 다른 인터프리터와 공유하므로 직접 지우지 않음)
    HashSet<string> removed_globals = new();
    bool search_variable(string name,out dynamic? value)
    {
        for (Scope? scope = current ; scope is not null ; scope = scope.Parent)
        {
            if (scope.Variables.TryGetValue(name , out value))
                return true;
        }
        return search_global(name , out value);
    }
    bool search_global(string name , out dynamic? value)
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
    void substitute_variable(string name,dynamic? value)
    {
        //새로 만드는 변수는 항상 지금 영역에 둠 (전역 목록은 다른 인터프리터와 공유함)
        VariableDictionary dict = current.Variables;
        if (!dict.TryAdd(name , value))
            dict[name] = value;
    }
    bool remove_variable(string name)
    {
        //읽을 때와 같은 순서로 찾아서 삭제함 (호출한 다른 함수의 지역 변수는 삭제하지 않음)
        for (Scope? scope = current ; scope is not null ; scope = scope.Parent)
        {
            if (scope.Variables.Remove(name)) return true;
        }
        //전역 함수/변수는 이 인터프리터에서만 보이지 않게 함
        if (removed_globals.Contains(name)) return false;
        lock (runtime.Global)
        {
            if (!runtime.Global.ContainsKey(name)) return false;
        }
        return removed_globals.Add(name);
    }

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
            return execute_line(line , false);
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
                    return ri.value;
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
        }
    }
    object? execute_line(int line , bool flow)
    {
        Token[] tokens = scripts[line].tokens;
        if (tokens.Length > 0 && tokens.Last().type is TokenType.Error)
        {
            return new GrammarError(line ,(string)tokens.Last().value);
        }
        //키워드는 인터프리터 차원에서 관리
        if (tokens.Length is 0)
            return null;
        else if (tokens[0].type is TokenType.Keyword)
            return process_keyword(tokens , line , flow);
       return execute(tokens , 0,out _);
    }

    object? execute(Token[] tokens , int start,out int end)
    {
        end = tokens.Length; //기본값
        if (start >= tokens.Length)
            return null;
        Token token = tokens[start];
        switch(token.type)
        {
            //상수
            case TokenType.Constant:
                //근데 뒤에 대입 연산이 있는가?
                if (checkvalue(tokens,start+1,TokenType.Prefix,'=') && tokens.Length > start+2)
                {
                    //이름도?
                    Token name = tokens[start+2];
                    if (name.type == TokenType.Name)
                    {
                        //그러면 상수 만들기!
                        substitute_variable((string)name.value, new JyunoConstantVariable(token.value));
                        return null;
                    }
                }
                end = start + 1;
                return token.value;
            //이름
            case TokenType.Name:
                //대입 연산인가?
                if (checkvalue(tokens,start+1,TokenType.Prefix,'=')) {
                    //근데 뒤에 인자가 더 없거나, ')' 이라면?
                    if (start+2 >= tokens.Length || checkvalue(tokens,start+2,TokenType.Prefix,')'))
                    {
                        end = start + 2;
                        //삭제 연산
                        if (EnableRemoveVariable)
                            return remove_variable((string)token.value);
                        else
                            throw new JyunoException("런타임에서 변수/함수 삭제 연산을 허용하지 않습니다.");
                    }
                    //대입 연산이 금지되었는가?
                    if (BlockSubstitute)
                        throw new JyunoException("런타임에서 변수 대입 연산을 허용하지 않습니다.");
                    var ret = execute(tokens , start + 2,out end); //별다른 제한이 없음.
                    //변수가 존재하는가? (함수 안이라면 지역 변수, 함수를 만든 곳의 변수, 프로그램이 제공하는 변수 순서)
                    //없다면 지금 영역(함수 안이라면 지역 변수)에 새로 만듦
                    if (search_variable((string)token.value , out dynamic? v))
                    {
                        if (v is VariableInterface vi)
                            vi.Set(ret);
                        else
                            throw new JyunoException($"'{token.value}'은/는 변수가 아닙니다.");
                    }
                    else
                    {
                        substitute_variable((string)token.value , new JyunoVariable(ret));
                    }
                    return null;
                }
                //레이블 연산인가?
                if (checkvalue(tokens,start+1,TokenType.Prefix,':'))
                {
                    substitute_variable((string)token.value , new JyunoVariable(CurrentExecuteLine));
                    return null;
                }
                //대입 연산이 아니면
                if (!search_variable((string)token.value ,out dynamic? f))
                {
                    throw new JyunoException($"'{token.value}'은/는 존재하지 않는 함수/변수/명령어 입니다.");
                }
                //함수 or 변수
                if (f is FunctionInterface fi)
                {
                    //이제 여기서 바뀜.
                    return fi.Execute(token2value(tokens , start + 1,out end).ToArray());
                }
                else if (f is VariableInterface vi)
                {
                    end = start + 1;
                    object? value = vi.Get();
                    //배열/리스트 뒤에 인자가 있다면 인덱스 읽기 (l 0) 또는 쓰기 (l 0 값)
                    if (value is System.Collections.IList list && start + 1 < tokens.Length && !checkvalue(tokens , start + 1 , TokenType.Prefix , ')'))
                    {
                        var index = token2value(tokens , start + 1 , out end);
                        switch (index.Count)
                        {
                            case 0:
                                return list;
                            case 1:
                                return Collections.Get(list , index.First());
                            case 2:
                                //대입 연산이 금지되었는가?
                                if (BlockSubstitute)
                                    throw new JyunoException("런타임에서 변수 대입 연산을 허용하지 않습니다.");
                                Collections.Set(list , index.First() , index.Last());
                                return null;
                            default:
                                throw new JyunoException("배열/리스트는 '(이름) (인덱스)' 또는 '(이름) (인덱스) (값)' 형식으로만 쓸수 있습니다.");
                        }
                    }
                    return value;
                }
                else
                    throw new JyunoException($"'{token.value}'는 함수 또는 변수가 아닙니다.");
            //연산자
            case TokenType.Prefix:
                if (token.value is '(')
                {
                    var value = execute(tokens , start + 1,out end);
                    //괄호는 ')'로 닫혀야 하며, 그 다음 위치를 알려줌
                    if (!checkvalue(tokens , end , TokenType.Prefix , ')'))
                        throw new JyunoException("괄호 '(' 다음에는 ')'로 끝나야만 합니다.");
                    end++;
                    return value;
                }
                if (token.value is ')')
                {
                    end = start;
                    return null;
                }
                break;
            //그 외
            default:
                throw new JyunoException("처리할수 없습니다.");
        }
        return null;
    }
    bool checkvalue(in Token[] arr,in int index,in TokenType type,in object? value)
    {
        if (index >= arr.Length)
            return false;
        Token t = arr[index];
        return t.type.Equals(type) && t.value.Equals(value);
    }
    LinkedList<object?> token2value(Token[] arr,int start,out int end)
    {
        LinkedList<object?> ret = new();
        for (int i = start ; i < arr.Length ; i++)
        {
            TokenType type = arr[i].type;
            //상수
            if (type == TokenType.Constant)
            {
                ret.AddLast(arr[i].value);
                continue;
            }
            //이름
            if (type == TokenType.Name)
            {
                if (search_variable((string)arr[i].value , out dynamic? v))
                {
                    if (v is VariableInterface vi)
                    {
                        ret.AddLast(vi.Get());
                        continue;
                    }
                } else
                {
                    throw new JyunoException($"'{arr[i].value}'은/는 알수없는 변수/함수 입니다.");
                }
                throw new JyunoException($"'{arr[i].value}'에서 값을 가져올수 없습니다.");
            }
            //키워드
            if (type is TokenType.Prefix)
            {
                if (arr[i].value is '(')
                {
                    ret.AddLast(execute(arr,i+1, out int e));
                    //괄호는 ')'로 닫혀야 함
                    if (!checkvalue(arr , e , TokenType.Prefix , ')'))
                        throw new JyunoException("괄호 '(' 다음에는 ')'로 끝나야만 합니다.");
                    i = e;
                    continue;
                }
                if (arr[i].value is ')')
                {
                    end = i;
                    return ret;
                }
                continue;
            }
        }
        //모두 순회했다면
        end = arr.Length;
        return ret;
    }

    //if/else/end 등의 짝 (Run 할때 계산)
    BlockMap? blocks = null;
    BlockMap get_blocks()
    {
        //ExecuteLine(string) 등으로 줄이 추가되었다면 다시 계산
        if (blocks is null || blocks.Count != scripts.Count)
            blocks = new BlockMap(scripts);
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
        current.Variables[state.Name] = new JyunoVariable(state.Items[state.Index]);
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

    object? process_keyword(in Token[] tokens,in int line,bool flow)
    {
        KeywordType keyword = (KeywordType)tokens[0].value;
        //반환
        if (keyword is KeywordType.Return)
        {
            var ret = token2value(tokens , 1 , out _);
            return new ReturnInfo(ret.Count is 0 ? null : ret.First());
        }
        //지역 변수 선언 (실행 위치를 옮기지 않으므로 ExecuteLine()에서도 사용 가능)
        if (keyword is KeywordType.Local)
        {
            declare_local(tokens);
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
                var target = token2value(tokens , 1 , out _);
                if (target.Count is 0)
                {
                    throw new JyunoException("이동할 값을 넣지 않았습니다.");
                }
                if (!Values.TryGetInteger(target.First() , out long goto_line))
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
                if (!Parser.IsTrue(token2value(tokens , 1 , out _)))
                    jump(map.Pair[line] + 1);
                //실행할꺼면 어짜피 else 마주칠때까지 실행하면 됨
                break;
            case KeywordType.Else:
                //if가 참이어서 여기까지 왔으므로 end 다음 줄로 건너뛰기
                jump(map.Pair[line] + 1);
                break;
            case KeywordType.While:
                //실행하지 말아야 한다면 end 다음 줄로 건너뛰기
                if (!Parser.IsTrue(token2value(tokens , 1 , out _)))
                    jump(map.Pair[line] + 1);
                break;
            case KeywordType.Repeat:
                //repeat 줄은 반복문에 새로 들어올때만 실행되므로, 항상 횟수를 새로 정함
                var count = token2value(tokens , 1 , out _);
                //반복 횟수는 자연수여야만 함, 아니라면 실행하지 않음
                if (count.Count > 0 && Values.TryGetInteger(count.First() , out long n) && n > 0)
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
                var source = token2value(tokens , 2 , out _);
                if (source.Count is not 1)
                    throw new JyunoException("for 다음에는 변수 이름과 순회할 값 하나만 와야 합니다.");
                object?[] items = source.First() switch {
                    string text => text.Select(c => (object?)c.ToString()).ToArray(),
                    //순회 도중 컬렉션이 바뀌어도 영향을 받지 않도록 복사
                    System.Collections.IList list => list.Cast<object?>().ToArray(),
                    _ => throw new JyunoException($"'{source.First() ?? "null"}'은/는 순회할수 없습니다. (배열/리스트/문자열만 가능)")
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
            //파라미터: 몇번째 param인지에 따라 인자를 받음 (인자가 부족하면 null)
            case KeywordType.Param:
                if (tokens.Length != 2 || tokens[1].type is not TokenType.Name)
                    throw new JyunoException("param 다음에는 파라미터 이름 하나만 와야 합니다.");
                if (frames.Count is 0)
                    throw new JyunoException("param은 함수가 호출되었을 때만 사용할수 있습니다.");
                object?[] args = frames.Peek().Args;
                int index = map.ParamIndex[line];
                substitute_variable((string)tokens[1].value , new JyunoVariable(index < args.Length ? args[index] : null));
                break;
            //함수의 끝 (함수 본문은 이 줄에 도달하면 끝남)
            case KeywordType.EndFunc:
                break;
        }
        return null;
    }

    //local (이름) 또는 local (이름) = (값): 바깥에 같은 이름의 변수가 있어도 지금 영역에 변수를 새로 만듦
    void declare_local(Token[] tokens)
    {
        if (tokens.Length < 2 || tokens[1].type is not TokenType.Name)
            throw new JyunoException("local 다음에는 변수 이름이 와야 합니다.");
        //대입 연산이 금지되었는가?
        if (BlockSubstitute)
            throw new JyunoException("런타임에서 변수 대입 연산을 허용하지 않습니다.");
        string name = (string)tokens[1].value;
        object? value = null;
        if (tokens.Length > 2)
        {
            if (!checkvalue(tokens , 2 , TokenType.Prefix , '=') || tokens.Length is 3)
                throw new JyunoException("local은 'local (이름)' 또는 'local (이름) = (값)' 형식으로만 쓸수 있습니다.");
            value = execute(tokens , 3 , out _);
        }
        //이미 지금 영역에 있는 이름이라면 일반 대입과 같음 (상수/함수는 바꿀수 없음)
        if (current.Variables.TryGetValue(name , out object? existing))
        {
            if (existing is VariableInterface vi)
                vi.Set(value);
            else
                throw new JyunoException($"'{name}'은/는 변수가 아닙니다.");
            return;
        }
        substitute_variable(name , new JyunoVariable(value));
    }

    //함수 호출 정보 (인자, 반복 횟수)
    sealed class CallFrame
    {
        public CallFrame(object?[] args)
        {
            Args = args;
        }
        public readonly object?[] Args;
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
            this.scope = scope;
        }
        readonly Interpreter interpreter;
        readonly int line, end;
        //함수를 만든 영역 (함수 본문에서 읽을수 있는 바깥 변수)
        readonly Scope scope;
        public object? Execute(params object?[] args) => interpreter.call_function(line , end , scope , args);
    }
    object? call_function(int line , int end , Scope scope , object?[] args)
    {
        if (frames.Count >= MaxCallDepth)
            throw new JyunoException($"함수 호출이 너무 깊습니다. (최대 {MaxCallDepth}번)");
        int current_line = CurrentExecuteLine;
        Scope caller = current;
        frames.Push(new CallFrame(args));
        //함수 안에서 만든 변수는 함수가 끝나면 사라짐
        current = new Scope(scope);
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
            frames.Pop();
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
    sealed class BlockMap
    {
        //if → else(없다면 end), else → end, while/repeat/for → end, end → 여는 키워드, break → 반복문의 end, func ↔ endfunc
        public readonly int[] Pair;
        //블록 구조가 잘못된 줄의 오류 메시지
        public readonly string?[] Errors;
        //각 줄이 속한 가장 안쪽 func 줄 (함수 밖이라면 -1)
        public readonly int[] Owner;
        //param 줄이 함수의 몇번째 파라미터인지
        public readonly int[] ParamIndex;
        public int Count => Pair.Length;

        public BlockMap(List<CommandLine> scripts)
        {
            Pair = new int[scripts.Count];
            Errors = new string?[scripts.Count];
            Owner = new int[scripts.Count];
            ParamIndex = new int[scripts.Count];
            Array.Fill(Pair , -1);
            Stack<int> opened = new();
            List<(int line, int loop)> breaks = new();
            Dictionary<int , int> param_counts = new();
            KeywordType? keyword_at(int line)
            {
                Token[] tokens = scripts[line].tokens;
                return tokens.Length > 0 && tokens[0].type is TokenType.Keyword ? (KeywordType)tokens[0].value : null;
            }
            //닫히지 않은 블록 표시
            void unclosed(int line)
            {
                if (keyword_at(line) is KeywordType.Func)
                {
                    Errors[line] = "func 키워드는 endfunc 키워드로 종료 표시가 있어야 합니다.";
                    return;
                }
                Errors[line] = "모든 조건문(if, while 등) 키워드는 end 키워드로 종료 표시가 있어야 합니다.";
                //else가 있는 if라면 else도 오류
                if (Pair[line] >= 0)
                    Errors[Pair[line]] = Errors[line];
            }

            for (int line = 0 ; line < scripts.Count ; line++)
            {
                Owner[line] = opened.FirstOrDefault(o => keyword_at(o) is KeywordType.Func , -1);
                KeywordType? keyword = keyword_at(line);
                if (keyword is null)
                    continue;
                if (keyword is KeywordType.Func || Grammar.Conditionals.Contains(keyword.Value))
                {
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
                        //func는 end가 아닌 endfunc로 닫아야 함
                        if (!opened.TryPeek(out int opener) || keyword_at(opener) is KeywordType.Func)
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
                        int func = Owner[line];
                        if (func < 0)
                        {
                            Errors[line] = "endfunc와 짝이 되는 func 키워드가 없습니다.";
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
                            if (kind is KeywordType.Func)
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
                            Errors[line] = "param은 func와 endfunc 사이에서만 쓸수 있습니다.";
                            break;
                        }
                        param_counts.TryGetValue(Owner[line] , out int index);
                        ParamIndex[line] = index;
                        param_counts[Owner[line]] = index + 1;
                        break;
                }
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
}
