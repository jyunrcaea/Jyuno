namespace Jyuno;

public class JyunoException : Exception
{
    public JyunoException() : base() { }
    public JyunoException(string message) : base(message) { }
    public JyunoException(string message , Exception inner) : base(message , inner) { }

    /// <summary>오류가 난 줄 (0부터, 1.6.2부터). Run() 또는 ExecuteLine()이 실행하던 가장 안쪽의 줄이며, 알수 없다면 -1</summary>
    public int Line { get; internal set; } = -1;
}
