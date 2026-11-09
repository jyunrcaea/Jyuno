using Jyuno.Compiler;
using Jyuno.Language;

namespace Jyuno;

//줄을 실행할 때마다 토큰을 다시 해석하지 않도록, 처음 실행할 때 한번만 클로저 트리로 컴파일함
//컴파일한 트리는 토큰을 앞에서부터 해석하던 이전 방식(execute/token2value)과 실행 순서, 오류, 부수 효과가 모두 같아야 함
public partial class Interpreter
{
    //컴파일된 식: 값을 계산하는 함수와, 미리 계산할수 있는 값(상수)인지에 대한 정보
    sealed class Expr
    {
        public Expr(Func<object?> eval)
        {
            Eval = eval;
        }
        public readonly Func<object?> Eval;
        //미리 계산된 값 (Guards의 이름이 모두 그대로일 때만 유효함)
        public bool IsConstant { get; private init; }
        public object? Value { get; private init; }
        //미리 계산할때 사용한 내장 함수들 (스크립트에서 같은 이름을 다시 정의하면 미리 계산한 값은 쓰지 않음)
        public (NameSite Site, object Holder)[] Guards { get; private init; } = Array.Empty<(NameSite, object)>();
        //괄호 안에서 EarlyEnd를 돌려줄수 있는지
        public bool MayEndEarly { get; init; }

        public static Expr Constant(object? value) => new(() => value) { IsConstant = true , Value = value };
        public static Expr Folded(Func<object?> eval , object? value , (NameSite, object)[] guards , bool may_end_early) => new(eval) { IsConstant = true , Value = value , Guards = guards , MayEndEarly = may_end_early };
    }

    //괄호 안의 식이 ')' 전에 끝났음 (인자가 뒤에 있는데 배열/리스트가 아닌 변수를 읽은 경우)
    //대입 연산은 끝까지 실행한 다음, 괄호를 처리하는 쪽에서 오류를 냄
    sealed class EarlyEnd
    {
        public EarlyEnd(object? value)
        {
            Value = value;
        }
        public readonly object? Value;
    }

    //컴파일된 줄
    internal sealed class CompiledLine
    {
        public CompiledLine(Interpreter owner , Token[] tokens)
        {
            Owner = owner;
            Tokens = tokens;
        }
        //이 줄을 컴파일한 인터프리터 (클로저가 인터프리터의 상태를 사용하므로 다른 인터프리터에서는 다시 컴파일함)
        public readonly Interpreter Owner;
        public readonly Token[] Tokens;
        //토큰화 오류
        public string? Error;
        //줄 맨 앞의 키워드
        public KeywordType? Keyword;
        //키워드가 없는 줄
        public Func<object?> Expression = () => null;
        //키워드 다음의 인자들 (return/goto/if/while/repeat는 1번째 토큰부터, for는 2번째 토큰부터)
        public Func<object?[]> Arguments = () => Array.Empty<object?>();
        //local (이름) = (값) 의 값
        public Func<object?>? LocalValue;
        //if/while의 조건
        public Func<bool> Condition = () => false;
    }

    CompiledLine compiled_line(int line)
    {
        CommandLine command = scripts[line];
        //다른 스레드의 인터프리터가 같은 줄을 컴파일할수 있으므로 한번만 읽음
        if (command.compiled is CompiledLine compiled && compiled.Owner == this)
            return compiled;
        compiled = compile_line(command.tokens);
        command.compiled = compiled;
        return compiled;
    }

    CompiledLine compile_line(Token[] tokens)
    {
        CompiledLine line = new(this , tokens);
        if (tokens.Length is 0)
            return line;
        if (tokens[^1].type is TokenType.Error)
        {
            line.Error = (string)tokens[^1].value;
            return line;
        }
        if (tokens[0].type is TokenType.Keyword)
        {
            KeywordType keyword = (KeywordType)tokens[0].value;
            line.Keyword = keyword;
            switch (keyword)
            {
                case KeywordType.If:
                case KeywordType.While:
                {
                    Expr[] arguments = compile_arguments(tokens , 1 , out _);
                    Func<object?[]> evaluate = make_arguments(arguments);
                    line.Arguments = evaluate;
                    //조건이 하나라면 인자 배열을 만들지 않고 바로 참/거짓을 판단
                    if (arguments.Length is 1)
                    {
                        Func<object?> condition = arguments[0].Eval;
                        line.Condition = () => Parser.IsTrue(condition());
                    }
                    else
                        line.Condition = () => is_true(evaluate());
                    break;
                }
                case KeywordType.Return:
                case KeywordType.Goto:
                case KeywordType.Repeat:
                    line.Arguments = make_arguments(compile_arguments(tokens , 1 , out _));
                    break;
                case KeywordType.For:
                    line.Arguments = make_arguments(compile_arguments(tokens , 2 , out _));
                    break;
                case KeywordType.Local:
                    if (tokens.Length > 3 && is_prefix(tokens , 2 , '='))
                        line.LocalValue = compile_expression(tokens , 3 , false , out _).Eval;
                    break;
            }
            return line;
        }
        line.Expression = compile_expression(tokens , 0 , false , out _).Eval;
        return line;
    }

