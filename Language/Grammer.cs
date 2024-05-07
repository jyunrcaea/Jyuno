namespace Jyuno.Language;

public class Grammer
{
    public static Dictionary<string , KeywordType> Keywords = new() {
        {"if", KeywordType.If },
        {"return",KeywordType.Return },
        {"repeat",KeywordType.Repeat },
        {"goto",KeywordType.Goto },
        {"func", KeywordType.Func },
        {"end" , KeywordType.End}
    };
    public enum KeywordType
    {
        If,
        Return,
        Repeat,
        Goto,
        Func,
        End
    }

    public static HashSet<char> Prefixs = new() {
        ':', '[', ']', '+', '-', '*','/','~','@','=','<','>', '(' ,')'
    };
}

public record ReturnInfo(dynamic? value)
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