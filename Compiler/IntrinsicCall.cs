using Jyuno.Compiler;
using Jyuno.Language;

namespace Jyuno;

//내장 함수 호출 자리 특화
//컴파일할때 이름이 add, less 같은 내장 함수를 가리키는 호출 자리(인자 1~3개)는
//실행할때 이름이 여전히 같은 내장 함수 객체를 가리키는지만 확인하고, 인자 배열을 만들지 않고 바로 계산함
//빠른 경로가 처리할수 없는 값(0으로 나누기, 문자열 + 숫자 등)은 이미 계산한 인자로 원래 내장 함수를 실행하므로
//실행 결과, 오류, 인자 계산 순서는 특화하지 않았을 때와 같음
public partial class Interpreter
{
    //빠른 경로가 처리할수 없는 값 (원래 내장 함수로 계산해야 함)
    static readonly object Miss = new();
    //bool 결과는 매번 박싱하지 않고 같은 객체를 사용
    static readonly object True = true, False = false;
    static object box(bool value) => value ? True : False;

    //인자 1~3개의 호출 자리를 특화할수 있는지
    static bool supports(Intrinsic op , int arity) => arity switch {
        1 => op is Intrinsic.Not,
        2 => op is not (Intrinsic.None or Intrinsic.Not),
        3 => op is Intrinsic.Add or Intrinsic.Mul,
        _ => false
    };

    static bool is_number(object? value) => value is long or double;

    //--- 빠른 경로: 내장 함수(JyunoCommands)와 결과가 같아야 함, 처리할수 없다면 Miss ---

    static object fast1(Intrinsic op , object? x)
    {
        //not: 정수라면 비트 반전, bool이라면 논리 부정
        if (op is Intrinsic.Not)
        {
            if (x is long a) return ~a;
            if (x is bool b) return box(!b);
        }
        return Miss;
    }

    static object fast2(Intrinsic op , object? x , object? y)
    {
        if (x is long a && y is long b)
            return long2(op , a , b);
        if (is_number(x) && is_number(y))
            return number2(op , x! , y!);
        return any2(op , x , y);
    }

    static object long2(Intrinsic op , long a , long b)
    {
        switch (op)
        {
            case Intrinsic.Add: return unchecked(a + b);
            case Intrinsic.Sub: return unchecked(a - b);
            case Intrinsic.Mul: return unchecked(a * b);
            //0으로 나누기, long.MinValue / -1은 원래 내장 함수에서 오류를 냄
            case Intrinsic.Div: return b != 0 && !(a == long.MinValue && b == -1) ? a / b : Miss;
            case Intrinsic.Mod: return b != 0 && !(a == long.MinValue && b == -1) ? a % b : Miss;
            case Intrinsic.Equal: return box(a == b);
            case Intrinsic.Less: return box(a < b);
            case Intrinsic.Greater: return box(a > b);
            case Intrinsic.And: return a & b;
            case Intrinsic.Or: return a | b;
            default: return Miss;
        }
    }

    //long 또는 double (적어도 하나는 double)
    static object number2(Intrinsic op , object x , object y)
    {
        switch (op)
        {
            case Intrinsic.Add: return Values.Add(x , y)!;
            case Intrinsic.Sub: return real(x) - real(y);
            //mul은 1L부터 곱해나가지만, 1을 곱해도 값이 바뀌지 않으므로 x * y와 같음
            case Intrinsic.Mul: return Values.Multiply(x , y)!;
            case Intrinsic.Div:
                if (y is long d && d == 0) return Miss;
                return Values.Divide(x , y)!;
            case Intrinsic.Mod:
                if (y is long m && m == 0) return Miss;
                return Values.Modulo(x , y)!;
            case Intrinsic.Equal: return box(Values.Same(x , y));
            case Intrinsic.Less: return box(Values.Compare(x , y) is int lo && lo < 0);
            case Intrinsic.Greater: return box(Values.Compare(x , y) is int go && go > 0);
            case Intrinsic.And: return box(Parser.IsTrue(x) && Parser.IsTrue(y));
            case Intrinsic.Or: return box(Parser.IsTrue(x) || Parser.IsTrue(y));
            default: return Miss;
        }
    }

    static double real(object value) => value is double d ? d : (long)value;

