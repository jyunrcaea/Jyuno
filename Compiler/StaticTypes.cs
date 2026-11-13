using System.Text;
using Jyuno.Analysis;
using Jyuno.Language;

namespace Jyuno.Compiler;

//[프로토타입] 정적 타입 검사 (--static-typing)
//
//제한 모드 IR을 실행하지 않고 끝까지 훑어서, 실행 중에 "타입 때문에" 날수 있는 오류가 하나도 없다는 것을 보이지 못하면 거부합니다.
//  - 합 타입(union): 변수 하나가 int|string|null 처럼 여러 타입을 가질수 있음 (Crystal과 같은 방식, 가장 넓은 타입으로 뭉개지 않음)
//  - 흐름 민감: 같은 변수라도 문장마다 타입이 다름 (x = null 다음 x = 1 이면 그 뒤로는 int)
//  - 함수 단형화: 함수는 호출되는 인자 타입 조합마다 따로 검사 (호출되지 않은 함수는 검사하지 않음)
//  - 리스트는 원소 타입이 만들 때 정해짐 (불변, invariant): list 1 2 → list<int>, 여기에 문자열을 넣으면 거부
//  - 정의 여부도 타입의 일부: 정의되지 않았을수 있는 변수/함수를 읽으면 거부
//검사가 보장하는 것: 통과한 프로그램은 실행 중에 타입 오류(잘못된 타입의 연산, null 연산, 정의되지 않은 이름, 잘못된 인자 수)를 내지 않음
//보장하지 않는 것: 0으로 나누기, 인덱스 범위, 호출 깊이, 형식 문자열, 출력 제한 같은 "값" 오류
/// <summary>[실험] 정적 타입 검사의 엄격함</summary>
public enum TypeCheckLevel
{
    /// <summary>null까지 모두 추적 (console.readline, int "문자열"의 결과도 null일수 있음)</summary>
    Strict,
    /// <summary>입력/변환 경계(console.readline, int/double 변환, file.read)의 결과는 null이 아니라고 보고, 실행할 때 그 자리에서 확인 (Kotlin의 플랫폼 타입)</summary>
    Pragmatic,
    /// <summary>
    /// 실용 단계 + 유니온 중 하나라도 되는 연산은 통과시키고, 나머지 경우는 실행할 때 그 자리에서 확인 (Erlang Dialyzer의 success typing).
    /// 어떤 타입으로도 안 되는 연산만 오류
    /// </summary>
    Lenient
}

/// <summary>[실험] 타입 검사 결과 하나</summary>
public sealed record TypeDiagnostic(int Line , string Category , string Message)
{
    public override string ToString() => $"{Line + 1}번 줄: [{Category}] {Message}";
}

/// <summary>[실험] 정적 타입 검사 결과</summary>
public sealed class TypeCheckResult
{
    internal TypeCheckResult(IReadOnlyList<TypeDiagnostic> errors , IReadOnlyList<TypeDiagnostic> warnings , IReadOnlyList<string> types , int instances , int rounds)
    {
        Errors = errors;
        Warnings = warnings;
        Types = types;
        Instances = instances;
        Rounds = rounds;
    }
    public bool Success => Errors.Count is 0;
    /// <summary>제한 모드를 지키지 않아서 검사하지 못함</summary>
    public bool Restricted { get; init; }
    public IReadOnlyList<TypeDiagnostic> Errors { get; }
    /// <summary>검사하지 못한 부분 (호출되지 않은 함수 등)</summary>
    public IReadOnlyList<TypeDiagnostic> Warnings { get; }
    /// <summary>추론한 타입 (최상위 변수, 함수 인스턴스)</summary>
    public IReadOnlyList<string> Types { get; }
    public int Instances { get; }
    public int Rounds { get; }
}

/// <summary>[실험] 정적 타입 검사</summary>
public static class JyunoTypeChecker
{
    /// <summary>
    /// 제한 모드로 빌드하고 모든 타입을 추론합니다. 제한 모드를 지키지 않으면 JyunoBuildException.
    /// hosts: 호스트 함수/변수의 타입 시그니처 (예: "text.at" → "(string, int, any...) -> string", 변수는 "string").
    /// 시그니처가 없는 호스트 함수의 결과는 any (어떤 연산에도 쓸수 없음)
    /// </summary>
    public static TypeCheckResult Check(IReadOnlyList<string> lines , Runtime profile , TypeCheckLevel level = TypeCheckLevel.Strict , IReadOnlyDictionary<string , string>? hosts = null , bool narrowing = true)
    {
        IrProgram program;
        try
        {
            program = IrBuilder.Build(lines , profile , operators: true);
        }
        catch (JyunoBuildException e)
        {
            //제한 모드(삭제, 내장 함수 재정의, 구조체 등)를 지키지 않으면 타입을 정할수 없음
            return new TypeCheckResult(e.Errors.Select(o => new TypeDiagnostic(o.Line , "제한 모드" , o.Message)).ToList() , Array.Empty<TypeDiagnostic>() , Array.Empty<string>() , 0 , 0) { Restricted = true };
        }
        return new TypeChecker(program , profile , level , hosts) { Narrowing = narrowing }.Run();
    }

    /// <summary>JyunoWeb 채점 호스트(콘솔 + 예시 함수)의 시그니처</summary>
    public static IReadOnlyDictionary<string , string> JyunoWebHost { get; } = new Dictionary<string , string> {
        ["console.write"] = "() -> null; (any) -> null; (string, any...) -> null",
        ["console.writeline"] = "() -> null; (any) -> null; (string, any...) -> null",
        ["console.readline"] = "() -> string!",
        ["console.read"] = "() -> char",
        ["console.clear"] = "() -> null",
        ["console.title"] = "string",
        ["random"] = "() -> double; (int|double, any...) -> int; (int|double, int|double, any...) -> int",
        ["time.now"] = "(any...) -> int",
        ["text.at"] = "(string, int|double, any...) -> string",
        ["text.upper"] = "(string, any...) -> string",
    };
}

//─────────── 타입 ───────────

[Flags]
enum TK : ushort
{
    None = 0,
    Undef = 1, //정의되지 않음 (변수 상태에만)
    Null = 2,
    Int = 4,
    Real = 8,
    Bool = 16,
    Str = 32,
    Char = 64,
    Any = 128, //알수 없음 (호스트가 시그니처 없이 준 값)
    List = 256,
    Arr = 512,
    Fn = 1024, //함수 칸이 정의됨 (변수 상태에만)
    Values = Null | Int | Real | Bool | Str | Char | Any | List | Arr
}

//타입 = 종류 집합 + (리스트/배열이라면) 원소 타입과 실제 C# 원소 타입 후보 (Clr: Int=long, Real=double, Str, Bool, Any=object)
sealed class AType : IEquatable<AType>
{
    public readonly TK Kinds;
    public readonly AType? ListElem, ArrElem;
    public readonly TK ListClr, ArrClr;
    const int MaxDepth = 3;

    AType(TK kinds , AType? list_elem , TK list_clr , AType? arr_elem , TK arr_clr)
    {
        Kinds = kinds;
        ListElem = (kinds & TK.List) != 0 ? list_elem ?? Bottom : null;
        ArrElem = (kinds & TK.Arr) != 0 ? arr_elem ?? Bottom : null;
        ListClr = (kinds & TK.List) != 0 ? list_clr : TK.None;
        ArrClr = (kinds & TK.Arr) != 0 ? arr_clr : TK.None;
    }

    public static readonly AType Bottom = new(TK.None , null , TK.None , null , TK.None);
    public static readonly AType Undef = Of(TK.Undef), Null = Of(TK.Null), Int = Of(TK.Int), Real = Of(TK.Real), Bool = Of(TK.Bool),
        Str = Of(TK.Str), Char = Of(TK.Char), Any = Of(TK.Any), FnDefined = Of(TK.Fn);
    public static AType Of(TK kinds) => new(kinds , null , TK.None , null , TK.None);
    public static AType List(AType elem , TK clr) => new(TK.List , clip(elem , 1) , clr , null , TK.None);
    public static AType Arr(AType elem , TK clr) => new(TK.Arr , null , TK.None , clip(elem , 1) , clr);

    //리스트 안의 리스트가 너무 깊어지면 any (추론이 끝나도록)
    static AType clip(AType t , int depth)
    {
        if ((t.Kinds & (TK.List | TK.Arr)) == 0)
            return t;
        if (depth >= MaxDepth)
            return Of((t.Kinds & ~(TK.List | TK.Arr)) | TK.Any);
        return new AType(t.Kinds , t.ListElem is null ? null : clip(t.ListElem , depth + 1) , t.ListClr , t.ArrElem is null ? null : clip(t.ArrElem , depth + 1) , t.ArrClr);
    }

    public bool IsBottom => Kinds is TK.None;
    public bool Has(TK k) => (Kinds & k) != 0;

    public AType Union(AType other)
    {
        if (other.IsBottom || ReferenceEquals(this , other)) return this;
        if (IsBottom) return other;
        TK kinds = Kinds | other.Kinds;
        AType? le = ListElem is null ? other.ListElem : other.ListElem is null ? ListElem : ListElem.Union(other.ListElem);
        AType? ae = ArrElem is null ? other.ArrElem : other.ArrElem is null ? ArrElem : ArrElem.Union(other.ArrElem);
        AType result = new(kinds , le , ListClr | other.ListClr , ae , ArrClr | other.ArrClr);
        return Equals(result) ? this : result;
    }
    public static AType Union(IEnumerable<AType> types)
    {
        AType r = Bottom;
        foreach (AType t in types) r = r.Union(t);
        return r;
    }

    public AType Without(TK k)
    {
        if ((Kinds & k) == 0) return this;
        return new AType(Kinds & ~k , ListElem , ListClr , ArrElem , ArrClr);
    }

    //하나의 종류씩 나눔 (연산 결과를 조합마다 계산하기 위해)
    public IEnumerable<AType> Singles()
    {
        foreach (TK k in new[] { TK.Null , TK.Int , TK.Real , TK.Bool , TK.Str , TK.Char , TK.Any })
            if (Has(k)) yield return Of(k);
        if (Has(TK.List)) yield return new AType(TK.List , ListElem , ListClr , null , TK.None);
        if (Has(TK.Arr)) yield return new AType(TK.Arr , null , TK.None , ArrElem , ArrClr);
    }
    public int SingleCount => System.Numerics.BitOperations.PopCount((uint)(Kinds & TK.Values));

