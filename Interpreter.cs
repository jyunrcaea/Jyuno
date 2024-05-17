using Jyuno.Complier;
using Jyuno.Language;

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
    bool search_variable(string name,out dynamic? value)
    {
        foreach(var local in Locals)
        {
            if (local.TryGetValue(name , out value))
                return true;
        }
        if (runtime.Global.TryGetValue(name, out value)) return true;
        return false;
    }
    void substitute_variable(string name,dynamic? value)
    {
        VariableDictionary dict;
        if (Locals.Count > 0) {
            dict = Locals.Peek();
        } else
        {
            dict = runtime.Global;
        }

        if (!dict.TryAdd(name , value))
            dict[name] = value;
    }
    bool remove_variable(string name)
    {
        foreach(var stack in Locals)
        {
            if (stack.Remove(name)) return true;
        }
        if (runtime.Global.Remove(name)) return true;
        return false;
    }

    public List<CommandLine> scripts = new();
    public object? ExecuteLine(string cmd)
    {
        int index = scripts.Count;
        scripts.Add(new(cmd));
        return ExecuteLine(index);
    }
    public object? ExecuteLine(int line)
    {
        Token[] tokens = scripts[line].tokens;
        if (tokens.Length > 0 && tokens.Last().type is TokenType.Error)
        {
            return new GrammerError(line ,(string)scripts[line].tokens!.Last().value);
        }
        //키워드는 인터프리터 차원에서 관리
        if (tokens.Length is 0)
            return null;
        else if (tokens[0].type is TokenType.Keyword)
        {
            switch ((Grammer.KeywordType)tokens[0].value)
            {
                //반환
                case Grammer.KeywordType.Return:
                    return new ReturnInfo(token2value(tokens , 1, out _));
                //이동
                case Grammer.KeywordType.Goto:
                    var ret = token2value(tokens , 1 , out _);
                    if (ret.Count is 0)
                    {
                        throw new JyunoException("이동할 값을 넣지 않았습니다.");
                    }
                    if (ret.First() is int goto_line)
                    {
                        if (goto_line < 0)
                            throw new JyunoException("실행 위치를 음수로 이동할수 없습니다.");
                        CurrentExecuteLine = goto_line;
                    }
                    else
                        throw new JyunoException("실행 위치는 음이 아닌 정수여야 합니다.");
                    break;
                //만약
                case Grammer.KeywordType.If:
                    ret = token2value(tokens , 1 , out _);
                    bool enable = ret.Count > 0;
                    if (enable)
                    {
                        var first = ret.First();
                        if (
                            first is null ||
                            (first is long l && l.Equals(0)) ||
                            (first is bool b && b == false)
                        )
                        {
                            enable = false;
                        }
                    }
                    //실행하지 않을꺼라면 else 또는 end를 찾을때까지 건너뛰기
                    if (!enable)
                    {
                        if (skip(Grammer.KeywordType.Else, Grammer.KeywordType.End) is GrammerError ge)
                        {
                            return ge;
                        }
                    }
                    //실행할꺼면 어짜피 else 마주칠때까지 실행하면 됨
                    break;
                case Grammer.KeywordType.Else:
                    if (skip(Grammer.KeywordType.End) is GrammerError ger)
                    {
                        return ger;
                    }
                    break;
                case Grammer.KeywordType.End:
                    return new GrammerError(CurrentExecuteLine,"중단될수 없습니다.");
            }
            return null;
        }
       return execute(tokens , 0,out _);
    }
    public object? Run(int start = 0)
    {
        if (CurrentExecuteLine >= 0)
            throw new JyunoException("이미 실행중입니다.");
        for(CurrentExecuteLine = start ;CurrentExecuteLine<scripts.Count ; CurrentExecuteLine++)
        {
            var ret = ExecuteLine(CurrentExecuteLine);
            if (ret is ReturnInfo ri)
                return ri.value;
        }
        CurrentExecuteLine = -1;
        return null;
    }

    GrammerError? skip(params object[] end)
    {
        Token[] tk;
        object? first;
        do
        {
            //더이상 없다면... 그거대로 문제네
            if (++CurrentExecuteLine >= scripts.Count)
            {
                return new GrammerError(CurrentExecuteLine , "종료 키워드가 없습니다.");
            }

            tk = scripts[CurrentExecuteLine].tokens;
            if (tk.Length > 0 && tk.First().type is TokenType.Error)
            {
                return new GrammerError(CurrentExecuteLine , (string)tk.First().value);
            }
            first = tk.Length is 0 ? null : tk.First().value;
        }
        while (
            first is null ||
            !end.Contains(first)
        );
        return null;
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
                if (checkvalue(tokens,start+1,TokenType.Prefix,'=') && tokens.Length >= start+2)
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
                return token.value;
            //이름
            case TokenType.Name:
                //대입 연산인가?
                if (checkvalue(tokens,start+1,TokenType.Prefix,'=')) {
                    //근데 뒤에 인자가 더 없거나, ')' 이라면?
                    if (start+2 >= tokens.Length || checkvalue(tokens,start+2,TokenType.Prefix,')'))
                    {
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
                    return vi.Get();
                else
                    throw new JyunoException($"'{token.value}'는 함수 또는 변수가 아닙니다.");
            //연산자
            case TokenType.Prefix:
                if (token.value is '(')
                    return execute(tokens , start + 1,out end);
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
                    if ((i = e) >= arr.Length) throw new JyunoException("중괄호 '(' 다음에는 ')'로 끝나야만 합니다.");
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

    public void Dispose()
    {
        lock(runtime.Interpreters)
        {
            runtime.Interpreters.Remove(this);
        }
    }
}

public class CommandLine
{
    public string script;
    public Token[] tokens {
        get => tok ?? Parse();
    }
    private Token[] Parse()
    {
        return tok = Parser.Tokenizer(script).ToArray();
    }
    public CommandLine(string str)
    {
        script = str;
    }

    private Token[]? tok = null;
}