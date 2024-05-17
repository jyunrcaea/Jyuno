namespace Jyuno.Complier;

public record Token(TokenType type,object value)
{
    public override string ToString() => $"Type: {type}, Value: {value}";
}

public enum TokenType
{
    Unknown,
    /// <summary>
    /// 상수
    /// </summary>
    Constant,
    /// <summary>
    /// 연산자 (+,*,=, > 등)
    /// </summary>
    Prefix,
    /// <summary>
    /// 키워드 (if, return 등)
    /// </summary>
    Keyword,
    /// <summary>
    /// 이름
    /// </summary>
    Name,
    /// <summary>
    /// 오류
    /// </summary>
    Error
}