    //this의 모든 값이 other에 들어가는지
    public bool SubsetOf(AType other)
    {
        if (other.Has(TK.Any)) return true; //any 자리에는 무엇이든
        if ((Kinds & ~other.Kinds) != 0) return false;
        if (Has(TK.List) && !(ListElem!.SubsetOf(other.ListElem!) && other.ListElem!.SubsetOf(ListElem) && (ListClr & ~other.ListClr) == 0)) return false;
        if (Has(TK.Arr) && !(ArrElem!.SubsetOf(other.ArrElem!) && other.ArrElem!.SubsetOf(ArrElem) && (ArrClr & ~other.ArrClr) == 0)) return false;
        return true;
    }

    public bool Equals(AType? o) => o is not null && Kinds == o.Kinds && ListClr == o.ListClr && ArrClr == o.ArrClr
        && Equals(ListElem , o.ListElem) && Equals(ArrElem , o.ArrElem);
    public override bool Equals(object? obj) => Equals(obj as AType);
    public override int GetHashCode() => HashCode.Combine(Kinds , ListClr , ArrClr , ListElem , ArrElem);

    public override string ToString()
    {
        if (IsBottom) return "never";
        List<string> parts = new();
        if (Has(TK.Int)) parts.Add("int");
        if (Has(TK.Real)) parts.Add("double");
        if (Has(TK.Bool)) parts.Add("bool");
        if (Has(TK.Str)) parts.Add("string");
        if (Has(TK.Char)) parts.Add("char");
        if (Has(TK.List)) parts.Add($"list<{ListElem}>");
        if (Has(TK.Arr)) parts.Add($"array<{ArrElem}>");
        if (Has(TK.Any)) parts.Add("any");
        if (Has(TK.Null)) parts.Add("null");
        if (Has(TK.Fn)) parts.Add("func");
        if (Has(TK.Undef)) parts.Add("undefined");
        return string.Join("|" , parts);
    }
}

//─────────── 호스트 시그니처: "(string, int|double, any...) -> string; () -> null" ───────────

sealed class Signature
{
    public sealed record Overload(AType[] Params , AType? Rest , AType Result , bool Platform);
    public readonly List<Overload> Overloads = new();
    public AType? VariableType;
    public bool VariablePlatform;

    public static Signature Parse(string text)
    {
        Signature s = new();
        text = text.Trim();
        if (!text.StartsWith('('))
        {
            (s.VariableType , s.VariablePlatform) = type(text);
            return s;
        }
        foreach (string part in text.Split(';' , StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int arrow = part.LastIndexOf("->" , StringComparison.Ordinal);
            string ps = part[..arrow].Trim();
            ps = ps[1..^1];
            List<AType> list = new();
            AType? rest = null;
            foreach (string p in split_top(ps))
            {
                if (p.EndsWith("..." , StringComparison.Ordinal)) rest = type(p[..^3]).Type;
                else list.Add(type(p).Type);
            }
            var (result , platform) = type(part[(arrow + 2)..]);
            s.Overloads.Add(new Overload(list.ToArray() , rest , result , platform));
        }
        return s;
    }

    static IEnumerable<string> split_top(string text)
    {
        int depth = 0 , start = 0;
        for (int i = 0 ; i < text.Length ; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>') depth--;
            else if (text[i] == ',' && depth is 0)
            {
                yield return text[start..i].Trim();
                start = i + 1;
            }
        }
        if (text.Trim().Length > 0) yield return text[start..].Trim();
    }

    //"int|null", "list<int>", "string!" (!: 플랫폼 타입, 엄격 모드에서는 |null)
    static (AType Type, bool Platform) type(string text)
    {
        text = text.Trim();
        bool platform = text.EndsWith('!');
        if (platform) text = text[..^1];
        AType t = AType.Bottom;
        int depth = 0 , start = 0;
        List<string> atoms = new();
        for (int i = 0 ; i < text.Length ; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>') depth--;
            else if (text[i] == '|' && depth is 0) { atoms.Add(text[start..i]); start = i + 1; }
        }
        atoms.Add(text[start..]);
        foreach (string raw in atoms)
        {
            string a = raw.Trim();
            t = t.Union(a switch {
                "int" => AType.Int,
                "double" => AType.Real,
                "bool" => AType.Bool,
                "string" => AType.Str,
                "char" => AType.Char,
                "null" => AType.Null,
                "any" => AType.Any,
                _ when a.StartsWith("list<") => AType.List(type(a[5..^1]).Type , TK.Any),
                _ when a.StartsWith("array<") => AType.Arr(type(a[6..^1]).Type , TK.Any),
                _ => throw new ArgumentException($"알수 없는 타입 '{a}'")
            });
        }
        return (t , platform);
    }
}

//─────────── 검사기 ───────────

sealed class TypeChecker
{
    sealed class Unsupported : Exception { public Unsupported(string m) : base(m) { } }

    sealed class FrameInfo
    {
        public FrameInfo(int size , FuncInfo? owner)
        {
            Owner = owner;
            Slots = new SlotInfo[size];
            for (int i = 0 ; i < size ; i++) Slots[i] = new SlotInfo(this , i);
        }
        public readonly FuncInfo? Owner;
        public readonly SlotInfo[] Slots;
        public FrameInfo? Parent;
    }
    sealed class SlotInfo
    {
        public SlotInfo(FrameInfo frame , int index) { Frame = frame; Index = index; }
        public readonly FrameInfo Frame;
        public readonly int Index;
        public string Name = "?";
        public bool IsFunc, IsVar, HasConst, Shared;
        public int FuncDefs;
        public FuncInfo? Func;
        public AType Summary = AType.Bottom; //프로그램 전체에서 이 칸에 들어가는 모든 값
    }
    sealed class FuncInfo
    {
        public FuncInfo(IrStmt def , FrameInfo def_frame) { Def = def; DefFrame = def_frame; }
        public readonly IrStmt Def;
        public readonly FrameInfo DefFrame;
        public FrameInfo Frame = null!;
        public int Arity, Id;
        public string Name = "?";
        public readonly HashSet<string> Instances = new();
    }

    //흐름 민감 상태: 지금 함수가 가진 영역의 칸 → 타입 (Undef 포함)
    sealed class State
    {
        public readonly Dictionary<SlotInfo , AType> Map;
        public State() { Map = new(ReferenceEqualityComparer.Instance); }
        State(Dictionary<SlotInfo , AType> map) { Map = new(map , ReferenceEqualityComparer.Instance); }
        public State Clone() => new(Map);
        public AType Get(SlotInfo s) => Map.TryGetValue(s , out AType? t) ? t : AType.Undef;
        public static State? Join(State? a , State? b)
        {
            if (a is null) return b?.Clone();
            if (b is null) return a.Clone();
            State r = a.Clone();
            foreach (var (k , v) in b.Map)
                r.Map[k] = r.Map.TryGetValue(k , out AType? x) ? x.Union(v) : v.Union(AType.Undef);
            foreach (var (k , v) in a.Map)
                if (!b.Map.ContainsKey(k)) r.Map[k] = v.Union(AType.Undef);
            return r;
        }
        public bool SameAs(State? o)
        {
            if (o is null || o.Map.Count != Map.Count) return false;
            foreach (var (k , v) in Map)
                if (!o.Map.TryGetValue(k , out AType? x) || !x.Equals(v)) return false;
            return true;
        }
    }

    enum Def { Yes , No , Maybe }

    //검사 중인 함수 하나 (최상위는 Func = null)
    sealed class Ctx
    {
        public FuncInfo? Func;
        public AType[] Args = Array.Empty<AType>();
        public Dictionary<SlotInfo , Def> Outer = new(ReferenceEqualityComparer.Instance);
        public AType Ret = AType.Bottom;
        public readonly Dictionary<(IrStmt, GotoTargetKind) , State> Labels = new();
        public readonly Dictionary<IrStmt , State> Breaks = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<IrStmt , State> Continues = new(ReferenceEqualityComparer.Instance);
        public int LabelVersion;
    }

    readonly IrProgram program;
    readonly Runtime profile;
    readonly TypeCheckLevel level;
    readonly Dictionary<string , Signature> hosts = new(StringComparer.Ordinal);
    readonly Dictionary<IrName , SlotInfo[]> names = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<object , FrameInfo> frames = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<IrStmt , FuncInfo> functions = new(ReferenceEqualityComparer.Instance);
    readonly List<SlotInfo> all_slots = new();
    static readonly object RootKey = new();

    readonly Dictionary<(int, string, string) , TypeDiagnostic> errors = new();
    //느슨한 단계에서 실행할 때 확인할 곳 (경고)
    readonly Dictionary<(int, string, string) , TypeDiagnostic> checks = new();
    /// <summary>조건으로 유니온 타입을 좁힘 (x == null, x != null, if x, &&, ||, ?:)</summary>
    public bool Narrowing = true;
    readonly Dictionary<string , AType> returns = new(StringComparer.Ordinal);
    readonly Dictionary<string , AType> memo = new(StringComparer.Ordinal);
    readonly HashSet<string> in_progress = new(StringComparer.Ordinal);
    bool changed;
    int instances;
    const int MaxInstances = 3000;

    public TypeChecker(IrProgram program , Runtime profile , TypeCheckLevel level , IReadOnlyDictionary<string , string>? hosts)
    {
        this.program = program;
        this.profile = profile;
        this.level = level;
        if (hosts is not null)
            foreach (var (k , v) in hosts) this.hosts[k] = Signature.Parse(v);
    }

