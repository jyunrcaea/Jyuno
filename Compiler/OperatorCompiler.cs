using Jyuno.Compiler;
using Jyuno.Language;

namespace Jyuno;

//C 계열 연산자 (1.6.0부터)
//연산자는 이름이 아닌 특수 키워드이므로 다시 정의하거나 삭제할수 없고, 우선순위는 C#과 같음
//함수 호출(이름 + 인자들)은 모든 연산자보다 먼저 묶임: 'f a b + g c'는 '(f a b) + (g c)'
//&&, ||, ??, ?:는 단락 평가 (필요한 쪽만 계산)
//구조체는 'func +'처럼 연산자 이름의 구조체 함수를 만들어서 연산자를 다시 정의(오버로딩)할수 있음 (왼쪽 값의 구조체 함수를 오른쪽 값을 인자로 호출)
public partial class Interpreter
{
    enum Op : byte
    {
        Add, Sub, Mul, Div, Mod, Shl, Shr, Lt, Gt, Le, Ge, Eq, Ne, BitAnd, BitXor, BitOr, AndAlso, OrElse, Coalesce,
        Plus, Neg, Not, Complement
    }

    static Op binary_op(string symbol) => symbol switch {
        "+" => Op.Add, "-" => Op.Sub, "*" => Op.Mul, "/" => Op.Div, "%" => Op.Mod,
        "<<" => Op.Shl, ">>" => Op.Shr,
        "<" => Op.Lt, ">" => Op.Gt, "<=" => Op.Le, ">=" => Op.Ge, "==" => Op.Eq, "!=" => Op.Ne,
        "&" => Op.BitAnd, "^" => Op.BitXor, "|" => Op.BitOr,
        "&&" => Op.AndAlso, "||" => Op.OrElse, "??" => Op.Coalesce,
        _ => throw new ArgumentException(symbol)
    };

    static Op unary_op(string symbol) => symbol switch {
        "+" => Op.Plus, "-" => Op.Neg, "!" => Op.Not, "~" => Op.Complement,
        _ => throw new ArgumentException(symbol)
    };

    //구조체 함수로 다시 정의할 때의 이름 (func 다음의 기호)
    static string symbol_of(Op op) => op switch {
        Op.Add or Op.Plus => "+", Op.Sub or Op.Neg => "-", Op.Mul => "*", Op.Div => "/", Op.Mod => "%",
        Op.Shl => "<<", Op.Shr => ">>", Op.Lt => "<", Op.Gt => ">", Op.Le => "<=", Op.Ge => ">=", Op.Eq => "==", Op.Ne => "!=",
        Op.BitAnd => "&", Op.BitXor => "^", Op.BitOr => "|", Op.AndAlso => "&&", Op.OrElse => "||", Op.Coalesce => "??",
        Op.Not => "!", Op.Complement => "~",
        _ => "?"
    };

    static bool is_operator(Token[] tokens , int index , string symbol) => index < tokens.Length && tokens[index].type is TokenType.Operator && (string)tokens[index].value == symbol;

    //문법 오류가 있는 식 (실행하면 오류)
    static Expr syntax_error(string message) => new(() => throw new JyunoException(message)) { SyntaxError = message };

    //─────────── 컴파일 ───────────

    //start 위치부터 연산자 식 하나를 컴파일 (조건 연산자 ?: 가 가장 낮은 우선순위)
    //argument: 키워드(if, return 등)의 인자 자리인지 (값만 읽는 이름이 없을 때의 오류 메시지가 이전과 같도록)
    Expr compile_operators(Token[] tokens , int start , bool closed , bool argument , out int end)
    {
        Expr condition = compile_binary(tokens , start , closed , argument , 2 , out end);
        if (condition.SyntaxError is not null || !is_operator(tokens , end , "?"))
            return condition;
        if (operand_missing(tokens , end + 1))
            return fail("조건 연산자 '?' 다음에 값이 없습니다." , out end);
        Expr then = compile_operators(tokens , end + 1 , closed , argument , out int middle);
        if (then.SyntaxError is not null)
            return fail(then.SyntaxError , out end);
        if (!is_prefix(tokens , middle , ':'))
            return fail("조건 연산자 '?' 다음에는 ':'가 와야 합니다. (조건 ? 참일때 : 거짓일때)" , out end);
        if (operand_missing(tokens , middle + 1))
            return fail("조건 연산자 ':' 다음에 값이 없습니다." , out end);
        Expr otherwise = compile_operators(tokens , middle + 1 , closed , argument , out end);
        if (otherwise.SyntaxError is not null)
            return fail(otherwise.SyntaxError , out end);
        return make_conditional(operand(condition) , operand(then) , operand(otherwise));

        Expr fail(string message , out int at)
        {
            at = tokens.Length;
            return syntax_error(message);
        }
    }

