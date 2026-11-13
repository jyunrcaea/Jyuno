namespace Jyuno.Analysis;

[Flags]
public enum NameKinds
{
    None = 0,
    /// <summary>변수 (대입, local, param, for, 레이블)</summary>
    Variable = 1,
    /// <summary>함수 (func)</summary>
    Function = 2,
    /// <summary>상수 ((상수) = (이름))</summary>
    Constant = 4,
    /// <summary>구조체 (struct, 1.5.0부터)</summary>
    Struct = 8
}

public enum ScopeKind
{
    /// <summary>스크립트 최상위</summary>
    Top,
    /// <summary>함수 호출 한번</summary>
    Function,
    /// <summary>for 문의 반복 한번</summary>
    For,
    /// <summary>구조체 값 하나 (생성자와 구조체 함수의 바깥 영역, 1.5.0부터)</summary>
    Struct
}

/// <summary>
/// 변수 영역. 인터프리터는 실행하면서 영역에 이름을 만들지만, 어느 영역에 만들어질수 있는지는 코드만 보고 알수 있음
/// (대입은 지금 영역부터 바깥 영역으로 찾아서 없으면 지금 영역에 만들고, 함수 영역의 바깥은 함수를 정의한 영역)
/// </summary>
public sealed class Scope
{
    internal Scope(ScopeKind kind , Scope? parent , FunctionStatement? function , int id)
    {
        Kind = kind;
        Parent = parent;
        Function = function;
        Id = id;
    }
    public ScopeKind Kind { get; }
    /// <summary>바깥 영역 (함수 영역이라면 함수를 정의한 영역)</summary>
    public Scope? Parent { get; }
    /// <summary>이 영역이 들어있는 함수 (최상위라면 null)</summary>
    public FunctionStatement? Function { get; }
    /// <summary>스크립트 안에서 영역마다 다른 번호 (0부터)</summary>
    public int Id { get; }
    /// <summary>이 영역에 만들어질수 있는 이름과 종류 (만들어진 순서)</summary>
    public IReadOnlyDictionary<string , NameKinds> Names => names;
    public IEnumerable<string> NameOrder => order;
    readonly Dictionary<string , NameKinds> names = new();
    readonly List<string> order = new();
    internal void Add(string name , NameKinds kind)
    {
        if (names.TryGetValue(name , out NameKinds old))
            names[name] = old | kind;
        else
        {
            names[name] = kind;
            order.Add(name);
        }
    }
}