    public TypeCheckResult Run()
    {
        List<TypeDiagnostic> warnings = new();
        FrameInfo root;
        try
        {
            root = frame_of(RootKey , program.TopFrameSize , null , null);
            stack.Add(root);
            resolve_block(program.Root , null);
            stack.Clear();
            foreach (SlotInfo s in all_slots)
            {
                if (s.IsFunc && s.IsVar) throw new Unsupported($"'{s.Name}'을/를 함수와 변수로 같이 씀");
                if (s.FuncDefs > 1) throw new Unsupported($"함수 '{s.Name}'을/를 여러번 정의함");
            }
        }
        catch (Unsupported e)
        {
            return new TypeCheckResult(new[] { new TypeDiagnostic(0 , "구조" , e.Message) } , warnings , Array.Empty<string>() , 0 , 0);
        }
        int rounds = 0;
        Ctx top = new();
        do
        {
            rounds++;
            changed = false;
            errors.Clear();
            checks.Clear();
            memo.Clear();
            instances = 0;
            foreach (FuncInfo f in functions.Values) f.Instances.Clear();
            top = new Ctx();
            try
            {
                State st = enter(new State() , root);
                stack.Add(root);
                block(program.Root , st , top);
                stack.Clear();
            }
            catch (Unsupported e)
            {
                return new TypeCheckResult(new[] { new TypeDiagnostic(0 , "구조" , e.Message) } , warnings , Array.Empty<string>() , instances , rounds);
            }
            if (rounds > 60)
            {
                error(0 , "구조" , "타입 추론이 끝나지 않습니다.");
                break;
            }
        } while (changed);

        foreach (FuncInfo f in functions.Values)
            if (f.Instances.Count is 0)
                warnings.Add(new TypeDiagnostic(f.Def.Line , "검사 안 함" , $"함수 '{f.Name}'은/는 호출되지 않아서 검사하지 않았습니다."));
        warnings.AddRange(checks.Values.OrderBy(o => o.Line));
        List<string> types = new();
        foreach (SlotInfo s in root.Slots)
            if (s.IsVar) types.Add($"{s.Name}: {s.Summary}");
        foreach (FuncInfo f in functions.Values)
            foreach (string key in f.Instances.OrderBy(o => o))
                types.Add($"{key} -> {returns.GetValueOrDefault(key , AType.Bottom)}");
        return new TypeCheckResult(errors.Values.OrderBy(o => o.Line).ToList() , warnings , types , instances , rounds);
    }

    //─────────── 1. 이름과 칸 (StaticJit과 같은 방식) ───────────

    readonly List<FrameInfo> stack = new();
    FuncInfo? resolving;

    FrameInfo frame_of(object key , int size , FuncInfo? owner , FrameInfo? parent)
    {
        if (!frames.TryGetValue(key , out FrameInfo? f))
        {
            frames[key] = f = new FrameInfo(size , owner) { Parent = parent };
            all_slots.AddRange(f.Slots);
        }
        return f;
    }
    SlotInfo slot_at(int hops , int slot)
    {
        if (hops >= stack.Count) throw new Unsupported("영역 범위");
        return stack[stack.Count - 1 - hops].Slots[slot];
    }
    SlotInfo[] slots_of(IrName name)
    {
        if (!names.TryGetValue(name , out SlotInfo[]? slots))
        {
            names[name] = slots = Array.ConvertAll(name.Candidates , c => slot_at(c.Hops , c.Slot));
            foreach (SlotInfo s in slots) s.Name = name.Name;
        }
        return slots;
    }

    void resolve_block(IrBlock b , FuncInfo? f)
    {
        foreach (IrStmt s in b.Statements) resolve(s);
    }
    void resolve(IrStmt s)
    {
        foreach (IrExpr e in s.Arguments) resolve(e);
        switch (s.Kind)
        {
            case IrKind.Local:
                stack[^1].Slots[s.Slot].IsVar = true;
                stack[^1].Slots[s.Slot].Name = s.Name ?? "?";
                if (s.Expression is not null) resolve(s.Expression);
                break;
            case IrKind.Expression:
                resolve(s.Expression!);
                break;
            case IrKind.If:
                resolve_block(s.Body! , resolving);
                if (s.Else is not null) resolve_block(s.Else , resolving);
                break;
            case IrKind.While:
            case IrKind.Repeat:
                resolve_block(s.Body! , resolving);
                break;
            case IrKind.For:
            {
                if (s.Body is null) break;
                FrameInfo inner = frame_of(s , s.FrameSize , resolving , stack[^1]);
                stack.Add(inner);
                inner.Slots[s.Slot].IsVar = true;
                resolve_block(s.Body , resolving);
                stack.RemoveAt(stack.Count - 1);
                break;
            }
            case IrKind.Func:
            {
                if (s.Body is null) break;
                SlotInfo slot = stack[^1].Slots[s.Slot];
                slot.IsFunc = true;
                slot.FuncDefs++;
                slot.Name = s.Name ?? "?";
                FuncInfo f = new(s , stack[^1]) { Name = s.Name ?? "?" , Id = functions.Count };
                functions[s] = f;
                slot.Func = f;
                FuncInfo? outer = resolving;
                resolving = f;
                f.Frame = frame_of(s , s.FrameSize , f , stack[^1]);
                stack.Add(f.Frame);
                resolve_block(s.Body , f);
                stack.RemoveAt(stack.Count - 1);
                resolving = outer;
                break;
            }
            case IrKind.Param:
                stack[^1].Slots[s.Slot].IsVar = true;
                resolving!.Arity = Math.Max(resolving.Arity , s.ParameterIndex + 1);
                break;
        }
    }
    void resolve(IrExpr e)
    {
        switch (e)
        {
            case IrRead read:
                slots_of(read.Name);
                break;
            case IrCall call:
                slots_of(call.Name);
                foreach (IrExpr a in call.Arguments) resolve(a);
                break;
            case IrIntrinsic intrinsic:
                foreach (IrExpr a in intrinsic.Arguments) resolve(a);
                break;
            case IrAssign assign:
            {
                SlotInfo[] slots = slots_of(assign.Name);
                foreach (SlotInfo s in slots)
                {
                    s.IsVar = true;
                    //다른 함수의 칸에 쓰면, 그 칸은 흐름을 따라가지 않고 프로그램 전체의 합으로 봄
                    if (s.Frame.Owner != resolving) s.Shared = true;
                }
                if (slots.Length > 0 && assign.Create >= 0)
                {
                    SlotInfo create = slot_at(0 , assign.Create);
                    create.IsVar = true;
                    create.Name = assign.Name.Name;
                }
                resolve(assign.Value);
                break;
            }
            case IrParen paren:
                resolve(paren.Inner);
                break;
            case IrOperator op:
                foreach (IrExpr a in op.Operands) resolve(a);
                break;
            case IrConstDef d:
                stack[^1].Slots[d.Slot].IsVar = true;
                stack[^1].Slots[d.Slot].HasConst = true;
                break;
            case IrLabel l:
                stack[^1].Slots[l.Slot].IsVar = true;
                break;
        }
    }

    //─────────── 2. 흐름을 따라 타입 계산 ───────────

    //지금 검사하는 함수 인스턴스 (오류 메시지에 어떤 호출 때문인지 붙임, Crystal의 instantiation 표시)
    string? current_instance;

    //partial: 유니온의 일부 타입에서만 실패 (느슨한 단계에서는 실행할 때 확인하는 경고)
    void error(int line , string category , string message , bool partial = false)
    {
        if (current_instance is not null)
            message += $" ({current_instance}로 호출할 때)";
        var target = partial && level is TypeCheckLevel.Lenient ? checks : errors;
        if (target == checks) category = "실행 중 확인/" + category;
        var key = (line , category , message);
        if (!target.ContainsKey(key))
            target[key] = new TypeDiagnostic(line , category , message);
    }

    //영역에 들어감: 칸은 모두 정의되지 않은 상태
    static State enter(State st , FrameInfo frame)
    {
        foreach (SlotInfo s in frame.Slots) st.Map[s] = AType.Undef;
        return st;
    }
    static State leave(State st , FrameInfo frame)
    {
        foreach (SlotInfo s in frame.Slots) st.Map.Remove(s);
        return st;
    }

    void grow_summary(SlotInfo s , AType t)
    {
        AType u = s.Summary.Union(t.Without(TK.Undef | TK.Fn));
        if (!u.Equals(s.Summary))
        {
            s.Summary = u;
            changed = true;
        }
    }

    bool own(SlotInfo s , Ctx ctx) => s.Frame.Owner == ctx.Func;

    //칸 하나를 읽을 때의 (타입, 정의 여부)
    (AType Type, Def Def) look(SlotInfo s , State st , Ctx ctx)
    {
        if (own(s , ctx))
        {
            AType t = st.Get(s);
            Def d = !t.Has(TK.Undef) ? Def.Yes : (t.Kinds & ~TK.Undef) == 0 ? Def.No : Def.Maybe;
            if (s.IsFunc) return (AType.Bottom , d);
            return (s.Shared ? s.Summary : t.Without(TK.Undef) , d);
        }
        Def od = ctx.Outer.TryGetValue(s , out Def x) ? x : Def.Maybe;
        if (od is Def.No && s.Shared) od = Def.Maybe;
        return (s.IsFunc ? AType.Bottom : s.Summary , od);
    }

    State? block(IrBlock b , State? st , Ctx ctx)
    {
        IrStmt[] list = b.Statements;
        for (int round = 0 ; ; round++)
        {
            int version = ctx.LabelVersion;
            State? cur = st?.Clone();
            for (int i = 0 ; i < list.Length ; i++)
            {
                if (ctx.Labels.TryGetValue((list[i] , GotoTargetKind.Before) , out State? before)) cur = State.Join(cur , before);
                if (i > 0 && ctx.Labels.TryGetValue((list[i - 1] , GotoTargetKind.After) , out State? after)) cur = State.Join(cur , after);
                if (cur is null) continue;
                cur = statement(list[i] , cur , ctx);
            }
            if (list.Length > 0 && ctx.Labels.TryGetValue((list[^1] , GotoTargetKind.After) , out State? last)) cur = State.Join(cur , last);
            if (version == ctx.LabelVersion || round > 30)
                return cur;
        }
    }

    void add_label(Ctx ctx , (IrStmt, GotoTargetKind) key , State st)
    {
        State joined = ctx.Labels.TryGetValue(key , out State? old) ? State.Join(old , st)! : st.Clone();
        if (old is null || !joined.SameAs(old))
        {
            ctx.Labels[key] = joined;
            ctx.LabelVersion++;
        }
    }
    static void add_to(Dictionary<IrStmt , State> map , IrStmt key , State st)
    {
        map[key] = map.TryGetValue(key , out State? old) ? State.Join(old , st)! : st.Clone();
    }

