using Jyuno.Language;
using System.Text;

namespace Jyuno.Complier;

public class Parser
{
    public static IEnumerable<Token> Tokenizer(string text)
    {
        for(int index=0 ; index<text.Length; index++)
        {
            char start = text[index];
            if (start is ' ')
            {
                continue;
            }
            //연산자?
            if (Grammer.Prefixs.Contains(start))
            {
                yield return new(TokenType.Prefix , start);
                continue;
            }
            //숫자?
            if ('0' <= start && start <= '9')
            {
                long num = start - '0';
                while (++index < text.Length && '0' <= text[index] && text[index] <= '9')
                {
                    num *= 10;
                    num += text[index] - '0';
                }
                //실수라면?
                if (index < text.Length && text[index] is '.')
                {
                    double d = num;
                    double dec = 0.1;
                    while (++index < text.Length && '0' <= text[index] && text[index] <= '9')
                    {
                        d += (text[index] - '0') * dec;
                        dec *= 0.1;
                    }
                    index--;
                    yield return new(TokenType.Constant, d);
                    continue;
                }
                //정수라면?
                index--;
                yield return new(TokenType.Constant, num);
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
                            yield return new(TokenType.Error , "문자열은 항상 따옴표로 끝나야 합니다.");
                            yield break;
                        }
                        if (text[index] is 'n') { sb.Append('\n'); continue; }
                        if (text[index] is 'r') {  sb.Append('\r'); continue; }
                    }
                    sb.Append(text[index]);
                }
                if (index >= text.Length)
                {
                    yield return new(TokenType.Error , "문자열은 항상 따옴표로 끝나야 합니다.");
                    yield break;
                }
                yield return new(TokenType.Constant, sb.ToString());
                continue;
            }
            //글자!
            string name;
            {
                StringBuilder sb = new();
                for(;index < text.Length && !Grammer.Prefixs.Contains(start = text[index]) && start != ' ' && start != '"' && start != '\'' ; index++)
                {
                    sb.Append(start);
                }
                name = sb.ToString();
                index--;
            }
            //키워드인가?
            if (Grammer.Keywords.ContainsKey(name))
            {
                yield return new(TokenType.Keyword, Grammer.Keywords[name]);
                continue;
            }
            //변수인지 뭔지는 그때 판단하는걸로
            yield return new(TokenType.Name, name);
        }
    }

    public static IEnumerable<GrammerError> Checker(string[] texts)
    {
        for (int line=0 ;line < texts.Length ;line++)
        {
            var ret = Tokenizer(texts[line]).ToArray();
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
}

