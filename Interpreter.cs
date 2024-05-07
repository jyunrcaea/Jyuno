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
    public dynamic? ExecuteLine(string cmd)
    {
        int index = scripts.Count;
        scripts.Add(new(cmd));
        return ExecuteLine(index);
    }
    public dynamic? ExecuteLine(int line)
    {
        if (scripts[line].tokens is null)
        {
            if (scripts[line].Parse())
                return new GrammerError(line ,(string)scripts[line].tokens.Last().value);
        }
        //키워드는 인터프리터 차원에서 관리
        Token[] tokens = scripts[line].tokens!;
        if (tokens.Length is 0)
            return null;
        else if (tokens[0].type is TokenType.Keyword)
        {
            switch ((Grammer.KeywordType)tokens[0].value)
            {
                case Grammer.KeywordType.Return:
                    return new ReturnInfo(token2value(tokens , 1, out _));
            }
        }
       return execute(tokens , 0,out _);
    }
    public dynamic? Run(int start = 0)
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

    dynamic? execute(Token[] tokens , int start,out int end)
    {
        end = tokens.Length; //기본값
        if (start >= tokens.Length)
            return null;
        Token token = tokens[start];
        switch(token.type)
        {
            //상수
            case TokenType.Constant:
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
                            return remove_variable(token.value);
                        else
                            throw new JyunoException("런타임에서 변수/함수 삭제 연산을 허용하지 않습니다.");
                    }
                    //대입 연산이 금지되었는가?
                    if (BlockSubstitute)
                        throw new JyunoException("런타임에서 변수 대입 연산을 허용하지 않습니다.");
                    var ret = execute(tokens , start + 2,out end); //별다른 제한이 없음.
                    //변수가 존재하는가?
                    if (search_variable(token.value , out dynamic? v))
                    {
                        if (v is VariableInterface vi)
                            vi.Set(ret);
                        else
                            throw new JyunoException($"'{token.value}'은/는 변수가 아닙니다.");
                    }
                    else
                    {
                        substitute_variable(token.value , new JyunoVariable(ret));
                    }
                    return null;
                }
                //대입 연산이 아니면
                if (!search_variable(token.value,out dynamic? f))
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
    bool checkvalue(in Token[] arr,in int index,in TokenType type,in dynamic? value)
    {
        if (index >= arr.Length)
            return false;
        Token t = arr[index];
        return t.type == type && t.value == value;
    }
    LinkedList<dynamic?> token2value(Token[] arr,int start,out int end)
    {
        LinkedList<dynamic?> ret = new();
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
                if (search_variable(arr[i].value , out dynamic? v))
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
    public Token[]? tokens = null;
    public bool Parse()
    {
        this.tokens = Parser.Tokenizer(script).ToArray();
        if (tokens.Length > 0 && tokens.Last().type is TokenType.Error)
            return true;
        return false;
    }

    public CommandLine(string str)
    {
        script = str;
    }
}