    State? statement(IrStmt s , State st , Ctx ctx)
    {
        int line = s.Line;
        switch (s.Kind)
        {
            case IrKind.Empty:
                return st;
            case IrKind.Expression:
                expr(s.Expression! , ref st , ctx , line);
                return st;
            case IrKind.If:
            {
                condition(s.Arguments , ref st , ctx , line);
                var (yes , no) = split(s.Arguments , st , ctx);
                State? then = yes is null ? null : block(s.Body! , yes , ctx);
                State? other = s.Else is null ? no : no is null ? null : block(s.Else , no , ctx);
                return State.Join(then , other);
            }
            case IrKind.While:
            {
                State head = st;
                for (int i = 0 ; ; i++)
                {
                    State c = head.Clone();
                    condition(s.Arguments , ref c , ctx , line);
                    var (yes , no) = split(s.Arguments , c , ctx);
                    State? body = yes is null ? null : block(s.Body! , yes , ctx);
                    State next = State.Join(State.Join(st , body) , ctx.Continues.GetValueOrDefault(s))!;
                    if (next.SameAs(head) || i > 40)
                    {
                        return State.Join(no , ctx.Breaks.GetValueOrDefault(s));
                    }
                    head = next;
                }
            }
            case IrKind.Repeat:
            {
                foreach (IrExpr a in s.Arguments) expr(a , ref st , ctx , line);
                State head = st;
                for (int i = 0 ; ; i++)
                {
                    State? body = block(s.Body! , head , ctx);
                    State next = State.Join(State.Join(st , body) , ctx.Continues.GetValueOrDefault(s))!;
                    if (next.SameAs(head) || i > 40)
                        return State.Join(head , ctx.Breaks.GetValueOrDefault(s));
                    head = next;
                }
            }
            case IrKind.For:
            {
                AType[] args = Array.ConvertAll(s.Arguments , a => expr(a , ref st , ctx , line));
                if (args.Length is not 1 || s.Body is null)
                {
                    error(line , "구조" , "for 다음에는 변수 이름과 순회할 값 하나만 와야 합니다.");
                    return null;
                }
                AType item = AType.Bottom;
                bool iterable = args[0].Has(TK.Str) || is_coll(args[0]);
                foreach (AType one in args[0].Singles())
                {
                    if (one.Has(TK.Str)) item = item.Union(AType.Str);
                    else if (one.Has(TK.List)) item = item.Union(one.ListElem!);
                    else if (one.Has(TK.Arr)) item = item.Union(one.ArrElem!);
                    else report(line , "for" , new[] { one } , one.Has(TK.Null) ? "null은 순회할수 없습니다." : $"{one}은/는 순회할수 없습니다. (배열/리스트/문자열만 가능)" , iterable);
                }
                FrameInfo inner = frames[s];
                SlotInfo var = inner.Slots[s.Slot];
                stack.Add(inner);
                State head = st;
                State? result = null;
                for (int i = 0 ; ; i++)
                {
                    State body_in = enter(head.Clone() , inner);
                    body_in.Map[var] = item.IsBottom ? AType.Bottom : item;
                    grow_summary(var , item);
                    State? body = block(s.Body , body_in , ctx);
                    if (body is not null) leave(body , inner);
                    State? cont = ctx.Continues.GetValueOrDefault(s);
                    if (cont is not null) cont = leave(cont.Clone() , inner);
                    State next = State.Join(State.Join(st , body) , cont)!;
                    if (next.SameAs(head) || i > 40)
                    {
                        State? brk = ctx.Breaks.GetValueOrDefault(s);
                        if (brk is not null) brk = leave(brk.Clone() , inner);
                        result = State.Join(head , brk);
                        break;
                    }
                    head = next;
                }
                stack.RemoveAt(stack.Count - 1);
                return result;
            }
            case IrKind.Func:
            {
                if (s.Body is null) return st;
                st = st.Clone();
                st.Map[stack[^1].Slots[s.Slot]] = AType.FnDefined;
                return st;
            }
            case IrKind.Return:
            {
                AType first = AType.Null;
                for (int i = 0 ; i < s.Arguments.Length ; i++)
                {
                    AType t = expr(s.Arguments[i] , ref st , ctx , line);
                    if (i is 0) first = t;
                }
                ctx.Ret = ctx.Ret.Union(first);
                return null;
            }
            case IrKind.Break:
                add_to(ctx.Breaks , s.Loop! , st);
                return null;
            case IrKind.Goto:
            {
                AType[] args = Array.ConvertAll(s.Arguments , a => expr(a , ref st , ctx , line));
                if (args.Length is 0) { error(line , "인자 수" , "goto: 이동할 줄을 넣지 않았습니다."); return null; }
                if (!args[0].SubsetOf(AType.Int)) error(line , "타입" , $"goto: 이동할 줄은 int여야 합니다. ({args[0]})");
                long? constant = s.Arguments[0] is IrConst { Value: long v } ? v : null;
                foreach (IrTarget t in s.Targets)
                {
                    if (constant is long c && t.Line != c) continue;
                    switch (t.Kind)
                    {
                        case GotoTargetKind.Before:
                        case GotoTargetKind.After:
                            add_label(ctx , (t.Statement! , t.Kind) , st);
                            break;
                        case GotoTargetKind.Continue:
                            add_to(ctx.Continues , t.Statement! , st);
                            break;
                        case GotoTargetKind.Exit:
                            ctx.Ret = ctx.Ret.Union(AType.Null);
                            break;
                    }
                }
                return null;
            }
            case IrKind.Local:
            {
                SlotInfo slot = stack[^1].Slots[s.Slot];
                AType v = s.Expression is null ? AType.Null : expr(s.Expression , ref st , ctx , line);
                if (slot.HasConst) error(line , "타입" , $"'{slot.Name}'은/는 상수일수 있어서 local로 다시 정의할수 없습니다.");
                st = st.Clone();
                st.Map[slot] = v;
                grow_summary(slot , v);
                return st;
            }
            case IrKind.Param:
            {
                SlotInfo slot = stack[^1].Slots[s.Slot];
                AType v = s.ParameterIndex < ctx.Args.Length ? ctx.Args[s.ParameterIndex] : AType.Null;
                st = st.Clone();
                st.Map[slot] = v;
                grow_summary(slot , v);
                return st;
            }
            default:
                throw new Unsupported(s.Kind.ToString());
        }
    }

    void condition(IrExpr[] args , ref State st , Ctx ctx , int line)
    {
        foreach (IrExpr a in args) expr(a , ref st , ctx , line);
    }

    //─────────── 좁히기 (TypeScript의 narrowing, C#의 패턴 매칭과 같은 역할) ───────────
    //조건이 참일 때와 거짓일 때의 상태 (null이라면 그 쪽으로는 실행되지 않음)
    (State? Yes, State? No) split(IrExpr[] args , State st , Ctx ctx)
    {
        State? yes = st.Clone() , no = st.Clone();
        if (Narrowing && args.Length is 1)
            refine(args[0] , ref yes , ref no , ctx);
        return (yes , no);
    }

    void refine(IrExpr e , ref State? yes , ref State? no , Ctx ctx)
    {
        while (e is IrParen { Closed: true } p) e = p.Inner;
        State? none = null;
        switch (e)
        {
            case IrConst c:
                //항상 참/거짓인 조건 (while true 등)
                if (Parser.IsTrue(c.Value)) no = null; else yes = null;
                return;
            case IrRead or IrCall { HasArguments: false } when narrowable(name_of(e)! , yes ?? no , ctx) is SlotInfo s:
                //if x: 참이라면 x는 null이 아님
                yes = narrow(yes , s , t => t.Without(TK.Null));
                //거짓이라면 0, "", false, null 중 하나 (타입은 그대로)
                return;
            case IrIntrinsic i when program.Imports[i.Import].Name is "not" && i.Arguments.Length >= 1:
                refine(i.Arguments[0] , ref no , ref yes , ctx);
                return;
            case IrOperator { Symbol: "u!" } u:
                refine(u.Operands[0] , ref no , ref yes , ctx);
                return;
            case IrOperator { Symbol: "&&" } and_op:
                refine(and_op.Operands[0] , ref yes , ref none , ctx);
                refine(and_op.Operands[1] , ref yes , ref none , ctx);
                return;
            case IrOperator { Symbol: "||" } or_op:
                refine(or_op.Operands[0] , ref none , ref no , ctx);
                refine(or_op.Operands[1] , ref none , ref no , ctx);
                return;
        }
        //x == null, x != null, equal x null
        IrExpr[]? pair = e switch {
            IrOperator { Symbol: "==" or "!=" } op => op.Operands,
            IrIntrinsic i when program.Imports[i.Import].Name is "equal" && i.Arguments.Length is 2 => i.Arguments,
            _ => null
        };
        if (pair is null) return;
        IrExpr a = unparen(pair[0]) , b = unparen(pair[1]);
        IrName? read = is_null(a) ? name_of(b) : is_null(b) ? name_of(a) : null;
        if (read is null || narrowable(read , yes ?? no , ctx) is not SlotInfo slot) return;
        bool negated = e is IrOperator { Symbol: "!=" };
        ref State? when_null = ref negated ? ref no : ref yes;
        ref State? not_null = ref negated ? ref yes : ref no;
        when_null = narrow(when_null , slot , t => t.Has(TK.Null) ? AType.Null : AType.Bottom);
        not_null = narrow(not_null , slot , t => t.Without(TK.Null));
    }

    //변수 읽기 (키워드 인자에서는 인자 없는 호출로 만들어짐)
    static IrName? name_of(IrExpr e) => e switch {
        IrRead r => r.Name,
        IrCall { HasArguments: false } c => c.Name,
        _ => null
    };

    //null 상수 (전역 상수 null을 가리키는 이름도)
    bool is_null(IrExpr e) => e is IrConst { Value: null }
        || (name_of(e) is IrName n && n.Candidates.Length is 0 && n.Import >= 0 && program.Imports[n.Import].Name is "null");

    static IrExpr unparen(IrExpr e)
    {
        while (e is IrParen { Closed: true } p) e = p.Inner;
        return e;
    }

    //좁힐수 있는 이름: 지금 함수의 칸이고, 다른 함수가 바꾸지 않으며, 반드시 정의된 것
    SlotInfo? narrowable(IrName name , State? st , Ctx ctx)
    {
        if (st is null) return null;
        foreach (SlotInfo s in slots_of(name))
        {
            var (_ , d) = look(s , st , ctx);
            if (d is Def.No) continue;
            return d is Def.Yes && own(s , ctx) && !s.Shared && !s.IsFunc ? s : null;
        }
        return null;
    }