    static bool operand_missing(Token[] tokens , int index) => index >= tokens.Length || is_prefix(tokens , index , ')') || is_prefix(tokens , index , ':');

    //우선순위가 min 이상인 이항 연산자로 이어진 식
    Expr compile_binary(Token[] tokens , int start , bool closed , bool argument , int min , out int end)
    {
        Expr left = compile_unary(tokens , start , closed , argument , out end);
        while (left.SyntaxError is null && end < tokens.Length && tokens[end].type is TokenType.Operator)
        {
            string symbol = (string)tokens[end].value;
            int precedence = Grammar.BinaryPrecedence(symbol);
            if (precedence < min)
                break;
            if (operand_missing(tokens , end + 1))
            {
                end = tokens.Length;
                return syntax_error($"연산자 '{symbol}' 다음에 값이 없습니다.");
            }
            //??는 오른쪽부터 묶음 (a ?? b ?? c = a ?? (b ?? c)), 나머지는 왼쪽부터
            Expr right = compile_binary(tokens , end + 1 , closed , argument , symbol is "??" ? precedence : precedence + 1 , out end);
            if (right.SyntaxError is not null)
            {
                end = tokens.Length;
                return right;
            }
            left = make_binary(binary_op(symbol) , operand(left) , operand(right));
        }
        return left;
    }

    Expr compile_unary(Token[] tokens , int start , bool closed , bool argument , out int end)
    {
        if (start < tokens.Length && tokens[start].type is TokenType.Operator)
        {
            string symbol = (string)tokens[start].value;
            if (!Grammar.IsUnary(symbol))
            {
                end = tokens.Length;
                return syntax_error($"연산자 '{symbol}' 앞에 값이 없습니다.");
            }
            if (operand_missing(tokens , start + 1))
            {
                end = tokens.Length;
                return syntax_error($"연산자 '{symbol}' 다음에 값이 없습니다.");
            }
            Expr inner = compile_unary(tokens , start + 1 , closed , argument , out end);
            if (inner.SyntaxError is not null)
                return inner;
            return make_unary(unary_op(symbol) , operand(inner));
        }
        return compile_primary(tokens , start , closed , argument , out end);
    }

    //연산자의 피연산자: 상수, 함수 호출(이름 + 인자들), 괄호
    Expr compile_primary(Token[] tokens , int start , bool closed , bool argument , out int end)
    {
        end = tokens.Length;
        if (start >= tokens.Length)
            return Expr.Constant(null);
        Token token = tokens[start];
        switch (token.type)
        {
            case TokenType.Constant:
                end = start + 1;
                return Expr.Constant(token.value);
            case TokenType.Name:
                return compile_call(tokens , start , closed , argument , out end);
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
            default:
                return new Expr(() => throw new JyunoException("처리할수 없습니다."));
        }
    }

    //키워드(if, while, return, goto, repeat, for) 다음의 인자들: 식을 차례대로 (보통은 하나)
    Expr[] compile_keyword_arguments(Token[] tokens , int start)
    {
        List<Expr> arguments = new();
        for (int i = start ; i < tokens.Length ;)
        {
            Token token = tokens[i];
            if (token.type is TokenType.Prefix && token.value is ')')
                break;
            //그 외의 기호, 키워드, 값 앞에 올수 없는 연산자는 무시 (이전과 같음)
            if ((token.type is TokenType.Prefix && token.value is not '(') || token.type is TokenType.Keyword || token.type is TokenType.Error
                || (token.type is TokenType.Operator && !Grammar.IsUnary((string)token.value)))
            {
                i++;
                continue;
            }
            arguments.Add(compile_operators(tokens , i , false , true , out int end));
            i = Math.Max(end , i + 1);
        }
        return arguments.ToArray();
    }

    //괄호 안의 식이 ')' 전에 끝났다면(EarlyEnd) 연산자를 계산하기 전에 오류
    static Expr operand(Expr e) => e.MayEndEarly ? close(e) : e;

    //(이름) (연산자)= (값): (이름) = (이름) (연산자) (값)
    Expr compile_compound(Token[] tokens , int start , string symbol , bool closed , out int end)
    {
        string name = (string)tokens[start].value;
        if (operand_missing(tokens , start + 2))
        {
            end = tokens.Length;
            return syntax_error($"연산자 '{symbol}=' 다음에 값이 없습니다.");
        }
        Expr current = compile_call(tokens , start , closed , false , out _);
        Expr rhs = compile_expression(tokens , start + 2 , closed , out end);
        if (rhs.SyntaxError is not null)
            return rhs;
        return make_assign(name , make_binary(binary_op(symbol) , current , operand(rhs)));
    }

    //─────────── 식 만들기 ───────────

