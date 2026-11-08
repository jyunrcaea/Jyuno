using System.Globalization;
using System.Text;

namespace Jyuno.Language;

//string.* 명령어: C#의 System.String 메서드를 Jyuno에서 쓸수 있게 옮긴 것 (1.4.2부터)
//문자열 비교와 찾기는 모두 서수(ordinal, 유니코드 값) 기준이고, 대소문자 변환은 문화권과 무관한 Invariant 기준
//문자열을 받는 자리에 다른 타입의 값을 넣으면 오류 (concat, join, format은 모든 값을 문자열로 바꿈)
internal static class Strings
{
    public static void Add(VariableDictionary dict)
    {
        void add(string name , Func<object?[] , string , object?> function)
        {
            string full = "string." + name;
            dict.AddFunction(full , args => function(args , full));
        }
        add("concat" , (args , _) => {
            StringBuilder sb = new();
            foreach (object? arg in args)
                sb.Append(text_of(arg));
            return sb.ToString();
        });
        add("join" , (args , name) => {
            need(args , 1 , name);
            string separator = text_of(args[0]);
            //값이 배열/리스트 하나라면 그 원소들을 이어붙임
            if (args.Length is 2 && args[1] is System.Collections.IList list)
                return string.Join(separator , list.Cast<object?>().Select(text_of));
            return string.Join(separator , args.Skip(1).Select(text_of));
        });
        add("format" , (args , name) => {
            need(args , 1 , name);
            string format = text(args , 0 , name);
            try
            {
                return string.Format(CultureInfo.InvariantCulture , format , args[1..]);
            }
            catch (FormatException)
            {
                throw new JyunoException($"{name}: 형식 문자열이 올바르지 않습니다: \"{format}\"");
            }
        });
        add("compare" , (args , name) => {
            need(args , 2 , name);
            return (long)Math.Sign(string.CompareOrdinal(text_or_null(args , 0 , name) , text_or_null(args , 1 , name)));
        });
        add("equals" , (args , name) => {
            need(args , 2 , name);
            string? a = text_or_null(args , 0 , name), b = text_or_null(args , 1 , name);
            if (args.Length > 2 && Compiler.Parser.IsTrue(args[2]))
                return a is null || b is null ? a == b : string.Equals(a.ToUpperInvariant() , b.ToUpperInvariant() , StringComparison.Ordinal);
            return string.Equals(a , b , StringComparison.Ordinal);
        });
        add("isnullorempty" , (args , name) => {
            need(args , 1 , name);
            return string.IsNullOrEmpty(text_or_null(args , 0 , name));
        });
        add("isnullorwhitespace" , (args , name) => {
            need(args , 1 , name);
            return string.IsNullOrWhiteSpace(text_or_null(args , 0 , name));
        });
        add("indexof" , (args , name) => {
            need(args , 2 , name);
            string s = text(args , 0 , name), value = text(args , 1 , name);
            int start = args.Length > 2 ? index(args[2] , "시작 인덱스" , 0 , s.Length , name) : 0;
            return (long)s.IndexOf(value , start , StringComparison.Ordinal);
        });
        add("lastindexof" , (args , name) => {
            need(args , 2 , name);
            return (long)text(args , 0 , name).LastIndexOf(text(args , 1 , name) , StringComparison.Ordinal);
        });
        add("contains" , (args , name) => {
            need(args , 2 , name);
            return text(args , 0 , name).Contains(text(args , 1 , name) , StringComparison.Ordinal);
        });
        add("startswith" , (args , name) => {
            need(args , 2 , name);
            return text(args , 0 , name).StartsWith(text(args , 1 , name) , StringComparison.Ordinal);
        });
        add("endswith" , (args , name) => {
            need(args , 2 , name);
            return text(args , 0 , name).EndsWith(text(args , 1 , name) , StringComparison.Ordinal);
        });
        add("replace" , (args , name) => {
            need(args , 3 , name);
            string s = text(args , 0 , name), old = text(args , 1 , name);
            if (old.Length is 0)
                throw new JyunoException($"{name}: 바꿀 문자열은 빈 문자열일수 없습니다.");
            return s.Replace(old , text_or_null(args , 2 , name) ?? string.Empty , StringComparison.Ordinal);
        });
        add("substring" , (args , name) => {
            need(args , 2 , name);
            string s = text(args , 0 , name);
            int start = index(args[1] , "시작 인덱스" , 0 , s.Length , name);
            int length = args.Length > 2 ? index(args[2] , "길이" , 0 , s.Length - start , name) : s.Length - start;
            return s.Substring(start , length);
        });
        add("insert" , (args , name) => {
            need(args , 3 , name);
            string s = text(args , 0 , name);
            int at = index(args[1] , "인덱스" , 0 , s.Length , name);
            return s.Insert(at , text(args , 2 , name));
        });
        add("remove" , (args , name) => {
            need(args , 2 , name);
            string s = text(args , 0 , name);
            int start = index(args[1] , "시작 인덱스" , 0 , s.Length , name);
            int count = args.Length > 2 ? index(args[2] , "개수" , 0 , s.Length - start , name) : s.Length - start;
            return s.Remove(start , count);
        });
        add("toupper" , (args , name) => {
            need(args , 1 , name);
            return text(args , 0 , name).ToUpperInvariant();
        });
        add("tolower" , (args , name) => {
            need(args , 1 , name);
            return text(args , 0 , name).ToLowerInvariant();
        });
        //trim: 두번째 인자가 있다면 그 문자열에 있는 글자들을 제거 (없거나 빈 문자열이라면 공백 문자)
        add("trim" , (args , name) => trim(args , name , true , true));
        add("trimstart" , (args , name) => trim(args , name , true , false));
        add("trimend" , (args , name) => trim(args , name , false , true));
        add("padleft" , (args , name) => pad(args , name , true));
        add("padright" , (args , name) => pad(args , name , false));
        //split: 구분자가 없다면 공백 문자마다 나눔 (C#의 Split())
        add("split" , (args , name) => {
            need(args , 1 , name);
            string s = text(args , 0 , name);
            if (args.Length is 1)
                return s.Split();
            return s.Split(text(args , 1 , name));
        });
        add("tochararray" , (args , name) => {
            need(args , 1 , name);
            return text(args , 0 , name).Select(c => c.ToString()).ToArray();
        });
        //charcode: index 위치(생략하면 0)의 UTF-16 코드 (정수)
        add("charcode" , (args , name) => {
            need(args , 1 , name);
            string s = text(args , 0 , name);
            if (s.Length is 0)
                throw new JyunoException($"{name}: 빈 문자열입니다.");
            int at = args.Length > 1 ? index(args[1] , "인덱스" , 0 , s.Length - 1 , name) : 0;
            return (long)s[at];
        });
        //fromcharcode: UTF-16 코드(0 ~ 65535)들로 문자열을 만듦
        add("fromcharcode" , (args , name) => {
            StringBuilder sb = new(args.Length);
            foreach (object? arg in args)
                sb.Append((char)index(arg , "문자 코드" , 0 , char.MaxValue , name));
            return sb.ToString();
        });
    }