    static bool is_prefix(Token[] tokens , int index , char prefix) => index < tokens.Length && tokens[index].type is TokenType.Prefix && tokens[index].value is char c && c == prefix;

    static JyunoException parenthesis_error() => new("괄호 '(' 다음에는 ')'로 끝나야만 합니다.");

    //start 위치부터 하나의 식(대입, 함수 호출, 변수 읽기 등)을 컴파일
    //end: 식 다음 토큰의 위치
    //closed: 식 바로 다음에 ')'가 와야 하는지 (괄호 안의 식)
    Expr compile_expression(Token[] tokens , int start , bool closed , out int end)
    {
        end = tokens.Length; //기본값
        if (start >= tokens.Length)
            return Expr.Constant(null);
        Token token = tokens[start];
        switch (token.type)
        {
            //상수
            case TokenType.Constant:
                object constant = token.value;
                //(상수) = (이름): 상수 만들기
                if (is_prefix(tokens , start + 1 , '=') && tokens.Length > start + 2 && tokens[start + 2].type is TokenType.Name)
                {
                    string constant_name = (string)tokens[start + 2].value;
                    return new Expr(() => {
                        substitute_variable(constant_name , new JyunoConstantVariable(constant));
                        return null;
                    });
                }
                end = start + 1;
                return Expr.Constant(constant);
            //이름
            case TokenType.Name:
                string name = (string)token.value;
                //대입 연산인가?
                if (is_prefix(tokens , start + 1 , '='))
                {
                    //뒤에 인자가 더 없거나 ')' 이라면 삭제 연산
                    if (start + 2 >= tokens.Length || is_prefix(tokens , start + 2 , ')'))
                    {
                        end = start + 2;
                        return new Expr(() => {
                            if (EnableRemoveVariable)
                                return remove_variable(name);
                            throw new JyunoException("런타임에서 변수/함수 삭제 연산을 허용하지 않습니다.");
                        });
                    }
                    Expr rhs = compile_expression(tokens , start + 2 , closed , out end);
                    Func<object?> value = rhs.Eval;
                    NameSite target = new(name);
                    return new Expr(() => {
                        //대입 연산이 금지되었는가?
                        if (BlockSubstitute)
                            throw new JyunoException("런타임에서 변수 대입 연산을 허용하지 않습니다.");
                        object? ret = value();
                        //값이 ')' 전에 끝났다면 대입한 다음 괄호를 처리하는 쪽에 알림
                        EarlyEnd? early = null;
                        if (ret is EarlyEnd e)
                        {
                            early = e;
                            ret = e.Value;
                        }
                        //변수가 존재하는가? (지금 영역부터 바깥 영역, 프로그램이 제공하는 변수 순서)
                        //없다면 지금 영역에 새로 만듦
                        if (resolve(target , out object? holder))
                        {
                            if (holder is VariableInterface vi)
                                assign(vi , name , ret);
                            else
                                throw new JyunoException($"'{name}'은/는 변수가 아닙니다.");
                        }
                        else
                        {
                            declare_variable(name , ret);
                        }
                        return early is null ? null : new EarlyEnd(null);
                    }) { MayEndEarly = rhs.MayEndEarly };
                }
                //레이블 연산인가?
                if (is_prefix(tokens , start + 1 , ':'))
                {
                    return new Expr(() => {
                        substitute_variable(name , new JyunoVariable(CurrentExecuteLine));
                        return null;
                    });
                }
                return compile_call(tokens , start , closed , out end);
            //연산자
            case TokenType.Prefix:
                if (token.value is '(')
                {
                    Expr inner = compile_expression(tokens , start + 1 , true , out int inner_end);
                    //괄호는 ')'로 닫혀야 하며, 그 다음 위치를 알려줌
                    if (!is_prefix(tokens , inner_end , ')'))
                        return throw_after(inner);
                    end = inner_end + 1;
                    return close(inner);
                }
                if (token.value is ')')
                {
                    end = start;
                    return Expr.Constant(null);
                }
                return Expr.Constant(null);
            //그 외
            default:
                return new Expr(() => throw new JyunoException("처리할수 없습니다."));
        }
    }

