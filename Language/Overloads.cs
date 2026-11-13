using Jyuno.Compiler;

namespace Jyuno.Language;

//구조체가 다시 정의한 비교 연산자 (1.6.0부터)
//연산자 ==, <, > 등과 이 연산자로 비교하는 내장 함수(equal, less, greater, array.sort, array.contains 등)가 같은 기준을 쓰도록 한곳에서 계산
//왼쪽 값의 구조체 함수를 먼저 찾고, 없다면 아래처럼 다른 비교 연산자에서 이끌어냄 (C++20처럼)
//  a != b  →  !(a == b)
//  a > b   →  b < a        a < b   →  b > a
//  a <= b  →  !(a > b)     a >= b  →  !(a < b)
//다시 정의한 연산자가 없다면 null (원래대로 비교)
internal static class Overloads
{
    static bool call(object? receiver , string symbol , object? argument , out object? result)
    {
        if (receiver is StructValue structure)
            return structure.TryOperator(symbol , new[] { argument } , out result);
        result = null;
        return false;
    }

    public static bool? Equal(object? a , object? b) => call(a , "==" , b , out object? r) ? Parser.IsTrue(r) : null;

    public static bool? NotEqual(object? a , object? b)
    {
        if (call(a , "!=" , b , out object? r))
            return Parser.IsTrue(r);
        return Equal(a , b) is bool equal ? !equal : null;
    }

    public static bool? Less(object? a , object? b)
    {
        if (call(a , "<" , b , out object? r) || call(b , ">" , a , out r))
            return Parser.IsTrue(r);
        return null;
    }

    public static bool? Greater(object? a , object? b)
    {
        if (call(a , ">" , b , out object? r) || call(b , "<" , a , out r))
            return Parser.IsTrue(r);
        return null;
    }

    public static bool? LessOrEqual(object? a , object? b)
    {
        if (call(a , "<=" , b , out object? r))
            return Parser.IsTrue(r);
        return Greater(a , b) is bool greater ? !greater : null;
    }

    public static bool? GreaterOrEqual(object? a , object? b)
    {
        if (call(a , ">=" , b , out object? r))
            return Parser.IsTrue(r);
        return Less(a , b) is bool less ? !less : null;
    }

    //정렬 등에서 쓰는 순서 (a가 앞이면 음수, 같으면 0, 뒤면 양수)
    public static int? Order(object? a , object? b)
    {
        if (Less(a , b) is not bool less)
            return null;
        if (less)
            return -1;
        return Less(b , a) is true ? 1 : 0;
    }
}