    //타입이 남지 않으면 그 쪽으로는 실행되지 않음
    static State? narrow(State? st , SlotInfo s , Func<AType , AType> f)
    {
        if (st is null) return null;
        AType t = f(st.Get(s));
        if (t.IsBottom) return null;
        st.Map[s] = t;
        return st;
    }

    AType expr(IrExpr e , ref State st , Ctx ctx , int line)
    {
        switch (e)
        {
            case IrConst c:
                return constant_type(c.Value);
            case IrRead read:
                return read_name(read.Name , st , ctx , line);
            case IrIntrinsic intrinsic:
            {
                AType[] args = new AType[intrinsic.Arguments.Length];
                for (int i = 0 ; i < args.Length ; i++) args[i] = expr(intrinsic.Arguments[i] , ref st , ctx , line);
                return builtin(program.Imports[intrinsic.Import].Name , args , line);
            }
            case IrCall call:
                return call_name(call , ref st , ctx , line);
            case IrAssign assign:
            {
                AType v = expr(assign.Value , ref st , ctx , line);
                assign_name(assign , v , ref st , ctx , line);
                return AType.Null;
            }
            case IrParen paren:
            {
                AType inner = expr(paren.Inner , ref st , ctx , line);
                if (!paren.Closed)
                {
                    error(line , "구조" , "괄호 '(' 다음에는 ')'로 끝나야만 합니다.");
                    return AType.Bottom;
                }
                return inner;
            }
            case IrConstDef d:
            {
                SlotInfo slot = stack[^1].Slots[d.Slot];
                AType t = constant_type(d.Value);
                st = st.Clone();
                st.Map[slot] = t;
                grow_summary(slot , t);
                return AType.Null;
            }
            case IrLabel l:
            {
                SlotInfo slot = stack[^1].Slots[l.Slot];
                st = st.Clone();
                st.Map[slot] = AType.Int;
                grow_summary(slot , AType.Int);
                return AType.Null;
            }
            case IrOperator op:
                return operator_type(op , ref st , ctx , line);
            default:
                throw new Unsupported(e.Op.ToString());
        }
    }

    //1.6.0 연산자 (Compiler/OperatorCompiler.cs와 같은 규칙, 구조체는 제한 모드에서 거부됨)
    AType operator_type(IrOperator op , ref State st , Ctx ctx , int line)
    {
        IrExpr[] o = op.Operands;
        switch (op.Symbol)
        {
            case "&&" or "||":
            {
                expr(o[0] , ref st , ctx , line);
                //&&의 오른쪽은 왼쪽이 참일 때, ||의 오른쪽은 거짓일 때만 계산 (그 상태로 좁힘)
                var (yes , no) = split(new[] { o[0] } , st , ctx);
                State? right = op.Symbol is "&&" ? yes : no;
                State? skipped = op.Symbol is "&&" ? no : yes;
                if (right is not null)
                    expr(o[1] , ref right , ctx , line);
                st = State.Join(right , skipped) ?? st;
                return AType.Bool;
            }
            case "??":
            {
                AType left = expr(o[0] , ref st , ctx , line);
                if (!left.Has(TK.Null) && !left.Has(TK.Any))
                    return left; //null일수 없으면 오른쪽은 계산하지 않음
                State right_state = st.Clone();
                AType right = expr(o[1] , ref right_state , ctx , line);
                st = State.Join(st , right_state)!;
                return left.Without(TK.Null).Union(right);
            }
            case "?:":
            {
                expr(o[0] , ref st , ctx , line);
                var (a , b) = split(new[] { o[0] } , st , ctx);
                AType t = AType.Bottom , f = AType.Bottom;
                if (a is not null) t = expr(o[1] , ref a , ctx , line);
                if (b is not null) f = expr(o[2] , ref b , ctx , line);
                st = State.Join(a , b) ?? st;
                return t.Union(f);
            }
        }
        AType[] args = new AType[o.Length];
        for (int i = 0 ; i < o.Length ; i++) args[i] = expr(o[i] , ref st , ctx , line);
        if (args.Any(x => x.IsBottom)) return AType.Bottom;
        switch (op.Symbol)
        {
            case "+": return builtin("add" , args , line);
            case "-": return builtin("sub" , args , line);
            case "*": return builtin("mul" , args , line);
            case "/": return builtin("div" , args , line);
            case "%": return builtin("mod" , args , line);
            case "&": return builtin("and" , args , line);
            case "|": return builtin("or" , args , line);
            case "==" or "!=" or "u!":
                return AType.Bool;
            case "<" or ">" or "<=" or ">=":
                //less/greater와 같이 크기를 비교할수 없는 값이면 오류 (Values.Compare)
                bool some = args[0].Singles().Any(x => args[1].Singles().Any(y => ordered(new[] { x , y }).Item2 is null));
                foreach (AType x in args[0].Singles())
                    foreach (AType y in args[1].Singles())
                        if (ordered(new[] { x , y }) is (null , string message))
                            report(line , op.Symbol , new[] { x , y } , message , some);
                return AType.Bool;
            case "^":
                return args[0].SubsetOf(AType.Int) && args[1].SubsetOf(AType.Int) ? AType.Int
                    : !args[0].Has(TK.Int) || !args[1].Has(TK.Int) ? AType.Bool : AType.Int.Union(AType.Bool);
            case "<<" or ">>":
                if (!args[0].SubsetOf(AType.Int) || !args[1].SubsetOf(AType.Int))
                    report(line , op.Symbol , args , $"'{op.Symbol}' 연산은 정수끼리만 할수 있습니다. ({args[0]}, {args[1]})" , args[0].Has(TK.Int) && args[1].Has(TK.Int));
                return AType.Int;
            case "u~":
                if (!args[0].SubsetOf(AType.Int))
                    report(line , "~" , args , $"'~' 연산은 정수에만 할수 있습니다. ({args[0]})" , args[0].Has(TK.Int));
                return AType.Int;
            case "u-" or "u+":
            {
                if (!args[0].SubsetOf(Number))
                    report(line , op.Symbol[1..] , args , $"'{op.Symbol[1..]}' 연산은 숫자에만 할수 있습니다. ({args[0]})" , num(args[0]));
                AType r = AType.Bottom;
                if (args[0].Has(TK.Int)) r = r.Union(AType.Int);
                if (args[0].Has(TK.Real)) r = r.Union(AType.Real);
                return r.IsBottom ? AType.Int : r;
            }
            default:
                throw new Unsupported("연산자 " + op.Symbol);
        }
    }

    static AType constant_type(object? v) => v switch {
        null => AType.Null,
        long => AType.Int,
        double => AType.Real,
        bool => AType.Bool,
        string => AType.Str,
        char => AType.Char,
        _ => AType.Any
    };

    //이름 읽기: 안쪽 칸부터 정의된 칸, 없으면 전역
    AType read_name(IrName name , State st , Ctx ctx , int line)
    {
        SlotInfo[] slots = slots_of(name);
        AType result = AType.Bottom;
        bool maybe = false;
        foreach (SlotInfo s in slots)
        {
            var (t , d) = look(s , st , ctx);
            if (d is Def.No) continue;
            if (s.IsFunc)
            {
                error(line , "타입" , $"'{name.Name}'은/는 함수라서 값으로 쓸수 없습니다.");
                return AType.Bottom;
            }
            result = result.Union(t);
            if (d is Def.Yes) return result;
            maybe = true;
        }
        if (name.Import >= 0)
            return result.Union(global_value(name.Import , line));
        error(line , "정의" , maybe ? $"'{name.Name}'이/가 정의되지 않았을수 있습니다." : $"'{name.Name}'은/는 알수없는 변수/함수 입니다." , partial: maybe);
        return result;
    }

    AType global_value(int import , int line)
    {
        IrImport im = program.Imports[import];
        if (im.Kind is GlobalKind.Function)
        {
            error(line , "타입" , $"'{im.Name}'은/는 함수라서 값으로 쓸수 없습니다.");
            return AType.Bottom;
        }
        if (hosts.TryGetValue(im.Name , out Signature? sig) && sig.VariableType is AType vt)
            return sig.VariablePlatform && level is TypeCheckLevel.Strict ? vt.Union(AType.Null) : vt;
        if (profile.Global.TryGetValue(im.Name , out object? holder) && holder is NativeConstantVariable constant)
            return constant_type(constant.Get());
        return AType.Any;
    }

    void assign_name(IrAssign assign , AType v , ref State st , Ctx ctx , int line)
    {
        SlotInfo[] slots = slots_of(assign.Name);
        st = st.Clone();
        //실제로 값이 들어갈수 있는 곳: 안쪽부터 정의된 칸, 없으면 전역 또는 새로 만드는 칸
        List<SlotInfo> targets = new();
        bool definite = false;
        foreach (SlotInfo s in slots)
        {
            var (_ , d) = look(s , st , ctx);
            if (d is Def.No) continue;
            if (!targets.Contains(s)) targets.Add(s);
            if (d is Def.Yes) { definite = true; break; }
        }
        bool global = false;
        if (!definite)
        {
            if (assign.Name.Import >= 0)
            {
                global = true;
                IrImport im = program.Imports[assign.Name.Import];
                if (hosts.TryGetValue(im.Name , out Signature? sig) && sig.VariableType is AType vt && !v.SubsetOf(vt))
                    error(line , "타입" , $"'{im.Name}'에는 {vt}만 넣을수 있습니다. ({v})");
            }
            else if (slots.Length is 0)
            {
                error(line , "정의" , $"'{assign.Name.Name}'에 대입할 칸이 없습니다.");
                return;
            }
            else if (assign.Create >= 0)
            {
                SlotInfo create = slot_at(0 , assign.Create);
                if (!targets.Contains(create)) targets.Add(create);
            }
        }
        //들어갈 곳이 하나뿐이면 그 칸은 대입 뒤에 반드시 v
        bool strong = targets.Count is 1 && !global;
        foreach (SlotInfo s in targets) write(s , v , st , ctx , line , strong);
    }

