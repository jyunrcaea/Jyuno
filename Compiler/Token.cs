namespace Jyuno.Compiler;

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
    /// 기호 ('(', ')', '=', ':' 등, 값은 char)
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
    Error,
    /// <summary>
    /// 연산자 (+, ==, &amp;&amp;, += 등, 값은 기호 문자열, 1.6.0부터)
    /// 이름이 아니므로 변수/함수처럼 대입하거나 삭제할수 없음
    /// </summary>
    Operator
}
