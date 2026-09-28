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
        Locals.Push(new()); //다른 인터프리터와 독립되어야 함.
    }
    public bool EnableRemoveVariable { get; set; } = true;
    public bool BlockSubstitute { get; set; } = false;
    public Runtime runtime { get; init; }
    public int CurrentExecuteLine { get; private set; } = -1;
    Stack<VariableDictionary> Locals = new();
    //이 인터프리터에서만 삭제한 전역 함수/변수 (전역 목록은 다른 인터프리터와 공유하므로 직접 지우지 않음)
    HashSet<string> removed_globals = new();
    bool search_variable(string name,out dynamic? value)
    {
        foreach(var local in Locals)
        {
            if (local.TryGetValue(name , out value))
                return true;
        }
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
        //대입은 항상 지역 변수에 함 (전역 목록은 다른 인터프리터와 공유함)
        VariableDictionary dict = Locals.Peek();
        if (!dict.TryAdd(name , value))
            dict[name] = value;
    }
    bool remove_variable(string name)
    {
        foreach(var stack in Locals)
        {
            if (stack.Remove(name)) return true;
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
        repeat_counts.Clear();
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
        finally
        {
            //반환되거나 예외가 발생해도 다시 실행할수 있도록
            running = false;
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
                    //변수가 존재하는가?
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
                    return vi.Get();
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
    //repeat 줄 → 남은 반복 횟수
    Dictionary<int , long> repeat_counts = new();
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
        if (keyword is KeywordType.Func)
            throw new JyunoException("func 키워드는 아직 지원하지 않습니다.");
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
                }
                break;
            case KeywordType.Break:
                //가장 안쪽 반복문의 end 다음 줄로 건너뛰기
                int loop_end = map.Pair[line];
                repeat_counts.Remove(map.Pair[loop_end]);
                jump(loop_end + 1);
                break;
        }
        return null;
    }

    //스크립트를 한번 훑어서 if/while/repeat 블록의 짝을 미리 계산 (중첩된 블록과 break를 정확히 처리하기 위함)
    sealed class BlockMap
    {
        //if → else(없다면 end), else → end, while/repeat → end, end → 여는 키워드, break → 반복문의 end
        public readonly int[] Pair;
        //블록 구조가 잘못된 줄의 오류 메시지
        public readonly string?[] Errors;
        public int Count => Pair.Length;

        public BlockMap(List<CommandLine> scripts)
        {
            Pair = new int[scripts.Count];
            Errors = new string?[scripts.Count];
            Array.Fill(Pair , -1);
            Stack<int> opened = new();
            List<(int line, int loop)> breaks = new();
            KeywordType? keyword_at(int line)
            {
                Token[] tokens = scripts[line].tokens;
                return tokens.Length > 0 && tokens[0].type is TokenType.Keyword ? (KeywordType)tokens[0].value : null;
            }

            for (int line = 0 ; line < scripts.Count ; line++)
            {
                KeywordType? keyword = keyword_at(line);
                if (keyword is null)
                    continue;
                if (Grammar.Conditionals.Contains(keyword.Value))
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
                        if (!opened.TryPop(out int opener))
                        {
                            Errors[line] = "end와 짝이 되는 if, while, repeat 키워드가 없습니다.";
                            break;
                        }
                        //else가 있는 if라면 else가 end를 가리킴
                        if (Pair[opener] >= 0)
                            Pair[Pair[opener]] = line;
                        else
                            Pair[opener] = line;
                        Pair[line] = opener;
                        break;
                    case KeywordType.Break:
                        //가장 안쪽 반복문 (반복문의 end는 아직 모르므로 나중에 연결)
                        int loop = opened.FirstOrDefault(o => keyword_at(o) is not KeywordType.If , -1);
                        if (loop < 0)
                            Errors[line] = "break는 반복문(while, repeat) 안에서만 쓸수 있습니다.";
                        else
                            breaks.Add((line , loop));
                        break;
                }
            }
            //end로 닫히지 않은 블록
            foreach (int line in opened)
            {
                Errors[line] = "모든 조건문(if, while 등) 키워드는 end 키워드로 종료 표시가 있어야 합니다.";
                //else가 있는 if라면 else도 오류
                if (Pair[line] >= 0)
                    Errors[Pair[line]] = Errors[line];
            }
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
