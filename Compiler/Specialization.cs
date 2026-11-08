using Jyuno.Compiler;
using Jyuno.Language;

namespace Jyuno;

//[실험] 인터프리터 수준의 프로필 기반 특화 (interpreter-level PGO)
//내장 함수 호출 자리(add i 1 등)마다 실행 중에 "어떤 함수가 불렸는지, 인자의 타입이 무엇이었는지"를 기록하고,
//충분히 기록하면 그 경우만 빠르게 처리하는 특화 경로로 바꿈 (인자 배열을 만들지 않고 바로 계산)
//특화할때 가정한 것(같은 내장 함수, 같은 인자 타입)이 틀리면 원래 경로로 계산한 다음 다시 기록하거나(탈최적화) 특화를 포기함
//실행 결과, 오류, 부수 효과는 특화하지 않았을 때와 같아야 함
public partial class Interpreter
{
    //JYUNO_SPECIALIZE 환경 변수로 비교 실험 (off: 1.3.2와 같음, cond: if/while 조건만, static: 미리 정한 빠른 경로, 그 외: 프로필 기반)
    internal enum SpecializeMode { Off, Condition, Static, Profile }
    internal static SpecializeMode Specialize = Environment.GetEnvironmentVariable("JYUNO_SPECIALIZE")?.Trim().ToLowerInvariant() switch {
        "off" => SpecializeMode.Off,
        "cond" => SpecializeMode.Condition,
        "static" => SpecializeMode.Static,
        _ => SpecializeMode.Profile
    };

    //실험 통계 (특화/탈최적화 횟수)
    internal static long SpecializedCount, DeoptCount, GenericCount;

    //특화한 경로가 처리할수 없는 값 (원래 경로로 계산해야 함)
    static readonly object Miss = new();
    //bool 결과는 매번 박싱하지 않고 같은 객체를 사용
    static readonly object True = true, False = false;
    static object box(bool value) => value ? True : False;

    //인자 타입 기록
    [Flags]
    enum Seen : byte { None = 0, Long = 1, Double = 2, String = 4, Bool = 8, Other = 16 }
    static Seen seen_of(object? value) => value switch {
        long => Seen.Long,
        double => Seen.Double,
        string => Seen.String,
        bool => Seen.Bool,
        _ => Seen.Other
    };

    //특화한 인자 타입 조합 (Any: 프로필 없이 모든 빠른 경로를 차례대로 시도)
    enum Combo : byte { Any, Long, Number, String, Bool }

    //인자 1~3개의 호출 자리를 특화할수 있는지
    static bool supports(Intrinsic op , int arity) => arity switch {
        1 => op is Intrinsic.Not,
        2 => op is not (Intrinsic.None or Intrinsic.Not),
        3 => op is Intrinsic.Add or Intrinsic.Mul,
        _ => false
    };

    //프로필(지금까지 본 인자 타입)로 특화할 조합을 정함 (여러 타입이 섞인 자리는 Any: 모든 빠른 경로를 차례대로 시도)
    static Combo choose(Intrinsic op , Seen seen)
    {
        if (seen is Seen.Long)
            return Combo.Long;
        if ((seen & ~(Seen.Long | Seen.Double)) is 0)
            return Combo.Number;
        if (seen is Seen.String && op is Intrinsic.Add or Intrinsic.Equal)
            return Combo.String;
        if (seen is Seen.Bool && op is Intrinsic.And or Intrinsic.Or or Intrinsic.Not or Intrinsic.Equal)
            return Combo.Bool;
        return Combo.Any;
    }

    static bool is_number(object? value) => value is long or double;

    //--- 빠른 경로: 내장 함수(JyunoCommands)와 결과/오류가 같아야 함, 처리할수 없다면 Miss ---

    static object fast1(Intrinsic op , Combo combo , object? x)
    {
        //not: 정수라면 비트 반전, 그 외에는 논리 부정
        if (op is not Intrinsic.Not)
            return Miss;
        switch (combo)
        {
            case Combo.Long:
                return x is long l ? ~l : Miss;
            case Combo.Bool:
                return x is bool b ? box(!b) : Miss;
            case Combo.Number:
                if (x is long n) return ~n;
                return x is double d ? box(d == 0) : Miss;
            default:
                if (x is long a) return ~a;
                if (x is bool c) return box(!c);
                return Miss;
        }
    }