    //괄호 안의 식: ')' 전에 끝났다면 오류
    static Expr close(Expr inner)
    {
        if (!inner.MayEndEarly)
            return inner;
        Func<object?> eval = inner.Eval;
        Func<object?> checked_eval = () => {
            object? value = eval();
            if (value is EarlyEnd)
                throw parenthesis_error();
            return value;
        };
        return inner.IsConstant ? Expr.Folded(checked_eval , inner.Value , inner.Guards , false) : new Expr(checked_eval);
    }

    //괄호가 닫히지 않은 식: 식을 먼저 실행한 다음 오류
    static Expr throw_after(Expr inner)
    {
        Func<object?> eval = inner.Eval;
        return new Expr(() => {
            eval();
            throw parenthesis_error();
        });
    }

    //(이름) (인자들): 함수 호출, 변수 읽기, 또는 배열/리스트의 인덱스 읽기/쓰기
    Expr compile_call(Token[] tokens , int start , bool closed , out int end)
    {
        string name = (string)tokens[start].value;
        NameSite site = new(name);
        //뒤에 인자가 있는가? (변수라면 배열/리스트일때만 인자를 사용)
        bool has_arguments = start + 1 < tokens.Length && !is_prefix(tokens , start + 1 , ')');
        Expr[] arguments = compile_arguments(tokens , start + 1 , out end);
        Func<object?[]> evaluate_arguments = make_arguments(arguments);
        //이름이 가리키는 것(holder)을 원래대로 실행
        object? invoke(object? holder)
        {
            //함수 or 변수
            if (holder is FunctionInterface fi)
                return fi.Execute(evaluate_arguments());
            if (holder is VariableInterface vi)
            {
                object? value = vi.Get();
                if (has_arguments)
                {
                    //배열/리스트 뒤에 인자가 있다면 인덱스 읽기 (l 0) 또는 쓰기 (l 0 값)
                    if (value is System.Collections.IList list)
                    {
                        object?[] index = evaluate_arguments();
                        switch (index.Length)
                        {
                            case 0:
                                return list;
                            case 1:
                                return Collections.Get(list , index[0]);
                            case 2:
                                //대입 연산이 금지되었는가?
                                if (BlockSubstitute)
                                    throw new JyunoException("런타임에서 변수 대입 연산을 허용하지 않습니다.");
                                Collections.Set(list , index[0] , index[1]);
                                return null;
                            default:
                                throw new JyunoException("배열/리스트는 '(이름) (인덱스)' 또는 '(이름) (인덱스) (값)' 형식으로만 쓸수 있습니다.");
                        }
                    }
                    //변수는 인자를 사용하지 않으므로, 괄호 안이라면 변수 바로 다음에 ')'가 와야 함
                    if (closed)
                        return new EarlyEnd(read(value));
                }
                return read(value);
            }
            throw new JyunoException($"'{name}'는 함수 또는 변수가 아닙니다.");
        }
        //내장 함수 호출 자리라면 특화 (Compiler/IntrinsicCall.cs)
        Func<object?> call = specialize_call(site , arguments , invoke) ?? (() => {
            if (!resolve(site , out object? holder))
                throw new JyunoException($"'{name}'은/는 존재하지 않는 함수/변수/명령어 입니다.");
            return invoke(holder);
        });
        bool may_end_early = closed && has_arguments;
        return try_fold(site , arguments , call , may_end_early) ?? new Expr(call) { MayEndEarly = may_end_early };
    }

