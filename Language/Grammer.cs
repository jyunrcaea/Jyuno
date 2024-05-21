namespace Jyuno.Language;

public class Grammer
{
    public static Dictionary<string , KeywordType> Keywords = new() {
        {"if", KeywordType.If },
        {"return",KeywordType.Return },
        {"repeat",KeywordType.Repeat },
        {"goto",KeywordType.Goto },
        {"func", KeywordType.Func },
        {"end" , KeywordType.End},
        {"else", KeywordType.Else },
        {"while", KeywordType.While },
        {"break", KeywordType.Break }
    };

    public static HashSet<char> Prefixs = new() {
        ':', '[', ']', '+', '-', '*','/','~','@','=','<','>', '(' ,')'
    };

    public static HashSet<KeywordType> Conditonals = new() {
        KeywordType.If,
        KeywordType.While,
        KeywordType.Repeat
    };
}

public record ReturnInfo(object? value)
{
    public override string ToString()
    {
        return $"반환됨: {value}";
    }
}

public record GrammerError(int Line,string Message)
{
    public override string ToString()
    {
        return $"문법 오류: {Message} ({Line} 줄)";
    }
}


public enum KeywordType
{
    If,
    Return,
    Repeat,
    Goto,
    Func,
    End,
    Else,
    While,
    Break
}