    void write(SlotInfo s , AType v , State st , Ctx ctx , int line , bool strong)
    {
        if (s.HasConst) error(line , "타입" , $"'{s.Name}'은/는 상수일수 있어서 값을 바꿀수 없습니다.");
        grow_summary(s , v);
        if (own(s , ctx))
            st.Map[s] = strong ? v : st.Get(s).Union(v);
    }

    AType call_name(IrCall call , ref State st , Ctx ctx , int line)
    {
        SlotInfo[] slots = slots_of(call.Name);
        //함수 칸 (직접 호출)
        SlotInfo? fslot = slots.FirstOrDefault(o => o.IsFunc);
        if (fslot is not null)
        {
            if (slots.Length > 1 || call.Name.Import >= 0) throw new Unsupported($"함수 '{call.Name.Name}'의 후보가 여러 개");
            var (_ , d) = look(fslot , st , ctx);
            if (d is not Def.Yes)
                error(line , "정의" , d is Def.No ? $"함수 '{call.Name.Name}'이/가 아직 정의되지 않았습니다." : $"함수 '{call.Name.Name}'이/가 정의되지 않았을수 있습니다." , partial: d is Def.Maybe);
            FuncInfo f = fslot.Func!;
            AType[] args = new AType[Math.Max(f.Arity , call.Arguments.Length)];
            for (int i = 0 ; i < call.Arguments.Length ; i++) args[i] = expr(call.Arguments[i] , ref st , ctx , line);
            for (int i = call.Arguments.Length ; i < args.Length ; i++) args[i] = AType.Null;
            return instance(f , args.Take(f.Arity).ToArray() , st , ctx , line);
        }
        //변수 칸 → 전역
        AType result = AType.Bottom;
        bool maybe = false;
        AType[]? evaluated = null;
        AType[] arguments(ref State s2)
        {
            if (evaluated is null)
            {
                evaluated = new AType[call.Arguments.Length];
                for (int i = 0 ; i < evaluated.Length ; i++) evaluated[i] = expr(call.Arguments[i] , ref s2 , ctx , line);
            }
            return evaluated;
        }
        foreach (SlotInfo s in slots)
        {
            var (t , d) = look(s , st , ctx);
            if (d is Def.No) continue;
            result = result.Union(value_call(t , call , arguments(ref st) , line));
            if (d is Def.Yes) return result;
            maybe = true;
        }
        if (call.Name.Import >= 0)
        {
            IrImport im = program.Imports[call.Name.Import];
            if (im.Kind is GlobalKind.Function)
                return result.Union(builtin(im.Name , arguments(ref st) , line));
            return result.Union(value_call(global_value(call.Name.Import , line) , call , arguments(ref st) , line));
        }
        error(line , "정의" , maybe ? $"'{call.Name.Name}'이/가 정의되지 않았을수 있습니다." : call.Missing , partial: maybe);
        arguments(ref st);
        return result;
    }

    //변수 이름을 함수처럼 쓴 경우: 배열/리스트 읽기/쓰기
    AType value_call(AType value , IrCall call , AType[] args , int line)
    {
        if (!call.HasArguments)
            return value;
        AType result = AType.Bottom;
        bool indexable = is_coll(value);
        foreach (AType one in value.Singles())
        {
            AType? elem = one.Has(TK.List) ? one.ListElem : one.Has(TK.Arr) ? one.ArrElem : null;
            TK clr = one.Has(TK.List) ? one.ListClr : one.ArrClr;
            if (elem is null)
            {
                report(line , call.Name.Name , new[] { one } , one.Has(TK.Any) ? $"'{call.Name.Name}'의 타입을 알수 없어서 인덱스로 쓸수 있는지 모릅니다." : $"'{call.Name.Name}'({one})은/는 배열/리스트가 아니라서 인자를 줄수 없습니다." , indexable);
                continue;
            }
            if (args.Length is 0 or > 2)
            {
                error(line , "인자 수" , "배열/리스트는 '(이름) (인덱스)' 또는 '(이름) (인덱스) (값)' 형식으로만 쓸수 있습니다.");
                continue;
            }
            if (!args[0].SubsetOf(AType.Int))
                report(line , call.Name.Name , new[] { args[0] } , $"인덱스는 int여야 합니다. ({args[0]})" , args[0].Has(TK.Int));
            if (args.Length is 1)
            {
                result = result.Union(elem);
                continue;
            }
            AType v = args[1];
            if (fits_into(one , v) is string message)
                report(line , call.Name.Name , new[] { v } , message , v.Singles().Any(x => fits_into(one , x) is null));
            result = result.Union(AType.Null);
        }
        return result;
    }

    //실제 C# 원소 타입 후보 모두에 넣을수 있는지 (Collections.TryFit)
    static bool fits(AType v , TK clr)
    {
        if ((clr & TK.Int) != 0 && !v.SubsetOf(AType.Int)) return false;
        if ((clr & TK.Real) != 0 && !v.SubsetOf(AType.Int.Union(AType.Real))) return false;
        if ((clr & TK.Str) != 0 && !v.SubsetOf(AType.Str.Union(AType.Null))) return false;
        if ((clr & TK.Bool) != 0 && !v.SubsetOf(AType.Bool)) return false;
        return true;
    }

    //함수 호출: 인자 타입 조합마다 함수 본문을 따로 검사 (단형화)
    AType instance(FuncInfo f , AType[] args , State st , Ctx caller , int line)
    {
        //함수가 보는 바깥 칸의 정의 여부 (호출하는 자리 기준)
        Dictionary<SlotInfo , Def> outer = new(ReferenceEqualityComparer.Instance);
        StringBuilder key = new();
        key.Append(f.Name).Append('(').Append(string.Join(", " , args.Select(o => o.ToString()))).Append(')');
        StringBuilder defs = new();
        for (FrameInfo? fr = f.DefFrame ; fr is not null ; fr = fr.Parent)
        {
            foreach (SlotInfo s in fr.Slots)
            {
                if (!s.IsVar && !s.IsFunc) continue;
                Def d = look(s , st , caller).Def;
                outer[s] = d;
                defs.Append(d is Def.Yes ? 'y' : d is Def.No ? 'n' : 'm');
            }
        }
        string full = key + "#" + f.Id + "#" + defs;
        string shown = key.ToString();
        f.Instances.Add(shown);
        if (memo.TryGetValue(full , out AType? done))
            return done;
        if (in_progress.Contains(full))
            return returns.GetValueOrDefault(full , AType.Bottom);
        if (++instances > MaxInstances)
            throw new Unsupported("함수 인스턴스가 너무 많습니다.");
        in_progress.Add(full);
        Ctx ctx = new() { Func = f , Args = args , Outer = outer };
        State body_in = enter(new State() , f.Frame);
        //함수 영역이 스택에서 정의 프레임 바로 위에 오도록 (이름의 hops는 정의 위치 기준)
        List<FrameInfo> saved = new(stack);
        stack.Clear();
        List<FrameInfo> chain = new();
        for (FrameInfo? fr = f.DefFrame ; fr is not null ; fr = fr.Parent) chain.Add(fr);
        chain.Reverse();
        stack.AddRange(chain);
        stack.Add(f.Frame);
        string? outer_instance = current_instance;
        current_instance = shown;
        State? end = block(f.Def.Body! , body_in , ctx);
        current_instance = outer_instance;
        stack.Clear();
        stack.AddRange(saved);
        if (end is not null) ctx.Ret = ctx.Ret.Union(AType.Null);
        in_progress.Remove(full);
        AType old = returns.GetValueOrDefault(full , AType.Bottom);
        AType ret = old.Union(ctx.Ret);
        if (!ret.Equals(old))
        {
            returns[full] = ret;
            changed = true;
        }
        returns[shown] = returns.GetValueOrDefault(shown , AType.Bottom).Union(ret);
        memo[full] = ret;
        return ret;
    }

    //─────────── 3. 내장 함수 ───────────

    //연산 실패 보고 (어떤 타입 때문인지에 따라 분류)
    void report(int line , string name , AType[] combo , string message , bool partial = false)
    {
        string category = combo.Any(o => o.Has(TK.Any)) ? "any" : combo.Any(o => o.Has(TK.Null)) ? "null" : "타입";
        error(line , category , $"{name}: {message}" , partial);
    }

    static readonly AType Number = AType.Int.Union(AType.Real);
    static readonly AType[] Concrete = { AType.Null , AType.Int , AType.Real , AType.Bool , AType.Str , AType.Char , AType.List(AType.Any , TK.Any) , AType.Arr(AType.Any , TK.Any) };

    AType builtin(string name , AType[] args , int line)
    {
        //아직 타입을 모르는 인자 (재귀 호출의 첫 단계) → 결과도 모름
        if (args.Any(o => o.IsBottom))
            return AType.Bottom;
        bool jyuno = profile.Global.TryGetValue(name , out object? holder) && holder is NativeFunction { IsJyunoBuiltin: true };
        if (!jyuno || !builtins.ContainsKey(name))
        {
            if (hosts.TryGetValue(name , out Signature? sig))
                return host_call(name , sig , args , line);
            return AType.Any; //시그니처 없는 호스트 함수: 인자는 무엇이든, 결과는 any
        }
        //list/array는 인자 전체를 같이 봐야 함
        if (name is "list" or "array")
            return collection(name , args);
        //인자의 종류 조합마다 계산
        List<AType[]> combos = new() { Array.Empty<AType>() };
        foreach (AType a in args)
        {
            IEnumerable<AType> singles = a.Singles().SelectMany(o => o.Has(TK.Any) ? Concrete : new[] { o });
            List<AType[]> next = new();
            foreach (AType[] c in combos)
                foreach (AType s in singles)
                    next.Add(c.Append(s).ToArray());
            combos = next;
            if (combos.Count > 4096)
            {
                error(line , "구조" , $"{name}: 인자 타입의 조합이 너무 많습니다.");
                return AType.Any;
            }
        }
        AType result = AType.Bottom;
        var rule = builtins[name];
        bool any_arg = args.Any(o => o.Has(TK.Any));
        var outcomes = combos.Select(combo => (combo, rule(this , combo))).ToList();
        //유니온 중 하나라도 되는 조합이 있으면 (느슨한 단계에서는) 실행 중 확인
        bool partial = outcomes.Any(o => o.Item2.Error is null);
        foreach (var (combo , (t , err)) in outcomes)
        {
            if (err is not null)
            {
                string shown = string.Join(", " , combo.Select(o => o.ToString()));
                string category = any_arg ? "any" : combo.Any(o => o.Has(TK.Null)) && err.Contains("null") ? "null" : err.StartsWith("인자") ? "인자 수" : "타입";
                error(line , category , $"{name} ({shown}): {err}" , partial);
            }
            if (t is not null) result = result.Union(t);
        }
        return result;
    }

