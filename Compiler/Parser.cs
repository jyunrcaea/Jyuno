using Jyuno.Language;
using System.Globalization;
using System.Text;

namespace Jyuno.Compiler;

public class Parser
{
    public static IEnumerable<Token> Tokenizer(string text)
    {
        text = text.Trim();
        LinkedList<Token> stack = new();
        for(int index=0 ; index<text.Length; index++)
        {
            char start = text[index];
            if (start is ' ')
            {
                continue;
            }
            //숫자? (숫자 바로 앞에 붙은 '-'는 음수 부호)
            if (is_digit(start) || (start is '-' && index + 1 < text.Length && is_digit(text[index + 1])))
            {
                int begin = index;
                while (++index < text.Length && is_digit(text[index])) { }
                //실수라면?
                bool real = index < text.Length && text[index] is '.';
                if (real)
                {
                    while (++index < text.Length && is_digit(text[index])) { }
                }
                string number = text[begin..index];
                index--;
                if (real)
                {
                    //한 자리씩 더하면 오차가 쌓이므로 문자열을 그대로 변환
                    stack.AddLast(new Token(TokenType.Constant , double.Parse(number , NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint , CultureInfo.InvariantCulture)));
                    continue;
                }
                //정수라면? (long 범위를 넘으면 오류)
                if (!long.TryParse(number , NumberStyles.AllowLeadingSign , CultureInfo.InvariantCulture , out long num))
                {
                    stack.AddLast(new Token(TokenType.Error , $"정수 {number}은/는 long 범위를 벗어납니다."));
                    return stack;
                }
                stack.AddLast(new Token(TokenType.Constant , num));
                continue;
            }
            //연산자?
            if (Grammar.Prefixes.Contains(start))
            {
                stack.AddLast(new Token(TokenType.Prefix , start));
                continue;
            }
            //문자?
            if (start is '"' || start is '\'')
            {
                StringBuilder sb = new();
                while (++index < text.Length && text[index] != start)
                {
                    if (text[index] == '\\')
                    {
                        if (++index >= text.Length)
                        {
                            stack.AddLast(new Token(TokenType.Error , "문자열은 항상 따옴표로 끝나야 합니다."));
                            return stack;
                        }
                        if (text[index] is 'n') { sb.Append('\n'); continue; }
                        if (text[index] is 'r') {  sb.Append('\r'); continue; }
                    }
                    sb.Append(text[index]);
                }
                if (index >= text.Length)
                {
                    stack.AddLast(new Token(TokenType.Error , "문자열은 항상 따옴표로 끝나야 합니다."));
                    return stack;
                }
                stack.AddLast(new Token(TokenType.Constant , sb.ToString()));
                continue;
            }
            //글자!
            string name;
            {
                StringBuilder sb = new();
                for(;index < text.Length && !Grammar.Prefixes.Contains(start = text[index]) && start != ' ' && start != '"' && start != '\'' ; index++)
                {
                    sb.Append(start);
                }
                name = sb.ToString();
                index--;
            }
            //키워드인가?
            if (Grammar.Keywords.ContainsKey(name))
            {
                stack.AddLast(new Token(TokenType.Keyword, Grammar.Keywords[name]));
                continue;
            }
            //변수인지 뭔지는 그때 판단하는걸로
            stack.AddLast(new Token(TokenType.Name , name));
        }
        return stack;
    }

    static bool is_digit(char c) => '0' <= c && c <= '9';

    public static IEnumerable<GrammarError> Checker(string[] texts)
    {
        for (int line=0 ;line < texts.Length ;line++)
        {
            var ret = Tokenizer(texts[line]).ToArray();
            //빈 줄은 검사할 필요가 없음
            if (ret.Length is 0)
                continue;
            //토큰화 과정에서 에러가 있었다면
            if (ret.Last().type is TokenType.Error)
            {
                yield return new(line , (string)ret.Last().value);
                continue;
            }
            for(int i=1 ;i<ret.Length; i++)
            {
                switch(ret[i].type)
                {
                    //연산자라면?
                    case TokenType.Prefix:
                        switch ((char)ret[i].value)
                        {
                            case ':':
                                if (ret[i - 1].type is TokenType.Constant)
                                    yield return new(line , "상수에 값을 대입할수 없습니다.");
                                break;
                            case '+':
                            case '-':
                            case '*':
                            case '/':
                                if (ret[i - 1].type is TokenType.Prefix)
                                    yield return new(line , "잘못된 연산자 사용.");
                                break;
                        }
                        break;
                    //아니라면?
                }
            }
        }
    }

    public static bool IsTrue(in LinkedList<object?> target)
    {
        if (target.Count is 0)
            return false;
        else
            return IsTrue(target.First());
    }
    public static bool IsTrue(in object? target)
    {
        if (target is object obj)
        {
            if (
                (obj is long l && l == 0) ||
                (obj is string s && string.IsNullOrEmpty(s)) ||
                (obj is bool b && b is false) ||
                (obj is double d && d == 0) ||
                (obj is float f && f == 0) ||
                (obj is int i && i == 0) ||
                (obj is char c && c == '\0')
            )
            {
                return false;
            }
            return true;
        }
        else
        {
            return false;
        }
    }
}

