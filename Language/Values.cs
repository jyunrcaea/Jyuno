using System.Collections;
using System.Text;
using System.Globalization;

namespace Jyuno.Language;

//Jyuno 값(object?)의 숫자 변환/비교를 위한 도구
internal static class Values
{
    //오류 메시지에 넣을 값 (문화권과 무관하게, null은 "null")
    public static string Show(object? value) => value is null ? "null" : Text(value);

    //값을 문자열로 (Convert.ToString(value, InvariantCulture)와 같으며, 1.6.2부터 배열/리스트는 [1, 2, 3] 형식)
    //null은 빈 문자열, 원소의 null은 "null", 자기 자신을 담은 배열/리스트는 [...]
    public static string Text(object? value)
    {
        if (value is IList list && value is not string)
        {
            StringBuilder builder = new();
            append_list(builder , list , null);
            return builder.ToString();
        }
        return Convert.ToString(value , CultureInfo.InvariantCulture) ?? string.Empty;
    }
    static void append_list(StringBuilder builder , IList list , HashSet<object>? visiting)
    {
        visiting ??= new(ReferenceEqualityComparer.Instance);
        if (!visiting.Add(list))
        {
            builder.Append("[...]");
            return;
        }
        EnsureStack("값이 너무 깊게 중첩되어 있어서 문자열로 바꿀수 없습니다.");
        builder.Append('[');
        for (int i = 0 ; i < list.Count ; i++)
        {
            //공유된 리스트를 여러번 담은 값은 결과가 매우 길어질수 있으므로, 취소와 메모리 한도를 확인 (1.7.0부터)
            Interpreter.Tick();
            if (builder.Length >= 1 << 18)
            {
                //만들고 있는 내용과 결과 문자열
                Interpreter.ReserveString(builder.Length);
                Interpreter.Reserve(builder.Length * 4L);
            }
            if (i > 0)
                builder.Append(", ");
            object? item = list[i];
            if (item is IList inner && item is not string)
                append_list(builder , inner , visiting);
            else
                builder.Append(Show(item));
        }
        builder.Append(']');
        visiting.Remove(list);
    }

    //재귀하는 계산(비교, 복사, 문자열 변환)이 스택 오버플로로 프로세스를 끝내기 전에 오류를 냄 (1.6.2부터)
    internal static void EnsureStack(string message)
    {
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new JyunoLimitException(message);
    }

    //정수형 값이라면 long으로 꺼냄 (박싱된 long을 (int)로 캐스팅하면 예외가 나므로)
    public static bool TryGetInteger(object? value , out long result)
    {
        switch (value)
        {
            case long l: result = l; return true;
            case int i: result = i; return true;
            case short s: result = s; return true;
            case sbyte sb: result = sb; return true;
            case byte b: result = b; return true;
            case ushort us: result = us; return true;
            case uint ui: result = ui; return true;
            case ulong ul when ul <= long.MaxValue: result = (long)ul; return true;
            default: result = 0; return false;
        }
    }