    //바꿀수 없는 값이라면 컴파일할때 미리 계산 (연산자는 다시 정의할수 없으므로, 피연산자가 미리 계산한 내장 함수일 때만 확인이 필요)
    static Expr fold(Expr[] operands , Func<object?> eval , Func<object?> compute)
    {
        foreach (Expr o in operands)
        {
            if (!o.IsConstant)
                return new Expr(eval);
        }
        object? value;
        try
        {
            value = compute();
        }
        catch (Exception)
        {
            //오류는 실행할때 그대로 발생하도록 미리 계산하지 않음
            return new Expr(eval);
        }
        if (value is not (null or long or double or bool or string) || value is string { Length: > 4096 })
            return new Expr(eval);
        var guards = operands.SelectMany(o => o.Guards).ToArray();
        //미리 계산한 내장 함수가 있다면 실행할때 이름이 그대로인지 피연산자가 확인함 (바뀌었다면 다시 계산)
        return guards.Length is 0 ? Expr.Constant(value) : Expr.Folded(eval , value , guards , false);
    }

    Expr make_binary(Op op , Expr left , Expr right)
    {
        Func<object?> a = left.Eval, b = right.Eval;
        Func<object?> eval;
        switch (op)
        {
            case Op.AndAlso:
                eval = () => Parser.IsTrue(a()) ? box(Parser.IsTrue(b())) : False;
                break;
            case Op.OrElse:
                eval = () => Parser.IsTrue(a()) ? True : box(Parser.IsTrue(b()));
                break;
            case Op.Coalesce:
                eval = () => a() ?? b();
                break;
            case Op.Add:
                eval = () => {
                    object? x = a(), y = b();
                    if (x is long l && y is long r)
                        return unchecked(l + r);
                    return binary(Op.Add , x , y);
                };
                break;
            case Op.Sub:
                eval = () => {
                    object? x = a(), y = b();
                    if (x is long l && y is long r)
                        return unchecked(l - r);
                    return binary(Op.Sub , x , y);
                };
                break;
            case Op.Lt:
                eval = () => {
                    object? x = a(), y = b();
                    if (x is long l && y is long r)
                        return box(l < r);
                    return binary(Op.Lt , x , y);
                };
                break;
            case Op.Eq:
                eval = () => {
                    object? x = a(), y = b();
                    if (x is long l && y is long r)
                        return box(l == r);
                    return binary(Op.Eq , x , y);
                };
                break;
            default:
                eval = () => {
                    object? x = a();
                    return binary(op , x , b());
                };
                break;
        }
        //문자열 반복은 결과가 너무 길어질수 있으므로 미리 계산하지 않음 (mul의 상수 계산과 같은 기준)
        if (op is Op.Mul && left.IsConstant && left.Value is string)
            return new Expr(eval);
        return fold(new[] { left , right } , eval , eval);
    }

    Expr make_unary(Op op , Expr inner)
    {
        Func<object?> a = inner.Eval;
        Func<object?> eval = () => unary(op , a());
        return fold(new[] { inner } , eval , eval);
    }

    Expr make_conditional(Expr condition , Expr then , Expr otherwise)
    {
        Func<object?> c = condition.Eval, t = then.Eval, f = otherwise.Eval;
        Func<object?> eval = () => Parser.IsTrue(c()) ? t() : f();
        return fold(new[] { condition , then , otherwise } , eval , eval);
    }

    //─────────── 계산 ───────────

    //구조체 값이 다시 정의한 연산자 (구조체 함수)
    static FunctionInterface? overload(object? value , string symbol)
    {
        if (value is StructInstance instance && instance.Scope.Variables.TryGetValue(symbol , out object? holder) && holder is FunctionInterface function)
            return function;
        return null;
    }

    //구조체 값(receiver)이 다시 정의한 연산자를 호출 (없다면 false)
    //연산자 함수는 왼쪽 값의 복사본에서 실행됨 (함수가 필드를 바꿔도 원래 값은 바뀌지 않음, 방금 만들어진 값이라면 그대로 사용)
    bool try_overload(object? receiver , string symbol , object?[] args , out object? result)
    {
        if (receiver is StructInstance instance && overload(instance , symbol) is not null)
        {
            StructInstance own = take(instance);
            result = invoke_function(overload(own , symbol)! , args);
            return true;
        }
        result = null;
        return false;
    }

    //내장 함수와 같은 계산 (C# 함수를 호출할 때처럼 값이 인터프리터 밖으로 나갔다 들어옴)
    static object? builtin(Func<object?[] , object?> function , params object?[] args)
    {
        escape(args);
        object? result = function(args);
        escape(result);
        return result;
    }

