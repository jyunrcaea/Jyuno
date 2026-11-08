using System.Globalization;

namespace Jyuno.Language;

//Jyuno 값(object?)의 숫자 변환/비교를 위한 도구
internal static class Values
{
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
        if (TryGetInteger(a , out long integer_a) && TryGetInteger(b , out long integer_b))
            return integer_a == integer_b;
        if (TryGetReal(a , out double real_a) && TryGetReal(b , out double real_b))
            return real_a == real_b;
        return Equals(a , b);
    }

    //less/greater 명령어: a가 b보다 작으면 음수, 같으면 0, 크면 양수 (숫자끼리는 값으로, 문자열끼리는 유니코드 순서로 비교)
    //NaN처럼 순서를 매길수 없다면 null
    public static int? Compare(object? a , object? b)
    {
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
        throw new JyunoException($"'{a ?? "null"}'와/과 '{b ?? "null"}'은/는 크기를 비교할수 없습니다.");
    }

    //사칙연산: 자주 쓰는 long/double 조합은 바로 계산하고, 그 외(int, decimal 등 프로그램이 넘긴 값)는 이전처럼 dynamic으로 계산
    //(dynamic과 결과가 같아야 함: long끼리는 오버플로우시 순환, long과 double은 double로 계산)
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
        dynamic? sum = a;
        sum += (dynamic?)b;
        return sum;
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
        dynamic mul = a;
        mul *= (dynamic)b;
        return mul;
    }
    public static object? Divide(object a , object b)
    {
        if (a is long la)
        {
            //long.MinValue / -1은 dynamic과 같은 예외가 나도록 아래에서 계산
            if (b is long lb && lb != 0 && !(la == long.MinValue && lb == -1)) return la / lb;
            if (b is double db) return la / db;
        }
        else if (a is double da)
        {
            if (b is double db) return da / db;
            if (b is long lb) return da / lb;
        }
        return (dynamic)a / (dynamic)b;
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
        return (dynamic)a % (dynamic)b;
    }
}
