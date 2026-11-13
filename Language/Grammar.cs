namespace Jyuno.Language;

public class Grammar
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
        {"break", KeywordType.Break },
        {"param", KeywordType.Param },
        {"endfunc", KeywordType.EndFunc },
        {"local", KeywordType.Local },
        {"for", KeywordType.For },
        {"struct", KeywordType.Struct },
        {"endstruct", KeywordType.EndStruct }
    };

    //기호: 이름을 끊는 글자 중 연산자가 아닌 것 ('='은 '=='처럼 연산자의 일부가 아니라면 대입/삭제)
    public static HashSet<char> Prefixes = new() {
        ':', '[', ']', '@', '=', '(' ,')'
    };

    //연산자에 쓰이는 글자 (1.6.0부터, 이 글자들도 이름을 끊음)
    public static HashSet<char> OperatorChars = new() {
        '+', '-', '*', '/', '%', '&', '|', '^', '!', '~', '<', '>', '?', '='
    };

    //연산자 기호 (긴 것부터 찾음)
    public static readonly string[] Operators = {
        "<<=", ">>=", "??=",
        "==", "!=", "<=", ">=", "&&", "||", "<<", ">>", "??",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
        "+", "-", "*", "/", "%", "&", "|", "^", "!", "~", "<", ">", "?"
    };

    //이항 연산자의 우선순위 (C#과 같음, 클수록 먼저 계산), 이항 연산자가 아니라면 -1
    //?: (조건)은 1, ??는 2이며 둘 다 오른쪽부터 묶음
    public static int BinaryPrecedence(string op) => op switch {
        "*" or "/" or "%" => 12,
        "+" or "-" => 11,
        "<<" or ">>" => 10,
        "<" or ">" or "<=" or ">=" => 9,
        "==" or "!=" => 8,
        "&" => 7,
        "^" => 6,
        "|" => 5,
        "&&" => 4,
        "||" => 3,
        "??" => 2,
        _ => -1
    };

    //단항 연산자인지 (+x, -x, !x, ~x)
    public static bool IsUnary(string op) => op is "+" or "-" or "!" or "~";

    //복합 대입 연산자 (x += 1) → 계산할 이항 연산자 (+), 아니라면 null
    public static string? CompoundAssignment(string op) => op.Length >= 2 && op[^1] is '=' && op is not ("==" or "!=" or "<=" or ">=") ? op[..^1] : null;

    //구조체에서 func로 다시 정의(오버로딩)할수 있는 연산자 (&&, ||, ??, ?:, 대입은 제외)
    public static bool IsOverloadable(string op) => op is "+" or "-" or "*" or "/" or "%" or "==" or "!=" or "<" or ">" or "<=" or ">="
        or "&" or "|" or "^" or "<<" or ">>" or "!" or "~";

    public static HashSet<KeywordType> Conditionals = new() {
        KeywordType.If,
        KeywordType.While,
        KeywordType.Repeat,
        KeywordType.For
    };
}

public record ReturnInfo(object? value)
{
    public override string ToString()
    {
        return $"반환됨: {value}";
    }
}

public record GrammarError(int Line,string Message)
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
    Break,
    Param,
    EndFunc,
    Local,
    For,
    Struct,
    EndStruct
}