    static object fast2(Intrinsic op , Combo combo , object? x , object? y)
    {
        switch (combo)
        {
            case Combo.Long:
                return x is long a && y is long b ? long2(op , a , b) : Miss;
            case Combo.Number:
                return is_number(x) && is_number(y) ? number2(op , x! , y!) : Miss;
            case Combo.String:
                if (x is string s && y is string t)
                {
                    if (op is Intrinsic.Add) return string.Concat(s , t);
                    if (op is Intrinsic.Equal) return box(s == t);
                }
                return Miss;
            case Combo.Bool:
                if (x is bool p && y is bool q)
                {
                    switch (op)
                    {
                        case Intrinsic.And: return box(p && q);
                        case Intrinsic.Or: return box(p || q);
                        case Intrinsic.Equal: return box(p == q);
                    }
                }
                return Miss;
            default:
                if (x is long la && y is long lb)
                    return long2(op , la , lb);
                if (is_number(x) && is_number(y))
                    return number2(op , x! , y!);
                return any2(op , x , y);
        }
    }

    static object long2(Intrinsic op , long a , long b)
    {
        switch (op)
        {
            case Intrinsic.Add: return unchecked(a + b);
            case Intrinsic.Sub: return unchecked(a - b);
            case Intrinsic.Mul: return unchecked(a * b);
            //0으로 나누기, long.MinValue / -1은 원래 경로에서 오류를 냄
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

    //long 또는 double (적어도 하나는 double일수 있음)
    static object number2(Intrinsic op , object x , object y)
    {
        switch (op)
        {
            case Intrinsic.Add: return Values.Add(x , y)!;
            case Intrinsic.Sub:
                if (x is long a && y is long b) return unchecked(a - b);
                return real(x) - real(y);
            //mul은 1L부터 곱해나가지만, 1을 곱해도 값이 바뀌지 않으므로 x * y와 같음
            case Intrinsic.Mul: return Values.Multiply(x , y)!;
            case Intrinsic.Div:
                if (y is long d && (d == 0 || (x is long n && n == long.MinValue && d == -1))) return Miss;
                return Values.Divide(x , y)!;
            case Intrinsic.Mod:
                if (y is long m && (m == 0 || (x is long k && k == long.MinValue && m == -1))) return Miss;
                return Values.Modulo(x , y)!;
            case Intrinsic.Equal: return box(Values.Same(x , y));
            case Intrinsic.Less: return box(Values.Compare(x , y) is int lo && lo < 0);
            case Intrinsic.Greater: return box(Values.Compare(x , y) is int go && go > 0);
            case Intrinsic.And:
                if (x is long p && y is long q) return p & q;
                return box(Parser.IsTrue(x) && Parser.IsTrue(y));
            case Intrinsic.Or:
                if (x is long r && y is long s) return r | s;
                return box(Parser.IsTrue(x) || Parser.IsTrue(y));
            default: return Miss;
        }
    }

    static double real(object value) => value is double d ? d : (long)value;

    //타입과 상관없이 계산할수 있는 내장 함수 (비교할수 없는 값이라면 원래와 같은 오류)
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

    static object fast3(Intrinsic op , Combo combo , object? x , object? y , object? z)
    {
        if (combo is Combo.Long or Combo.Any && x is long a && y is long b && z is long c)
        {
            if (op is Intrinsic.Add) return unchecked(a + b + c);
            if (op is Intrinsic.Mul) return unchecked(a * b * c);
            return Miss;
        }
        if (combo is Combo.Number or Combo.Any && is_number(x) && is_number(y) && is_number(z))
        {
            //원래처럼 왼쪽부터 차례대로 계산
            if (op is Intrinsic.Add) return Values.Add(Values.Add(x , y) , z)!;
            if (op is Intrinsic.Mul) return Values.Multiply(Values.Multiply(x! , y!)! , z!)!;
            return Miss;
        }
        if (combo is Combo.String or Combo.Any && op is Intrinsic.Add && x is string s && y is string t && z is string u)
            return string.Concat(s , t , u);
        return Miss;
    }

    //함수 호출 자리 하나 (프로필 기록 → 특화 → 탈최적화)
    sealed class SpecializedCall
    {
        //이만큼 실행한 다음 특화 (그 전에는 원래 경로로 실행하면서 기록)
        const int ProfileRuns = 8;
        //탈최적화가 이만큼 일어나면 타입을 가정하지 않는 경로(Any)로 고정 (타입이 계속 바뀌는 자리)
        const int MaxDeopts = 2;

        const byte Profiling = 0, Specialized = 1, Generic = 2;

        public SpecializedCall(Interpreter owner , NameSite site , Expr[] arguments , Func<object? , object?> generic , NativeFunction? static_callee)
        {
            this.owner = owner;
            this.site = site;
            this.generic = generic;
            arity = arguments.Length;
            a = arguments[0].Eval;
            b = arity > 1 ? arguments[1].Eval : null!;
            c = arity > 2 ? arguments[2].Eval : null!;
            //프로필 없이 미리 정한 빠른 경로를 사용 (비교 실험용)
            if (static_callee is not null)
            {
                callee = static_callee;
                op = static_callee.Intrinsic;
                combo = Combo.Any;
                state = Specialized;
                is_static = true;
            }
        }
        readonly Interpreter owner;
        readonly NameSite site;
        readonly Func<object? , object?> generic;
        readonly int arity;
        readonly Func<object?> a, b, c;
        readonly bool is_static;

        byte state = Profiling;
        NativeFunction? callee;
        Intrinsic op;
        Combo combo;
        Seen seen;
        int runs, deopts;

        public object? Eval()
        {
            if (!owner.resolve(site , out object? holder))
                throw new JyunoException($"'{site.Name}'은/는 존재하지 않는 함수/변수/명령어 입니다.");
            if (state is Specialized)
            {
                //가정 1: 같은 내장 함수
                //(인자를 계산하는 도중에 재귀 호출로 이 자리의 상태가 바뀔수 있으므로 지금 상태를 지역 변수로 고정)
                NativeFunction? function = callee;
                if (ReferenceEquals(holder , function))
                {
                    Intrinsic o = op;
                    Combo k = combo;
                    switch (arity)
                    {
                        case 1:
                        {
                            object? x = a();
                            object r = fast1(o , k , x);
                            return r != Miss ? r : deopt(function! , new[] { x });
                        }
                        case 2:
                        {
                            object? x = a();
                            object? y = b();
                            object r = fast2(o , k , x , y);
                            return r != Miss ? r : deopt(function! , new[] { x , y });
                        }
                        default:
                        {
                            object? x = a();
                            object? y = b();
                            object? z = c();
                            object r = fast3(o , k , x , y , z);
                            return r != Miss ? r : deopt(function! , new[] { x , y , z });
                        }
                    }
                }
                //이름이 다른 함수/변수를 가리키게 되었음 (func add 등)
                if (!is_static)
                {
                    Interlocked.Increment(ref DeoptCount);
                    reset();
                }
                return generic(holder);
            }
            if (state is Generic)
                return generic(holder);
            return profile(holder);
        }

        //원래 경로로 실행하면서 함수와 인자 타입을 기록
        object? profile(object? holder)
        {
            if (holder is not NativeFunction function || !supports(function.Intrinsic , arity) || (callee is not null && !ReferenceEquals(callee , function)))
            {
                //내장 함수가 아니거나 여러 함수가 불리는 자리
                state = Generic;
                Interlocked.Increment(ref GenericCount);
                return generic(holder);
            }
            callee = function;
            //원래 경로와 같은 순서로 인자를 계산
            object?[] values = new object?[arity];
            values[0] = a();
            if (arity > 1) values[1] = b();
            if (arity > 2) values[2] = c();
            foreach (object? value in values)
                seen |= seen_of(value);
            //(재귀 호출로 그 사이에 상태가 바뀌었다면 그대로 둠)
            if (state is Profiling && ++runs >= ProfileRuns)
            {
                op = function.Intrinsic;
                combo = choose(op , seen);
                state = Specialized;
                Interlocked.Increment(ref SpecializedCount);
            }
            return function.Execute(values);
        }

        //가정 2(인자 타입)가 틀렸음: 이미 계산한 인자로 원래 내장 함수를 실행
        object? deopt(NativeFunction function , object?[] values)
        {
            //이미 모든 빠른 경로를 시도했다면(Any) 그대로 원래 함수로 계산 (문자열 + 숫자 등)
            if (!is_static && combo is not Combo.Any && state is Specialized && ReferenceEquals(callee , function))
            {
                Interlocked.Increment(ref DeoptCount);
                //타입이 계속 바뀌는 자리는 여러 타입을 모두 시도하는 경로(Any)로 고정
                if (++deopts >= MaxDeopts)
                    combo = Combo.Any;
                else
                    restart();
            }
            return function.Execute(values);
        }

        //이름이 다른 함수/변수를 가리키게 되었음: 다시 기록하거나, 너무 자주 바뀌면 특화를 포기
        void reset()
        {
            if (state is Generic)
                return;
            if (++deopts >= MaxDeopts)
            {
                state = Generic;
                Interlocked.Increment(ref GenericCount);
                return;
            }
            restart();
        }

        void restart()
        {
            state = Profiling;
            callee = null;
            seen = Seen.None;
            runs = 0;
        }
    }

    //compile_call에서 사용: 특화할수 있는 호출 자리라면 특화하는 식을 돌려줌
    Func<object?>? specialize_call(NameSite site , Expr[] arguments , Func<object? , object?> generic)
    {
        if (arguments.Length is < 1 or > 3)
            return null;
        switch (Specialize)
        {
            case SpecializeMode.Profile:
                return new SpecializedCall(this , site , arguments , generic , null).Eval;
            case SpecializeMode.Static:
                //컴파일할때 이름이 특화할수 있는 내장 함수를 가리킬 때만
                if (search_global(site.Name , out object? holder) && holder is NativeFunction function && supports(function.Intrinsic , arguments.Length))
                    return new SpecializedCall(this , site , arguments , generic , function).Eval;
                return null;
            default:
                return null;
        }
    }
}
