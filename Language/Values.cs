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
}