    AType host_call(string name , Signature sig , AType[] args , int line)
    {
        foreach (var o in sig.Overloads)
        {
            if (args.Length < o.Params.Length || (o.Rest is null && args.Length != o.Params.Length)) continue;
            for (int i = 0 ; i < args.Length ; i++)
            {
                AType p = i < o.Params.Length ? o.Params[i] : o.Rest!;
                if (!args[i].SubsetOf(p))
                    report(line , name , new[] { args[i] } , $"{i + 1}번째 인자는 {p}여야 합니다. ({args[i]})" , (args[i].Kinds & p.Kinds) != 0);
            }
            return o.Platform && level is TypeCheckLevel.Strict ? o.Result.Union(AType.Null) : o.Result;
        }
        error(line , "인자 수" , $"{name}: 인자 {args.Length}개로 부를수 있는 형태가 없습니다.");
        return AType.Any;
    }

    AType collection(string name , AType[] args)
    {
        AType elem = AType.Union(args);
        TK clr = TK.None;
        if (args.Length is 0) clr = TK.Any;
        else
        {
            foreach (TK k in new[] { TK.Int , TK.Real , TK.Str , TK.Bool })
                if (args.All(a => a.Has(k))) clr |= k;
            bool same = args.All(a => a.SingleCount is 1 && a.Kinds == args[0].Kinds && (a.Kinds & (TK.Int | TK.Real | TK.Str | TK.Bool)) != 0);
            if (!same) clr |= TK.Any;
        }
        return name is "list" ? AType.List(elem , clr) : AType.Arr(elem , clr);
    }

    //결과가 null일수 있는 변환 (실용 모드에서는 null이 아니라고 보고 실행할 때 확인)
    AType maybe_null(AType t) => level is TypeCheckLevel.Strict ? t.Union(AType.Null) : t;

    delegate (AType? Type, string? Error) Rule(TypeChecker self , AType[] a);
    static (AType?, string?) ok(AType t) => (t , null);
    static (AType?, string?) fail(string message) => (null , message);
    const string NullError = "null을 처리할수 없습니다.";

    static bool num(AType t) => t.Has(TK.Int) || t.Has(TK.Real);
    static bool is_int(AType t) => t.Has(TK.Int);
    static bool is_coll(AType t) => t.Has(TK.List) || t.Has(TK.Arr);
    static AType arith(AType a , AType b) => is_int(a) && is_int(b) ? AType.Int : AType.Real;

    //컬렉션 연산 결과: 원래 컬렉션과 같은 종류, 원소 타입은 합 (실제 원소 타입은 넣을수 없으면 다시 정해짐)
    static AType like(AType first , AType elem)
    {
        TK clr = (first.Has(TK.List) ? first.ListClr : first.ArrClr) | TK.Any;
        foreach (TK k in new[] { TK.Int , TK.Real , TK.Str , TK.Bool })
            if (elem.Kinds == k) clr |= k;
        return first.Has(TK.List) ? AType.List(elem , clr) : AType.Arr(elem , clr);
    }
    static AType elem_of(AType c) => c.Has(TK.List) ? c.ListElem! : c.ArrElem!;

    static readonly Dictionary<string , Rule> builtins = new(StringComparer.Ordinal) {
        ["int"] = (self , a) => a.Length switch {
            0 => ok(AType.Int),
            1 => a[0].Has(TK.Null) ? fail(NullError)
                : a[0].Has(TK.Int) || a[0].Has(TK.Bool) ? ok(AType.Int)
                : ok(self.maybe_null(AType.Int)),
            _ => ok(AType.Null)
        },
        ["double"] = (self , a) => a.Length switch {
            0 => ok(AType.Real),
            1 => a[0].Has(TK.Null) ? fail(NullError)
                : num(a[0]) || a[0].Has(TK.Bool) ? ok(AType.Real)
                : ok(self.maybe_null(AType.Real)),
            _ => ok(AType.Null)
        },
        ["string"] = (self , a) => a.Length is 0 ? ok(AType.Str) : a[0].Has(TK.Null) ? fail(NullError) : ok(AType.Str),
        ["array"] = (self , a) => ok(AType.Bottom),
        ["list"] = (self , a) => ok(AType.Bottom),
        ["length"] = (self , a) => a.Length is 0 ? fail("인자: 길이를 구할 값이 필요합니다.")
            : a[0].Has(TK.Null) ? fail(NullError)
            : a[0].Has(TK.Str) || is_coll(a[0]) ? ok(AType.Int)
            : fail($"{a[0]}은/는 길이를 구할수 없습니다."),
        ["add"] = (self , a) => {
            if (a.Length is 0) return fail("인자: 덧셈을 할 값들을 넣지 않았습니다.");
            if (is_coll(a[0]))
            {
                AType elem = elem_of(a[0]);
                for (int i = 1 ; i < a.Length ; i++) elem = elem.Union(is_coll(a[i]) ? elem_of(a[i]) : a[i]);
                return ok(like(a[0] , elem));
            }
            if (a.Length is 1) return ok(a[0]);
            if (a.Any(o => o.Has(TK.Str))) return ok(AType.Str);
            AType sum = a[0];
            for (int i = 1 ; i < a.Length ; i++)
            {
                if (num(sum) && num(a[i])) sum = arith(sum , a[i]);
                else return fail(a.Any(o => o.Has(TK.Null)) ? NullError : $"{sum}와/과 {a[i]}은/는 더할수 없습니다.");
            }
            return ok(sum);
        },
        ["sub"] = (self , a) => {
            if (a.Length < 2) return fail("인자: 뺄셈 할 값 2개가 필요합니다.");
            if (is_coll(a[0])) return ok(like(a[0] , elem_of(a[0])));
            if (a[0].Has(TK.Null) || a[1].Has(TK.Null)) return fail(NullError);
            if (a[0].Has(TK.Str))
                return a[1].Has(TK.Str) ? ok(AType.Str) : fail($"문자열에서는 문자열만 뺄수 있습니다. ({a[1]})");
            if (num(a[0]) && num(a[1])) return ok(arith(a[0] , a[1]));
            return fail("숫자 또는 문자열 이외에는 뺄셈 연산을 할수 없습니다.");
        },
        ["mul"] = (self , a) => {
            if (a.Length is 0) return ok(AType.Int);
            if (a[0].Has(TK.Str))
            {
                if (a.Length is 1) return ok(AType.Str);
                return a[1].Has(TK.Null) ? fail(NullError) : is_int(a[1]) ? ok(AType.Str) : fail("문자열을 반복할 횟수는 정수여야 합니다.");
            }
            AType mul = AType.Int;
            foreach (AType x in a)
            {
                if (x.Has(TK.Null)) return fail(NullError);
                if (!num(x)) return fail($"{mul}와/과 {x}은/는 곱할수 없습니다.");
                mul = arith(mul , x);
            }
            return ok(mul);
        },
        ["div"] = (self , a) => {
            if (a.Length < 2) return fail("인자: 나눗셈 할 값 2개가 필요합니다.");
            if (a[0].Has(TK.Str)) return a[1].Has(TK.Null) ? fail(NullError) : ok(AType.Arr(AType.Str , TK.Str));
            if (a[0].Has(TK.Null) || a[1].Has(TK.Null)) return fail(NullError);
            if (num(a[0]) && num(a[1])) return ok(arith(a[0] , a[1]));
            return fail($"{a[0]}을/를 {a[1]}(으)로 나눌수 없습니다.");
        },
        ["mod"] = (self , a) => {
            if (a.Length < 2) return fail("인자: 모듈로 연산 할 값 2개가 필요합니다.");
            if (a[0].Has(TK.Null) || a[1].Has(TK.Null)) return fail(NullError);
            if (num(a[0]) && num(a[1])) return ok(arith(a[0] , a[1]));
            return fail($"{a[0]}와/과 {a[1]}은/는 나머지 연산을 할수 없습니다.");
        },
        ["equal"] = (self , a) => a.Length < 2 ? fail("인자: 비교할 대상이 최소 2개 이상 있어야 합니다.") : ok(AType.Bool),
        ["less"] = (self , a) => ordered(a),
        ["greater"] = (self , a) => ordered(a),
        ["and"] = (self , a) => a.Length is 0 ? fail("인자: and 연산을 할 값이 필요합니다.") : ok(a.All(is_int) ? AType.Int : AType.Bool),
        ["or"] = (self , a) => a.Length is 0 ? fail("인자: or 연산을 할 값이 필요합니다.") : ok(a.All(is_int) ? AType.Int : AType.Bool),
        ["not"] = (self , a) => a.Length is 0 ? fail("인자: not 연산을 할 값이 필요합니다.") : ok(is_int(a[0]) ? AType.Int : AType.Bool),
        ["bool"] = (self , a) => ok(AType.Bool),
        //Jyuno 내장 콘솔 (JyunoSdk)
        ["console.write"] = (self , a) => a.Length < 2 || a[0].Has(TK.Str) || a[0].Has(TK.Null) ? ok(AType.Null) : fail("형식 문자열은 문자열이어야 합니다."),
        ["console.writeline"] = (self , a) => a.Length < 2 ? ok(AType.Null) : a[0].Has(TK.Null) ? fail(NullError) : a[0].Has(TK.Str) ? ok(AType.Null) : fail("형식 문자열은 문자열이어야 합니다."),
        ["console.readline"] = (self , a) => ok(self.maybe_null(AType.Str)),
        ["console.read"] = (self , a) => ok(AType.Char),
        ["console.clear"] = (self , a) => ok(AType.Null),
        //수학
        ["math.sin"] = (self , a) => real(a , 1),
        ["math.cos"] = (self , a) => real(a , 1),
        ["math.tan"] = (self , a) => real(a , 1),
        ["math.log2"] = (self , a) => real(a , 1),
        ["math.log"] = (self , a) => real(a , a.Length >= 2 ? 2 : 1),
        ["math.pow"] = (self , a) => real(a , 2),
        ["math.abs"] = (self , a) => a.Length is 0 ? fail("인자: 절댓값을 구할 값이 필요합니다.") : a[0].Has(TK.Null) ? fail(NullError)
            : a[0].Has(TK.Int) ? ok(AType.Int) : a[0].Has(TK.Real) ? ok(AType.Real) : fail($"{a[0]}의 절댓값을 구할수 없습니다."),
        //파일 (JyunoSdk --file)
        ["file.exist"] = (self , a) => str_arg(a , AType.Bool),
        ["file.read"] = (self , a) => str_arg(a , self.maybe_null(AType.Str)),
        ["directory.now"] = (self , a) => ok(AType.Str),
        ["directory.exist"] = (self , a) => str_arg(a , AType.Bool),
        ["file.write"] = (self , a) => a.Length < 2 ? fail("인자: 파일의 경로, 그리고 내용을 입력해야합니다.") : a[0].Has(TK.Str) && a[1].Has(TK.Str) ? ok(AType.Null) : fail("경로와 내용은 문자열이어야 합니다."),
        ["directory.files"] = (self , a) => a.Length > 0 && a[0].Has(TK.Null) ? fail(NullError) : ok(AType.Arr(AType.Str , TK.Str).Union(AType.Bool)),
        ["directory.list"] = (self , a) => a.Length > 0 && a[0].Has(TK.Null) ? fail(NullError) : ok(AType.Arr(AType.Str , TK.Str).Union(AType.Bool)),
        ["directory.move"] = (self , a) => a.Length >= 2 && a[0].Has(TK.Str) && a[1].Has(TK.Str) ? ok(AType.Bool) : fail("기존 경로, 그리고 이동할 새 경로를 입력하지 않았습니다."),
        ["file.remove"] = (self , a) => str_arg(a , AType.Bool),
        //난수: 정수 인자라면 정수, 그 외에는 실수
        ["math.random"] = (self , a) => random(a),
        ["math.csprng"] = (self , a) => random(a),
    };

