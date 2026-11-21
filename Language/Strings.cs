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
        add("concat" , (args , _) => Interpreter.Concat(args.Select(text_of).ToArray()));
        add("join" , (args , name) => {
            need(args , 1 , name);
            string separator = text_of(args[0]);
            //값이 배열/리스트 하나라면 그 원소들을 이어붙임
            if (args.Length is 2 && args[1] is System.Collections.IList list)
                return Join(separator , list.Cast<object?>().Select(text_of).ToArray());
            return Join(separator , args.Skip(1).Select(text_of).ToArray());
        });
        add("format" , (args , name) => {
            need(args , 1 , name);
            string format = text(args , 0 , name);
            try
            {
                object?[] values = format_args(args[1..]);
                long length = format_length(format , values);
                Interpreter.ReserveString(length);
                return Interpreter.Hold(string.Format(CultureInfo.InvariantCulture , format , values) , length * 2);
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
            string replacement = text_or_null(args , 2 , name) ?? string.Empty;
            //더 긴 문자열로 바꾼다면 결과의 길이를 미리 계산 (최대로 늘어나도 작다면 세지 않음)
            if (replacement.Length > old.Length && (long)s.Length / old.Length * (replacement.Length - old.Length) + s.Length >= 1 << 19)
                Interpreter.ReserveString(s.Length + (long)count_of(s , old) * (replacement.Length - old.Length));
            return s.Replace(old , replacement , StringComparison.Ordinal);
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
            string value = text(args , 2 , name);
            Interpreter.ReserveString((long)s.Length + value.Length);
            return s.Insert(at , value);
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
            string s = text(args , 0 , name);
            Interpreter.ReserveString(s.Length);
            return s.ToUpperInvariant();
        });
        add("tolower" , (args , name) => {
            need(args , 1 , name);
            string s = text(args , 0 , name);
            Interpreter.ReserveString(s.Length);
            return s.ToLowerInvariant();
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
            string? separator = args.Length is 1 ? null : text(args , 1 , name);
            //조각마다 문자열 하나와 배열의 칸 하나 (조각의 최대 개수로 어림)
            Interpreter.Reserve(s.Length * 2L + (s.Length / Math.Max(separator?.Length ?? 1 , 1) + 1) * 32L);
            return separator is null ? s.Split() : s.Split(separator);
        });
        add("tochararray" , (args , name) => {
            need(args , 1 , name);
            string s = text(args , 0 , name);
            //글자마다 한 글자 문자열(32바이트)과 배열의 칸 하나
            Interpreter.Reserve(s.Length * 40L + 32);
            return s.Select(c => c.ToString()).ToArray();
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
    static string text_of(object? value) => Values.Text(value);

    //만들수 있는 문자열의 최대 길이 (mul의 반복, padleft/padright, 1.6.2부터, Native JS와 같음)
    internal const int MaxLength = 1 << 29;

    //console.write/writeline의 형식 문자열: Console.Write(format, args)처럼 첫번째 인자도 {0} (1.6.2부터 문화권과 관계없이 바꾸고, 잘못된 형식은 Jyuno 오류)
    internal static string Format(object format , object?[] args)
    {
        if (format is not string text)
            throw new JyunoException($"형식 문자열은 문자열이어야 합니다. ('{Values.Show(format)}')");
        try
        {
            object?[] values = format_args(args);
            Interpreter.ReserveString(format_length(text , values));
            return string.Format(CultureInfo.InvariantCulture , text , values);
        }
        catch (FormatException)
        {
            throw new JyunoException($"형식 문자열이 올바르지 않습니다: \"{text}\"");
        }
    }
    //구분자로 이어붙이기 (string.join, array.join): 긴 문자열이라면 만들기 전에 한도를 확인 (1.7.0부터)
    internal static string Join(string separator , string[] texts)
    {
        long length = (long)separator.Length * Math.Max(texts.Length - 1 , 0);
        foreach (string text in texts)
            length += text.Length;
        Interpreter.ReserveString(length);
        return Interpreter.Hold(string.Join(separator , texts) , length * 2);
    }

    //형식 문자열의 결과 길이 어림값 (1.7.0부터): 형식 문자열 + 값의 길이 + 중괄호 안의 숫자(너비, 자릿수 {0,1000} {0:D1000})
    //'{0:D999999999}'처럼 한 줄로 몇 GB를 만들수 있으므로, 만들기 전에 최대 길이와 메모리 한도를 확인하기 위함
    static long format_length(string format , object?[] values)
    {
        long length = format.Length;
        foreach (object? value in values)
            length += value is string s ? s.Length : 32;
        bool inside = false;
        long number = 0;
        foreach (char c in format)
        {
            if (c is '{')
                inside = true;
            else if (c is '}')
                inside = false;
            if (inside && c is >= '0' and <= '9')
            {
                number = Math.Min(number * 10 + (c - '0') , int.MaxValue);
                continue;
            }
            length += number;
            number = 0;
        }
        return length + number;
    }

    //겹치지 않게 찾은 value의 개수 (replace의 결과 길이 계산)
    static int count_of(string s , string value)
    {
        int count = 0;
        for (int at = s.IndexOf(value , StringComparison.Ordinal) ; at >= 0 ; at = s.IndexOf(value , at + value.Length , StringComparison.Ordinal))
            count++;
        return count;
    }

    //배열/리스트는 [1, 2, 3] 형식으로 (string.Format은 타입 이름을 씀)
    static object?[] format_args(object?[] args) => args.Select(o => o is System.Collections.IList && o is not string ? Values.Text(o) : o).ToArray();

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
        int width = index(args[1] , "길이" , 0 , MaxLength , name);
        char fill = ' ';
        if (args.Length > 2)
        {
            string c = text(args , 2 , name);
            if (c.Length is not 1)
                throw new JyunoException($"{name}: 채울 글자는 한 글자여야 합니다. ('{c}')");
            fill = c[0];
        }
        Interpreter.ReserveString(width);
        return left ? s.PadLeft(width , fill) : s.PadRight(width , fill);
    }
}