    //숫자가 아닌 값 (비교할수 없는 값이라면 원래와 같은 오류)
    static object any2(Intrinsic op , object? x , object? y)
    {
        switch (op)
        {
            case Intrinsic.Equal:
                if (x is string a && y is string b) return box(a == b);
                return box(Values.Same(x , y));
            case Intrinsic.Less: return box(Values.Compare(x , y) is int lo && lo < 0);
            case Intrinsic.Greater: return box(Values.Compare(x , y) is int go && go > 0);
            case Intrinsic.Add:
                if (x is string s && y is string t) return string.Concat(s , t);
                return Miss;
            case Intrinsic.And:
                if (x is bool p && y is bool q) return box(p && q);
                return Miss;
            case Intrinsic.Or:
                if (x is bool r && y is bool w) return box(r || w);
                return Miss;
            default: return Miss;
        }
    }

    static object fast3(Intrinsic op , object? x , object? y , object? z)
    {
        if (x is long a && y is long b && z is long c)
        {
            if (op is Intrinsic.Add) return unchecked(a + b + c);
            if (op is Intrinsic.Mul) return unchecked(a * b * c);
            return Miss;
        }
        if (is_number(x) && is_number(y) && is_number(z))
        {
            //원래처럼 왼쪽부터 차례대로 계산
            if (op is Intrinsic.Add) return Values.Add(Values.Add(x , y) , z)!;
            if (op is Intrinsic.Mul) return Values.Multiply(Values.Multiply(x! , y!)! , z!)!;
            return Miss;
        }
        if (op is Intrinsic.Add && x is string s && y is string t && z is string u)
            return string.Concat(s , t , u);
        return Miss;
    }

    //내장 함수 호출 자리 하나
    sealed class IntrinsicCall
    {
        public IntrinsicCall(Interpreter owner , NameSite site , Expr[] arguments , Func<object? , object?> generic , NativeFunction callee)
        {
            this.owner = owner;
            this.site = site;
            this.generic = generic;
            this.callee = callee;
            op = callee.Intrinsic;
            arity = arguments.Length;
            //[실험] 탈출 분석: 결과가 bool/정수뿐이고 인자를 담거나 바꾸지 않는 내장 함수라면, 변수의 구조체 값을 복사하지 않고 넘김
            bool borrows = op is Intrinsic.Equal or Intrinsic.Less or Intrinsic.Greater or Intrinsic.Not or Intrinsic.And or Intrinsic.Or;
            //빌린 값은 뒤의 인자를 계산하는 동안 바뀌면 안 되므로, 뒤의 인자가 모두 상수나 변수 읽기일 때만
            Func<object?> eval(int i)
            {
                Expr e = arguments[i];
                if (!borrows || e.Borrow is null)
                    return e.Eval;
                for (int j = i + 1 ; j < arguments.Length ; j++)
                {
                    if (!arguments[j].IsConstant && arguments[j].Borrow is null)
                        return e.Eval;
                }
                return e.Borrow;
            }
            a = eval(0);
            b = arity > 1 ? eval(1) : null!;
            c = arity > 2 ? eval(2) : null!;
        }
        readonly Interpreter owner;
        readonly NameSite site;
        readonly Func<object? , object?> generic;
        readonly NativeFunction callee;
        readonly Intrinsic op;
        readonly int arity;
        readonly Func<object?> a, b, c;

        public object? Eval()
        {
            if (!owner.resolve(site , out object? holder))
                throw new JyunoException($"'{site.Name}'은/는 존재하지 않는 함수/변수/명령어 입니다.");
            //이름이 다른 함수/변수를 가리키게 되었다면(func add, local add, add = 1 등) 원래대로 호출
            if (!ReferenceEquals(holder , callee))
                return generic(holder);
            //인자는 원래와 같이 왼쪽부터 계산
            switch (arity)
            {
                case 1:
                {
                    object? x = a();
                    object r = fast1(op , x);
                    return r != Miss ? r : owner.invoke_function(callee , new[] { x });
                }
                case 2:
                {
                    object? x = a();
                    object? y = b();
                    object r = fast2(op , x , y);
                    return r != Miss ? r : owner.invoke_function(callee , new[] { x , y });
                }
                default:
                {
                    object? x = a();
                    object? y = b();
                    object? z = c();
                    object r = fast3(op , x , y , z);
                    return r != Miss ? r : owner.invoke_function(callee , new[] { x , y , z });
                }
            }
        }
    }

    //compile_call에서 사용: 컴파일할때 이름이 특화할수 있는 내장 함수를 가리킨다면 특화한 식을 돌려줌
    Func<object?>? specialize_call(NameSite site , Expr[] arguments , Func<object? , object?> generic)
    {
        if (arguments.Length is < 1 or > 3)
            return null;
        if (search_global(site.Name , out object? holder) && holder is NativeFunction function && supports(function.Intrinsic , arguments.Length))
            return new IntrinsicCall(this , site , arguments , generic , function).Eval;
        return null;
    }
}