    static (AType?, string?) random(AType[] a)
    {
        if (a.Length is 0) return ok(AType.Real);
        int n = Math.Min(a.Length , 2);
        for (int i = 0 ; i < n ; i++)
        {
            if (a[i].Has(TK.Null)) return fail(NullError);
            if (!num(a[i])) return fail($"{a[i]}은/는 숫자가 아닙니다.");
        }
        return ok(a.Take(n).All(is_int) ? AType.Int : AType.Real);
    }

    //컬렉션 함수 (array.*, list.*, Language/Collections.cs)와 문자열 함수 (string.*, Language/Strings.cs)
    static TypeChecker()
    {
        foreach (string kind in new[] { "array" , "list" })
        {
            bool is_list = kind is "list";
            void coll(string name , Func<AType , AType[] , (AType?, string?)> rule , bool only_list = false) =>
                builtins[$"{kind}.{name}"] = (self , a) => {
                    if (a.Length is 0) return fail("인자: 배열/리스트가 필요합니다.");
                    if (a[0].Has(TK.Null)) return fail(NullError);
                    if (!is_coll(a[0]) || (only_list && !a[0].Has(TK.List))) return fail($"{a[0]}은/는 {(only_list ? "리스트" : "배열/리스트")}가 아닙니다.");
                    return rule(a[0] , a);
                };
            //원래 컬렉션 그대로 (또는 같은 원소 타입의 새 컬렉션)
            foreach (string name in new[] { "sort" , "reverse" , "shuffle" , "copy" , "slice" , "distinct" })
                coll(name , (c , a) => ok(c));
            coll("fill" , (c , a) => a.Length < 2 ? ok(c) : fits_into(c , a[1]) is string e ? fail(e) : ok(c));
            coll("contains" , (c , a) => ok(AType.Bool));
            foreach (string name in new[] { "indexof" , "lastindexof" , "count" , "binary_search" })
                coll(name , (c , a) => ok(AType.Int));
            coll("join" , (c , a) => ok(AType.Str));
            coll("min" , (c , a) => ok(elem_of(c)));
            coll("max" , (c , a) => ok(elem_of(c)));
            coll("sum" , (c , a) => elem_of(c).SubsetOf(Number) ? ok(elem_of(c).Has(TK.Real) ? AType.Real.Union(elem_of(c).Has(TK.Int) ? AType.Int : AType.Bottom) : AType.Int) : fail($"{elem_of(c)}은/는 숫자가 아닙니다."));
            builtins[$"{kind}.range"] = (self , a) => a.Length is 0 ? fail("인자: 범위가 필요합니다.") : a.Take(3).All(o => o.SubsetOf(AType.Int)) ? ok(is_list ? AType.List(AType.Int , TK.Int) : AType.Arr(AType.Int , TK.Int)) : fail("범위는 정수여야 합니다.");
            builtins[$"{kind}.new"] = (self , a) => a.Length is 0 ? fail("인자: 길이가 필요합니다.") : !a[0].SubsetOf(AType.Int) ? fail($"길이는 정수여야 합니다. ({a[0]})")
                : ok(is_list ? AType.List(a.Length > 1 ? a[1] : AType.Null , TK.Any) : AType.Arr(a.Length > 1 ? a[1] : AType.Null , TK.Any));
            if (!is_list) continue;
            coll("push" , (c , a) => a.Skip(1).Select(v => fits_into(c , v)).FirstOrDefault(e => e is not null) is string e ? fail(e) : ok(c) , true);
            coll("pop" , (c , a) => ok(elem_of(c)) , true);
            coll("insert" , (c , a) => a.Length < 3 ? fail("인자: 리스트, 인덱스, 값이 필요합니다.") : !a[1].SubsetOf(AType.Int) ? fail($"인덱스는 int여야 합니다. ({a[1]})") : fits_into(c , a[2]) is string e ? fail(e) : ok(c) , true);
            coll("removeat" , (c , a) => a.Length < 2 ? fail("인자: 제거할 인덱스가 필요합니다.") : !a[1].SubsetOf(AType.Int) ? fail($"인덱스는 int여야 합니다. ({a[1]})") : ok(elem_of(c)) , true);
            coll("remove" , (c , a) => ok(AType.Bool) , true);
            coll("clear" , (c , a) => ok(c) , true);
        }
        //문자열 함수: 첫 인자가 문자열이어야 하는 것들
        void text(string name , AType result) =>
            builtins["string." + name] = (self , a) => a.Length is 0 ? fail("인자: 문자열이 필요합니다.") : a[0].Has(TK.Null) ? fail(NullError) : a[0].Has(TK.Str) ? ok(result) : fail($"{a[0]}은/는 문자열이 아닙니다.");
        foreach (string name in new[] { "replace" , "substring" , "insert" , "remove" , "toupper" , "tolower" , "trim" , "trimstart" , "trimend" , "padleft" , "padright" })
            text(name , AType.Str);
        foreach (string name in new[] { "indexof" , "lastindexof" , "charcode" , "compare" })
            text(name , AType.Int);
        foreach (string name in new[] { "contains" , "startswith" , "endswith" , "equals" })
            text(name , AType.Bool);
        text("split" , AType.Arr(AType.Str , TK.Str));
        text("tochararray" , AType.Arr(AType.Str , TK.Str));
        text("format" , AType.Str);
        builtins["string.concat"] = (self , a) => ok(AType.Str);
        builtins["string.join"] = (self , a) => ok(AType.Str);
        builtins["string.isnullorempty"] = (self , a) => ok(AType.Bool);
        builtins["string.isnullorwhitespace"] = (self , a) => ok(AType.Bool);
        builtins["string.fromcharcode"] = (self , a) => a.All(o => o.SubsetOf(AType.Int)) ? ok(AType.Str) : fail("문자 코드는 정수여야 합니다.");
    }

    //컬렉션 c에 값 v를 넣을수 있는지 (넣을수 없다면 오류 메시지)
    static string? fits_into(AType c , AType v)
    {
        AType elem = elem_of(c);
        TK clr = c.Has(TK.List) ? c.ListClr : c.ArrClr;
        AType stored = stored_as(v , elem , clr);
        if (!stored.SubsetOf(elem)) return $"{c}에는 {elem}만 넣을수 있습니다. ({v})";
        if (!fits(v , clr)) return $"{c}은/는 실행할 때 원소 타입이 정해진 컬렉션일수 있어서 {v}을/를 넣을수 없을수 있습니다.";
        return null;
    }
    //실수 컬렉션(double[])에 넣은 정수는 실수로 바뀜
    static AType stored_as(AType v , AType elem , TK clr) =>
        v.Has(TK.Int) && clr == TK.Real && elem.Has(TK.Real) && !elem.Has(TK.Int) ? v.Without(TK.Int).Union(AType.Real) : v;

    static (AType?, string?) ordered(AType[] a)
    {
        if (a.Length is 0) return fail("인자: 비교를 할 값이 필요합니다.");
        for (int i = 1 ; i < a.Length ; i++)
        {
            AType x = a[i - 1] , y = a[i];
            bool fine = (num(x) && num(y)) || (x.Has(TK.Str) && y.Has(TK.Str)) || (x.Has(TK.Bool) && y.Has(TK.Bool)) || (x.Has(TK.Char) && y.Has(TK.Char));
            if (!fine) return fail(x.Has(TK.Null) || y.Has(TK.Null) ? $"null은 크기를 비교할수 없습니다." : $"{x}와/과 {y}은/는 크기를 비교할수 없습니다.");
        }
        return ok(AType.Bool);
    }
    static (AType?, string?) real(AType[] a , int count)
    {
        if (a.Length < count) return fail("인자: 인자가 누락되었습니다.");
        for (int i = 0 ; i < count ; i++)
        {
            if (a[i].Has(TK.Null)) return fail(NullError);
            if (!num(a[i])) return fail($"{a[i]}은/는 숫자가 아닙니다.");
        }
        return ok(AType.Real);
    }
    static (AType?, string?) str_arg(AType[] a , AType result)
    {
        if (a.Length is 0) return fail("인자: 경로를 입력해주세요.");
        if (a[0].Has(TK.Null)) return fail(NullError);
        return a[0].Has(TK.Str) ? ok(result) : fail("경로는 문자열이어야 합니다.");
    }
}
