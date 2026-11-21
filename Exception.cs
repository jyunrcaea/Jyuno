namespace Jyuno;

public class JyunoException : Exception
{
    public JyunoException() : base() { }
    public JyunoException(string message) : base(message) { }
    public JyunoException(string message , Exception inner) : base(message , inner) { }

    /// <summary>오류가 난 줄 (0부터, 1.6.2부터). Run() 또는 ExecuteLine()이 실행하던 가장 안쪽의 줄이며, 알수 없다면 -1</summary>
    public int Line { get; internal set; } = -1;
}

/// <summary>
/// 스크립트가 throw로 던진 값이 잡히지 않고 Run()/ExecuteLine() 밖으로 나옴 (1.7.0부터).
/// 프로그램이 추가한 함수에서 던지면 스크립트의 catch가 <see cref="Value"/>를 그대로 받습니다.
/// </summary>
public class JyunoThrownException : JyunoException
{
    public JyunoThrownException(object? value) : base()
    {
        Value = value;
    }
    /// <summary>던진 값 (구조체 값은 <see cref="Language.StructValue"/>)</summary>
    public object? Value { get; internal set; }
    /// <summary>문자열을 던졌다면 그 문자열, 그 외의 값은 문자열로 바꾼 모양 (42, [1, 2], point { x = 1, y = 2 }, null)</summary>
    public override string Message => message ??= describe(Value);
    string? message;
    //큰 리스트를 던졌을수 있으므로 메시지는 필요할 때 한번만 만듦
    static string describe(object? value)
    {
        if (value is string text)
            return text;
        try
        {
            return Language.Values.Show(value);
        }
        catch (Exception)
        {
            return "(문자열로 바꿀수 없는 값)";
        }
    }
}

/// <summary>
/// 프로그램이 정한 한도(메모리 한도, 함수 호출의 깊이, 스택)를 넘음 (1.7.0부터).
/// 스크립트의 catch로 잡을수 없으며, 항상 Run()/ExecuteLine() 밖으로 나옵니다.
/// </summary>
public class JyunoLimitException : JyunoException
{
    public JyunoLimitException(string message) : base(message) { }
    public JyunoLimitException(string message , Exception inner) : base(message , inner) { }
}
