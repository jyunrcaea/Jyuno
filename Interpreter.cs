using Jyuno.Complier;
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
            return process_keyword(tokens , line);
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

    GrammerError? skip(object? antoher_endkey = null)
    {
        int skip = 0;
        while(true)
        {
            //스크립트의 끝에 도달한 경우
            if (++CurrentExecuteLine > scripts.Count)
            {
                return new GrammerError(CurrentExecuteLine, "모든 조건문(if, while 등) 키워드는 end 키워드로 종료 표시가 있어야 합니다.");
            }
            //길이가 0이면 건너뛰기
            Token[] ret = scripts[CurrentExecuteLine].tokens;
            if (ret.Length is 0)
            {
                continue;
            }
            //키워드가 아니면 건너뛰기
            Token first = ret[0];
            if (first.type != TokenType.Keyword)
            {
                continue;
            }
            //if/while/repeat 등 조건문 키워드라면 end 한번이상 건너뛰기
            if (Grammer.Conditonals.Contains((KeywordType)first.value))
            {
                //근데 캐싱된게 있다면 즉시 건너뛰기
                if (scripts[CurrentExecuteLine].cache is int cache)
                {
                    CurrentExecuteLine = cache;
                }
                else skip++; //그렇지 않으면 스킵 횟수만 증가
                continue;
            }
            //만약 end 키워드를 마주쳤다면
            if (first.value.Equals(KeywordType.End))
            {
                //현재 skip 카운팅이 0 초과라면 건너뛰기
                if (skip > 0)
                    skip--;
                else
                    return null; //그렇지 않으면 즉시 종료
            }
            //만약 종료 키워드를 찾았다면 (그리고 skip이 0이라면)
            if (first.value.Equals(antoher_endkey) && skip is 0)
            {
                return null; //종료
            }
        }
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
    Stack<int> keyword_book = new();
    object? process_keyword(in Token[] tokens,in int line)
    {
        switch ((KeywordType)tokens[0].value)
        {
            //반환
            case KeywordType.Return:
                return new ReturnInfo(token2value(tokens , 1 , out _));
            //이동
            case KeywordType.Goto:
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
            case KeywordType.If:
                #region 변수
                CommandLine cmd;
                #endregion
                //실행하지 않을꺼라면 else 또는 end를 찾을때까지 건너뛰기
                if (!Parser.IsTrue(token2value(tokens , 1 , out _)))
                {
                    //만약 캐싱한 값이 있다면 즉시 이동
                    cmd = scripts[line];
                    if (cmd.cache is int)
                    {
                        CurrentExecuteLine = (int)cmd.cache;
                        break;
                    }
                    //그렇지 않으면 직접 else 또는 end를 찾은후 캐싱
                    if (skip(KeywordType.Else) is GrammerError ge)
                    {
                        return ge;
                    }
                    cmd.cache = CurrentExecuteLine;
                }
                //실행할꺼면 어짜피 else 마주칠때까지 실행하면 됨
                break;
            case KeywordType.Else:
                //만약 캐싱한 값이 있다면?
                cmd = scripts[line];
                if (cmd.cache is int next)
                {
                    CurrentExecuteLine = next;
                    break;
                }
                //없다면 직접 스킵후 캐싱
                if (skip() is GrammerError ger)
                {
                    return ger;
                }
                cmd.cache = CurrentExecuteLine;
                break;
            case KeywordType.End:
                //예약된 키워드가 있다면 이동
                if (keyword_book.TryPop(out int move))
                {
                    CurrentExecuteLine = move;
                }
                break;
            case KeywordType.While:
                ret = token2value(tokens , 1 , out _);
                //실행해야 한다면 키워드 예약 스택에 푸시
                if (Parser.IsTrue(ret))
                {
                    keyword_book.Push(CurrentExecuteLine-1);
                } else
                {
                    //실행하지 말아야 한다면 end까지 건너뛰기
                    skip();
                }
                break;
        }
        return null;
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
    public CommandLine(string str)
    {
        script = str;
    }
    public object? cache = null;
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