    static void need(object?[] args , int count , string name)
    {
        if (args.Length < count)
            throw new JyunoException($"{name}: 인자가 {count}개 이상 필요합니다.");
    }

    //문자열로 변환 (concat, join): null은 빈 문자열
    static string text_of(object? value) => Convert.ToString(value , CultureInfo.InvariantCulture) ?? string.Empty;

    //문자열 인자 (null이나 다른 타입이라면 오류)
    static string text(object?[] args , int i , string name)
    {
        return args[i] switch {
            string s => s,
            null => throw JyunoCommands.null_exception,
            _ => throw new JyunoException($"{name}: '{Values.Show(args[i])}'은/는 문자열이 아닙니다.")
        };
    }

    //문자열 또는 null 인자
    static string? text_or_null(object?[] args , int i , string name) => args[i] is null ? null : text(args , i , name);

    //정수 인자 (min ~ max)
    static int index(object? value , string what , long min , long max , string name)
    {
        if (!Values.TryGetInteger(value , out long i))
            throw new JyunoException($"{name}: {what}은/는 정수여야 합니다. ('{Values.Show(value)}')");
        if (i < min || i > max)
            throw new JyunoException($"{name}: {what} {i}은/는 범위({min} ~ {max})를 벗어났습니다.");
        return (int)i;
    }

    static string trim(object?[] args , string name , bool start , bool end)
    {
        need(args , 1 , name);
        string s = text(args , 0 , name);
        char[]? chars = args.Length > 1 ? text(args , 1 , name).ToCharArray() : null;
        if (chars is null || chars.Length is 0)
            return start && end ? s.Trim() : start ? s.TrimStart() : s.TrimEnd();
        return start && end ? s.Trim(chars) : start ? s.TrimStart(chars) : s.TrimEnd(chars);
    }

    //padleft/padright: 길이가 width가 되도록 글자(생략하면 공백)를 채움
    static string pad(object?[] args , string name , bool left)
    {
        need(args , 2 , name);
        string s = text(args , 0 , name);
        int width = index(args[1] , "길이" , 0 , Array.MaxLength , name);
        char fill = ' ';
        if (args.Length > 2)
        {
            string c = text(args , 2 , name);
            if (c.Length is not 1)
                throw new JyunoException($"{name}: 채울 글자는 한 글자여야 합니다. ('{c}')");
            fill = c[0];
        }
        return left ? s.PadLeft(width , fill) : s.PadRight(width , fill);
    }
}