    //상수 계산: 내장 함수(add, mul 등)의 인자가 모두 상수라면 컴파일할때 미리 계산함
    //스크립트에서 같은 이름을 다시 정의/삭제할수 있으므로, 실행할때 이름이 여전히 그 내장 함수를 가리킬 때만 미리 계산한 값을 사용
    Expr? try_fold(NameSite site , Expr[] arguments , Func<object?> call , bool may_end_early)
    {
        foreach (Expr argument in arguments)
        {
            if (!argument.IsConstant)
                return null;
        }
        if (!search_global(site.Name , out object? holder) || holder is not NativeFunction { Foldable: Func<object?[] , bool> foldable } function)
            return null;
        object?[] values = new object?[arguments.Length];
        for (int i = 0 ; i < values.Length ; i++)
            values[i] = arguments[i].Value;
        object? result;
        try
        {
            if (!foldable(values))
                return null;
            result = function.Execute(values);
        }
        catch (Exception)
        {
            //오류는 실행할때 그대로 발생하도록 미리 계산하지 않음
            return null;
        }
        //바꿀수 없는 값만 공유할수 있음 (배열/리스트 등은 실행할때마다 새로 만들어야 함)
        if (result is not (null or long or double or bool or string))
            return null;
        List<(NameSite, object)> guard_list = new() { (site , holder) };
        foreach (Expr argument in arguments)
            guard_list.AddRange(argument.Guards);
        var guards = guard_list.ToArray();
        return Expr.Folded(() => {
            foreach (var (guard , expected) in guards)
            {
                if (!resolve(guard , out object? now) || !ReferenceEquals(now , expected))
                    return call();
            }
            return result;
        } , result , guards , may_end_early);
    }

    //start 위치부터 ')' 또는 줄 끝까지의 인자들을 컴파일
    //end: 인자를 끝낸 ')'의 위치 (없다면 줄의 길이)
    Expr[] compile_arguments(Token[] tokens , int start , out int end)
    {
        List<Expr> arguments = new();
        for (int i = start ; i < tokens.Length ; i++)
        {
            Token token = tokens[i];
            switch (token.type)
            {
                //상수
                case TokenType.Constant:
                    arguments.Add(Expr.Constant(token.value));
                    break;
                //이름: 변수의 값
                case TokenType.Name:
                    string name = (string)token.value;
                    NameSite site = new(name);
                    arguments.Add(new Expr(() => {
                        if (!resolve(site , out object? holder))
                            throw new JyunoException($"'{name}'은/는 알수없는 변수/함수 입니다.");
                        if (holder is VariableInterface vi)
                            return read(vi.Get());
                        throw new JyunoException($"'{name}'에서 값을 가져올수 없습니다.");
                    }));
                    break;
                case TokenType.Prefix:
                    if (token.value is '(')
                    {
                        Expr inner = compile_expression(tokens , i + 1 , true , out int inner_end);
                        //괄호는 ')'로 닫혀야 함 (아니라면 실행할때 오류가 나므로 뒤의 인자는 볼 필요가 없음)
                        if (!is_prefix(tokens , inner_end , ')'))
                        {
                            arguments.Add(throw_after(inner));
                            end = tokens.Length;
                            return arguments.ToArray();
                        }
                        arguments.Add(close(inner));
                        i = inner_end;
                        break;
                    }
                    if (token.value is ')')
                    {
                        end = i;
                        return arguments.ToArray();
                    }
                    //그 외의 연산자는 무시
                    break;
                    //키워드 등은 무시
            }
        }
        //모두 순회했다면
        end = tokens.Length;
        return arguments.ToArray();
    }

    //인자들을 차례대로 계산해서 배열로 만드는 함수 (함수가 인자 배열을 보관할수 있으므로 매번 새 배열)
    static Func<object?[]> make_arguments(Expr[] arguments)
    {
        switch (arguments.Length)
        {
            case 0:
                return () => Array.Empty<object?>();
            case 1:
            {
                Func<object?> a = arguments[0].Eval;
                return () => new[] { a() };
            }
            case 2:
            {
                Func<object?> a = arguments[0].Eval, b = arguments[1].Eval;
                return () => {
                    object? first = a();
                    return new[] { first , b() };
                };
            }
            default:
            {
                Func<object?>[] evals = Array.ConvertAll(arguments , o => o.Eval);
                return () => {
                    object?[] values = new object?[evals.Length];
                    for (int i = 0 ; i < values.Length ; i++)
                        values[i] = evals[i]();
                    return values;
                };
            }
        }
    }
}