    object? binary(Op op , object? x , object? y)
    {
        //정수끼리 (자주 쓰는 경우)
        if (x is long a && y is long b)
        {
            switch (op)
            {
                case Op.Add: return unchecked(a + b);
                case Op.Sub: return unchecked(a - b);
                case Op.Mul: return unchecked(a * b);
                //0으로 나누기, long.MinValue / -1은 내장 함수에서 오류를 냄
                case Op.Div: if (b != 0 && !(a == long.MinValue && b == -1)) return a / b; break;
                case Op.Mod: if (b != 0 && !(a == long.MinValue && b == -1)) return a % b; break;
                case Op.Shl: return a << (int)(b & 63);
                case Op.Shr: return a >> (int)(b & 63);
                case Op.Lt: return box(a < b);
                case Op.Gt: return box(a > b);
                case Op.Le: return box(a <= b);
                case Op.Ge: return box(a >= b);
                case Op.Eq: return box(a == b);
                case Op.Ne: return box(a != b);
                case Op.BitAnd: return a & b;
                case Op.BitXor: return a ^ b;
                case Op.BitOr: return a | b;
            }
        }
        //구조체 값이 다시 정의한 연산자: 왼쪽 값의 구조체 함수를 오른쪽 값을 인자로 호출
        if (try_overload(x , symbol_of(op) , new[] { y } , out object? result))
            return result;
        //비교 연산자는 다른 비교 연산자에서 이끌어냄 (Language/Overloads.cs)
        if (x is StructInstance || y is StructInstance)
        {
            bool? derived = op switch {
                Op.Ne => Overloads.NotEqual(x , y),
                Op.Lt => Overloads.Less(x , y),
                Op.Gt => Overloads.Greater(x , y),
                Op.Le => Overloads.LessOrEqual(x , y),
                Op.Ge => Overloads.GreaterOrEqual(x , y),
                _ => null
            };
            if (derived is bool value)
                return box(value);
        }
        switch (op)
        {
            case Op.Add: return builtin(JyunoCommands.Add , x , y);
            case Op.Sub: return builtin(JyunoCommands.Sub , x , y);
            case Op.Mul: return builtin(JyunoCommands.Mul , x , y);
            case Op.Div: return builtin(JyunoCommands.Div , x , y);
            case Op.Mod: return builtin(JyunoCommands.Mod , x , y);
            case Op.Eq: return box(Values.Same(x , y));
            case Op.Ne: return box(!Values.Same(x , y));
            case Op.Lt: return box(Values.Compare(x , y) is int lt && lt < 0);
            case Op.Gt: return box(Values.Compare(x , y) is int gt && gt > 0);
            case Op.Le: return box(Values.Compare(x , y) is int le && le <= 0);
            case Op.Ge: return box(Values.Compare(x , y) is int ge && ge >= 0);
            case Op.BitAnd: return builtin(JyunoCommands.And , x , y);
            case Op.BitOr: return builtin(JyunoCommands.Or , x , y);
            case Op.BitXor:
                if (Values.TryGetInteger(x , out long p) && Values.TryGetInteger(y , out long q))
                    return p ^ q;
                return box(Parser.IsTrue(x) != Parser.IsTrue(y));
            case Op.Shl:
            case Op.Shr:
                if (Values.TryGetInteger(x , out long value) && Values.TryGetInteger(y , out long count))
                    return op is Op.Shl ? value << (int)(count & 63) : value >> (int)(count & 63);
                throw new JyunoException($"'{symbol_of(op)}' 연산은 정수끼리만 할수 있습니다. ('{Values.Show(x)}' {symbol_of(op)} '{Values.Show(y)}')");
            default:
                throw new InvalidOperationException(op.ToString());
        }
    }

    object? unary(Op op , object? x)
    {
        switch (op)
        {
            case Op.Not:
                if (x is bool b)
                    return box(!b);
                break;
            case Op.Neg:
                if (x is long l)
                    return unchecked(-l);
                if (x is double d)
                    return -d;
                break;
            case Op.Complement:
                if (x is long c)
                    return ~c;
                break;
            case Op.Plus:
                if (x is long or double)
                    return x;
                break;
        }
        //구조체 값이 다시 정의한 연산자: 인자 없이 호출
        if (try_overload(x , symbol_of(op) , Array.Empty<object?>() , out object? result))
            return result;
        switch (op)
        {
            case Op.Not:
                return box(!Parser.IsTrue(x));
            case Op.Complement:
                if (Values.TryGetInteger(x , out long integer))
                    return ~integer;
                throw new JyunoException($"'~' 연산은 정수에만 할수 있습니다. ('{Values.Show(x)}')");
            default:
                //프로그램이 넘긴 int, decimal 등은 sub와 같이 계산
                if (Values.TryGetInteger(x , out long whole))
                    return op is Op.Neg ? unchecked(-whole) : whole;
                if (Values.TryGetReal(x , out double real))
                    return op is Op.Neg ? -real : real;
                throw new JyunoException($"'{symbol_of(op)}' 연산은 숫자에만 할수 있습니다. ('{Values.Show(x)}')");
        }
    }
}