    //정수 또는 실수 값이라면 double로 꺼냄
    public static bool TryGetReal(object? value , out double result)
    {
        switch (value)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case decimal m: result = (double)m; return true;
            case ulong ul: result = ul; return true;
        }
        if (TryGetInteger(value , out long integer))
        {
            result = integer;
            return true;
        }
        result = 0;
        return false;
    }

    //int 명령어: 다른 타입의 값을 정수로 변환 (변환할수 없다면 null)
    public static long? ToInteger(object value)
    {
        if (TryGetInteger(value , out long integer))
            return integer;
        if (TryGetReal(value , out double real))
        {
            //소수점 아래는 버림, NaN/무한대/long 범위를 넘는 값은 변환할수 없음
            real = Math.Truncate(real);
            if (real >= -9223372036854775808.0 && real < 9223372036854775808.0)
                return (long)real;
            return null;
        }
        if (value is bool b)
            return b ? 1 : 0;
        if (long.TryParse(Convert.ToString(value , CultureInfo.InvariantCulture) , NumberStyles.Integer , CultureInfo.InvariantCulture , out integer))
            return integer;
        return null;
    }

    //double 명령어: 다른 타입의 값을 실수로 변환 (변환할수 없다면 null)
    public static double? ToReal(object value)
    {
        if (TryGetReal(value , out double real))
            return real;
        if (value is bool b)
            return b ? 1 : 0;
        if (double.TryParse(Convert.ToString(value , CultureInfo.InvariantCulture) , NumberStyles.Float | NumberStyles.AllowThousands , CultureInfo.InvariantCulture , out real))
            return real;
        return null;
    }

    //equal 명령어: 숫자끼리는 값으로 비교하고, 그 외에는 Equals로 비교 (타입이 달라도 예외가 나지 않음)
    public static bool Same(object? a , object? b)
    {
        //구조체가 ==를 다시 정의했다면 그 결과 (1.6.0부터)
        if (a is StructValue && Overloads.Equal(a , b) is bool equal)
            return equal;
        if (TryGetInteger(a , out long integer_a) && TryGetInteger(b , out long integer_b))
            return integer_a == integer_b;
        if (TryGetReal(a , out double real_a) && TryGetReal(b , out double real_b))
            return real_a == real_b;
        //object.Equals(a, b)는 같은 객체라면 Equals를 부르지 않고 true이므로, 구조체 값이 같은 객체끼리도 필드를 비교하도록 직접 호출 (1.6.2부터)
        return a is null ? b is null : a.Equals(b);
    }

    //less/greater 명령어: a가 b보다 작으면 음수, 같으면 0, 크면 양수 (숫자끼리는 값으로, 문자열끼리는 유니코드 순서로 비교)
    //NaN처럼 순서를 매길수 없다면 null
    public static int? Compare(object? a , object? b)
    {
        //구조체가 <, >를 다시 정의했다면 그 결과 (1.6.0부터)
        if ((a is StructValue || b is StructValue) && Overloads.Order(a , b) is int order)
            return order;
        if (TryGetInteger(a , out long integer_a) && TryGetInteger(b , out long integer_b))
            return integer_a.CompareTo(integer_b);
        if (TryGetReal(a , out double real_a) && TryGetReal(b , out double real_b))
        {
            if (double.IsNaN(real_a) || double.IsNaN(real_b))
                return null;
            return real_a.CompareTo(real_b);
        }
        if (a is string string_a && b is string string_b)
            return string.CompareOrdinal(string_a , string_b);
        //그 외에는 같은 타입이면서 비교할수 있는 값만 (C# 프로그램이 넘긴 char, DateTime 등)
        if (a is IComparable comparable && b is not null && a.GetType() == b.GetType())
            return comparable.CompareTo(b);
        throw new JyunoException($"'{Show(a)}'와/과 '{Show(b)}'은/는 크기를 비교할수 없습니다.");
    }

    //사칙연산: 자주 쓰는 long/double 조합은 바로 계산하고, 그 외(int, decimal 등 프로그램이 넘긴 값)는 arithmetic에서 계산
    //(long끼리는 오버플로우시 순환, long과 double은 double로 계산)
    public static object? Add(object? a , object? b)
    {
        if (a is long la)
        {
            if (b is long lb) return unchecked(la + lb);
            if (b is double db) return la + db;
        }
        else if (a is double da)
        {
            if (b is double db) return da + db;
            if (b is long lb) return da + lb;
        }
        //문자열이 있다면 이어붙임 (null은 빈 문자열)
        if (a is string || b is string)
            return Interpreter.Concat(Text(a) , Text(b));
        return arithmetic(a , b , '+');
    }
    public static object? Multiply(object a , object b)
    {
        if (a is long la)
        {
            if (b is long lb) return unchecked(la * lb);
            if (b is double db) return la * db;
        }
        else if (a is double da)
        {
            if (b is double db) return da * db;
            if (b is long lb) return da * lb;
        }
        return arithmetic(a , b , '*');
    }
    public static object? Divide(object a , object b)
    {
        if (a is long la)
        {
            if (b is long lb && lb != 0 && !(la == long.MinValue && lb == -1)) return la / lb;
            if (b is double db) return la / db;
        }
        else if (a is double da)
        {
            if (b is double db) return da / db;
            if (b is long lb) return da / lb;
        }
        return arithmetic(a , b , '/');
    }
    public static object? Modulo(object a , object b)
    {
        if (a is long la)
        {
            if (b is long lb && lb != 0 && !(la == long.MinValue && lb == -1)) return la % lb;
            if (b is double db) return la % db;
        }
        else if (a is double da)
        {
            if (b is double db) return da % db;
            if (b is long lb) return da % lb;
        }
        return arithmetic(a , b , '%');
    }

    //그 외의 사칙연산 (1.6.2부터 dynamic 대신 직접 계산하고, 계산할수 없다면 .NET 예외 대신 Jyuno 오류)
    //decimal이 있다면 decimal로, 정수(int 등)끼리는 long으로, 그 외의 숫자는 double로 계산
    static object arithmetic(object? a , object? b , char op)
    {
        if (a is null || b is null)
            throw JyunoCommands.null_exception;
        try
        {
            if ((a is decimal || b is decimal) && to_decimal(a , out decimal ma) && to_decimal(b , out decimal mb))
            {
                return op switch {
                    '+' => ma + mb,
                    '*' => ma * mb,
                    '/' => ma / mb,
                    _ => ma % mb
                };
            }
            if (TryGetInteger(a , out long la) && TryGetInteger(b , out long lb))
            {
                if (op is '/' or '%')
                {
                    if (lb == 0)
                        throw new JyunoException("0으로 나눌수 없습니다.");
                    //long.MinValue / -1은 long으로 나타낼수 없음 (나머지는 0)
                    if (la == long.MinValue && lb == -1)
                        return op is '%' ? 0L : throw new JyunoException($"정수 나눗셈의 결과가 정수의 범위를 벗어났습니다. ({la} / {lb})");
                }
                return op switch {
                    '+' => unchecked(la + lb),
                    '*' => unchecked(la * lb),
                    '/' => la / lb,
                    _ => la % lb
                };
            }
            if (TryGetReal(a , out double da) && TryGetReal(b , out double db))
            {
                return op switch {
                    '+' => da + db,
                    '*' => da * db,
                    '/' => da / db,
                    _ => da % db
                };
            }
        }
        catch (Exception e) when (e is OverflowException or DivideByZeroException)
        {
            throw new JyunoException(e is DivideByZeroException ? "0으로 나눌수 없습니다." : $"계산 결과가 범위를 벗어났습니다. ('{Show(a)}' {op} '{Show(b)}')" , e);
        }
        throw new JyunoException($"'{op}' 연산은 숫자끼리만 할수 있습니다. ('{Show(a)}' {op} '{Show(b)}')");
    }
    static bool to_decimal(object value , out decimal result)
    {
        if (value is decimal m)
        {
            result = m;
            return true;
        }
        if (TryGetInteger(value , out long integer))
        {
            result = integer;
            return true;
        }
        result = 0;
        return false;
    }

    //math.abs (1.6.2부터 dynamic 대신 직접 계산, 숫자의 타입은 그대로)
    public static object Abs(object value)
    {
        try
        {
            return value switch {
                long l => Math.Abs(l),
                double d => Math.Abs(d),
                int i => Math.Abs(i),
                decimal m => Math.Abs(m),
                float f => Math.Abs(f),
                short h => Math.Abs(h),
                sbyte b => Math.Abs(b),
                byte or ushort or uint or ulong => value,
                _ => throw new JyunoException($"'{Show(value)}'은/는 숫자가 아니므로 절댓값을 구할수 없습니다.")
            };
        }
        catch (OverflowException e)
        {
            throw new JyunoException($"'{Show(value)}'의 절댓값은 정수의 범위를 벗어납니다." , e);
        }
    }
}
