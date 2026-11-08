using Jyuno.Analysis;
using Jyuno.Language;
using System.Buffers.Binary;
using System.Text;

namespace Jyuno.Compiler;

//[프로토타입] 제한 모드 프로그램 바이너리 (.jyc v2)
//
//  헤더 32바이트: 매직(8) 포맷 버전(u16) 플래그(u16, 0) 언어 버전(u32) 본문 길이(u32) 예약(u32) 체크섬(8)
//  본문: 문자열 표 → 줄 수, 줄마다 속한 함수 → 최상위 칸 수 → 가져오기 표 → 스크립트 이름 → 문장 트리
//
//불러올 때 검증 (모두 통과해야 실행):
//  1. 형식: 모든 길이/번호의 범위, 가장 짧은 varint, 엄격한 UTF-8, 중첩 깊이 제한
//  2. 구조: 칸 번호는 그 영역의 크기 안, param은 함수 안, break는 감싸는 반복문, goto 대상은 감싸는 블록/반복문,
//     내장 함수 호출은 그 내장 함수를 가져온 가져오기 표 번호
//  3. 연결(link): 가져오기 표의 모든 이름을 호스트 런타임이 같은 종류로 제공하는지, 내장 함수로 가정한 이름은 Jyuno 내장 함수인지,
//     스크립트 이름이 호스트 전역과 겹치지 않는지
public static class JyunoProgramImage
{
    public static readonly byte[] Magic = { 0x89 , (byte)'J' , (byte)'Y' , (byte)'S' , 0x0D , 0x0A , 0x1A , 0x0A };
    public const ushort FormatVersion = 2;
    public const uint LanguageVersion = 0x0001_0400;
    const int HeaderSize = 32;
    //문장/식 중첩 깊이 제한 (재귀로 만들고 실행하므로 스택 오버플로를 막음)
    public const int MaxDepth = 512;

    /// <summary>제한 모드로 빌드합니다. profile: 실행할 호스트가 제공할 전역(명령어 묶음, 호스트 함수)을 가진 런타임</summary>
    public static byte[] Build(IReadOnlyList<string> lines , Runtime profile) => Write(IrBuilder.Build(lines , profile));

    /// <summary>검증하고 호스트와 연결해서 실행할수 있는 프로그램을 만듭니다.</summary>
    public static JyunoProgram Load(ReadOnlySpan<byte> data , Runtime host) => Link(Read(data) , host);

    /// <summary>(측정/테스트용) 바이너리 없이 바로 빌드하고 연결합니다.</summary>
    public static JyunoProgram Compile(IReadOnlyList<string> lines , Runtime host) => Link(IrBuilder.Build(lines , host) , host);

    /// <summary>바이너리가 필요로 하는 전역 이름과 종류 (호스트가 실행 전에 확인할수 있음)</summary>
    public static IReadOnlyList<(string Name, GlobalKind Kind)> Requirements(ReadOnlySpan<byte> data) => Read(data).Imports.Select(o => (o.Name , o.Kind)).ToArray();

    #region 연결 (호스트 권한 대조)

    internal static JyunoProgram Link(IrProgram program , Runtime host)
    {
        List<string> errors = new();
        object?[] bound = new object?[program.Imports.Length];
        lock (host.Global)
        {
            for (int i = 0 ; i < program.Imports.Length ; i++)
            {
                IrImport import = program.Imports[i];
                if (!host.Global.TryGetValue(import.Name , out object? holder))
                {
                    errors.Add($"'{import.Name}'({kind_name(import.Kind)})이/가 필요하지만 호스트가 제공하지 않습니다.");
                    continue;
                }
                GlobalKind actual = holder switch {
                    NativeConstantVariable => GlobalKind.Constant,
                    VariableInterface => GlobalKind.Variable,
                    _ => GlobalKind.Function
                };
                if (actual != import.Kind)
                {
                    errors.Add($"'{import.Name}'은/는 {kind_name(import.Kind)}여야 하지만 호스트에서는 {kind_name(actual)}입니다.");
                    continue;
                }
                //빌드할 때 Jyuno 내장 함수로 가정하고 바로 계산/미리 계산한 이름은, 호스트의 것도 같은 내장 함수여야 함
                //(Intrinsic, Foldable은 Jyuno 안에서만 붙일수 있으므로 호스트가 흉내낼수 없음)
                if (import.Intrinsic is not Intrinsic.None && (holder is not NativeFunction native || native.Intrinsic != import.Intrinsic))
                {
                    errors.Add($"'{import.Name}'은/는 Jyuno 내장 함수여야 합니다.");
                    continue;
                }
                if (import.Pure && holder is not NativeFunction { Foldable: not null })
                {
                    errors.Add($"'{import.Name}'은/는 Jyuno 내장 함수여야 합니다.");
                    continue;
                }
                bound[i] = holder;
            }
            //빌드할 때 스크립트의 이름으로 정한 이름이 호스트의 전역이라면, 인터프리터와 뜻이 달라지므로 거부
            HashSet<string> imported = new(program.Imports.Select(o => o.Name));
            foreach (string name in program.ScriptNames)
            {
                if (!imported.Contains(name) && host.Global.ContainsKey(name))
                    errors.Add($"스크립트의 이름 '{name}'이/가 호스트의 전역 이름과 겹칩니다. 이 호스트에 맞춰 다시 빌드해야 합니다.");
            }
        }
        if (errors.Count > 0)
            throw new JyunoLinkException(errors);
        return new JyunoProgram(new Interpreter.StaticEngine(program , bound).Compile());
    }

    static string kind_name(GlobalKind kind) => kind switch {
        GlobalKind.Function => "함수",
        GlobalKind.Variable => "변수",
        _ => "상수"
    };

    #endregion

    #region 쓰기

    sealed class StringTable
    {
        public readonly Dictionary<string , int> Index = new(StringComparer.Ordinal);
        public readonly List<string> List = new();
        public int this[string text]
        {
            get
            {
                if (!Index.TryGetValue(text , out int i))
                {
                    Index[text] = i = List.Count;
                    List.Add(text);
                }
                return i;
            }
        }
    }

    internal static byte[] Write(IrProgram program)
    {
        StringTable strings = new();
        Writer body = new();
        void zigzag(long v) => body.VarULong((ulong)((v << 1) ^ (v >> 63)));
        void constant(object? value)
        {
            switch (value)
            {
                case null: body.Byte(0); break;
                case long l: body.Byte(1); zigzag(l); break;
                case double d: body.Byte(2); body.Double(d); break;
                case string s: body.Byte(3); body.VarUInt((uint)strings[s]); break;
                case bool b: body.Byte(b ? (byte)4 : (byte)5); break;
                default: throw new JyunoException($"저장할수 없는 상수입니다: {value}");
            }
        }
        void name(IrName n)
        {
            body.VarUInt((uint)strings[n.Name]);
            body.VarUInt((uint)n.Candidates.Length);
            foreach (var (hops , slot) in n.Candidates)
            {
                body.VarUInt((uint)hops);
                body.VarUInt((uint)slot);
            }
            body.VarUInt((uint)(n.Import + 1));
        }
        void expressions(IrExpr[] list)
        {
            body.VarUInt((uint)list.Length);
            foreach (IrExpr e in list)
                expression(e);
        }
        void expression(IrExpr e)
        {
            body.Byte((byte)e.Op);
            switch (e)
            {
                case IrConst c: constant(c.Value); break;
                case IrRead r: name(r.Name); break;
                case IrCall call:
                    name(call.Name);
                    body.Byte((byte)((call.HasArguments ? 1 : 0) | (call.Closed ? 2 : 0)));
                    expressions(call.Arguments);
                    break;
                case IrIntrinsic intrinsic:
                    body.VarUInt((uint)intrinsic.Import);
                    expressions(intrinsic.Arguments);
                    break;
                case IrAssign assign:
                    name(assign.Name);
                    body.VarUInt((uint)assign.Create);
                    expression(assign.Value);
                    break;
                case IrConstDef definition:
                    body.VarUInt((uint)definition.Slot);
                    constant(definition.Value);
                    break;
                case IrLabel label:
                    body.VarUInt((uint)label.Slot);
                    break;
                case IrParen paren:
                    expression(paren.Inner);
                    break;
            }
        }
        void block(IrBlock b)
        {
            body.VarUInt((uint)b.Statements.Length);
            foreach (IrStmt s in b.Statements)
                statement(s);
        }
        void statement(IrStmt s)
        {
            body.Byte((byte)s.Kind);
            body.VarUInt((uint)s.Line);
            expressions(s.Arguments);
            switch (s.Kind)
            {
                case IrKind.Expression:
                    expression(s.Expression!);
                    break;
                case IrKind.If:
                    block(s.Body!);
                    body.Byte(s.Else is null ? (byte)0 : (byte)1);
                    if (s.Else is not null)
                        block(s.Else);
                    break;
                case IrKind.While:
                case IrKind.Repeat:
                    block(s.Body!);
                    break;
                case IrKind.For:
                case IrKind.Func:
                    body.VarUInt((uint)s.FrameSize);
                    body.VarUInt((uint)s.Slot);
                    block(s.Body!);
                    break;
                case IrKind.Goto:
                    body.VarUInt((uint)s.Targets.Length);
                    foreach (IrTarget t in s.Targets)
                    {
                        zigzag(t.Line);
                        body.Byte((byte)t.Kind);
                        body.VarUInt((uint)(t.Statement is null ? 0 : t.Statement.Id + 1));
                    }
                    break;
                case IrKind.Break:
                    body.VarUInt((uint)s.Loop!.Id);
                    break;
                case IrKind.Param:
                    body.VarUInt((uint)s.Slot);
                    body.VarUInt((uint)s.ParameterIndex);
                    break;
                case IrKind.Local:
                    body.VarUInt((uint)s.Slot);
                    body.VarUInt((uint)strings[s.Name!]);
                    body.Byte(s.Expression is null ? (byte)0 : (byte)1);
                    if (s.Expression is not null)
                        expression(s.Expression);
                    break;
            }
        }

        //본문을 먼저 만들고 (문자열 표를 채우기 위해) 문자열 표를 앞에 붙임
        body.VarUInt((uint)program.LineCount);
        foreach (int owner in program.LineOwner)
            body.VarUInt((uint)(owner + 1));
        body.VarUInt((uint)program.TopFrameSize);
        body.VarUInt((uint)program.Imports.Length);
        foreach (IrImport import in program.Imports)
        {
            body.VarUInt((uint)strings[import.Name]);
            body.Byte((byte)import.Kind);
            body.Byte((byte)import.Intrinsic);
            body.Byte(import.Pure ? (byte)1 : (byte)0);
        }
        body.VarUInt((uint)program.ScriptNames.Length);
        foreach (string n in program.ScriptNames)
            body.VarUInt((uint)strings[n]);
        block(program.Root);
        byte[] tree = body.ToArray();

        Writer payload = new();
        payload.VarUInt((uint)strings.List.Count);
        foreach (string text in strings.List)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            payload.VarUInt((uint)bytes.Length);
            payload.Bytes(bytes);
        }
        payload.Bytes(tree);
        byte[] content = payload.ToArray();

        Writer file = new();
        file.Bytes(Magic);
        file.UInt16(FormatVersion);
        file.UInt16(0);
        file.UInt32(LanguageVersion);
        file.UInt32((uint)content.Length);
        file.UInt32(0);
        file.UInt64(Checksum(file.ToArray() , content));
        file.Bytes(content);
        return file.ToArray();
    }

    #endregion

    #region 읽기 (검증)

    //읽는 중인 영역 사슬과 감싸는 문장들
    sealed class Context
    {
        public readonly List<int> Frames = new(); //[0] = 지금 영역의 칸 수
        public bool InFunction;
        public readonly List<IrStmt> Loops = new(); //지금 함수 안에서 감싸는 반복문
        public readonly List<IrBlock> Blocks = new(); //지금 함수 안에서 감싸는 블록
        public int Depth;
    }

    sealed class Pending
    {
        public Pending(IrStmt statement , List<IrBlock> blocks , List<IrStmt> loops , (long, GotoTargetKind, int)[] targets , int loop_id)
        {
            Statement = statement;
            Blocks = blocks;
            Loops = loops;
            Targets = targets;
            LoopId = loop_id;
        }
        public readonly IrStmt Statement;
        public readonly List<IrBlock> Blocks;
        public readonly List<IrStmt> Loops;
        public readonly (long Line, GotoTargetKind Kind, int Id)[] Targets;
        public readonly int LoopId;
    }

    internal static IrProgram Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..8].SequenceEqual(Magic))
            throw new JyunoProgramException("Jyuno 프로그램 바이너리가 아닙니다.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) != FormatVersion)
            throw new JyunoProgramException("지원하지 않는 포맷 버전입니다.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[10..]) != 0)
            throw new JyunoProgramException("알수 없는 플래그가 있습니다.");
        //제한 모드의 의미(내장 함수, 토큰 규칙)는 언어 버전마다 다를수 있으므로 같은 버전만
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != LanguageVersion)
            throw new JyunoProgramException("다른 Jyuno 버전으로 빌드한 파일입니다. 다시 빌드하세요.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[20..]) != 0)
            throw new JyunoProgramException("예약된 헤더 값이 0이 아닙니다.");
        if (length != data.Length - HeaderSize)
            throw new JyunoProgramException("파일 길이가 맞지 않습니다.");
        ReadOnlySpan<byte> payload = data[HeaderSize..];
        if (Checksum(data , payload) != BinaryPrimitives.ReadUInt64LittleEndian(data[24..]))
            throw new JyunoProgramException("파일이 손상되었습니다. (체크섬 불일치)");

        Reader r = new(payload.ToArray());
        int payload_length = payload.Length;
        //개수는 남은 바이트 수를 넘을수 없음 (원소마다 1바이트 이상)
        int count() => r.Count(payload_length - r.Position);
        string[] strings = new string[count()];
        for (int i = 0 ; i < strings.Length ; i++)
            strings[i] = r.Utf8(count());

        IrProgram program = new();
        program.LineCount = count();
        program.LineOwner = new int[program.LineCount];
        for (int i = 0 ; i < program.LineCount ; i++)
        {
            int owner = r.Count(program.LineCount) - 1;
            program.LineOwner[i] = owner;
        }
        program.TopFrameSize = count();
        IrImport[] imports = new IrImport[count()];
        for (int i = 0 ; i < imports.Length ; i++)
        {
            string name = strings[r.Index(strings.Length)];
            byte kind = r.Byte(), intrinsic = r.Byte(), pure = r.Byte();
            if (kind > (byte)GlobalKind.Constant || intrinsic > (byte)Intrinsic.Not || pure > 1)
                throw new JyunoProgramException("가져오기 표가 올바르지 않습니다.");
            imports[i] = new IrImport(name , (GlobalKind)kind , (Intrinsic)intrinsic , pure is 1);
        }
        if (imports.Select(o => o.Name).Distinct().Count() != imports.Length)
            throw new JyunoProgramException("가져오기 표에 같은 이름이 있습니다.");
        program.Imports = imports;
        program.ScriptNames = new string[count()];
        for (int i = 0 ; i < program.ScriptNames.Length ; i++)
        {
            program.ScriptNames[i] = strings[r.Index(strings.Length)];
            if (i > 0 && string.CompareOrdinal(program.ScriptNames[i - 1] , program.ScriptNames[i]) >= 0)
                throw new JyunoProgramException("스크립트 이름 목록이 정렬되어 있지 않습니다.");
        }

        List<IrStmt> all = new();
        List<Pending> pending = new();
        Context context = new();
        context.Frames.Add(program.TopFrameSize);

        JyunoProgramException bad(string message) => new(message);
        void enter()
        {
            if (++context.Depth > MaxDepth)
                throw bad("중첩이 너무 깊습니다.");
        }
        int slot(int frame)
        {
            ulong v = r.VarULong();
            return v < (ulong)frame ? (int)v : throw bad("칸 번호가 영역의 크기를 벗어납니다.");
        }
        object? constant()
        {
            switch (r.Byte())
            {
                case 0: return null;
                case 1: { ulong z = r.VarULong(); return (long)(z >> 1) ^ -(long)(z & 1); }
                case 2: return r.Double();
                case 3: return strings[r.Index(strings.Length)];
                case 4: return true;
                case 5: return false;
                default: throw bad("알수 없는 상수 종류입니다.");
            }
        }
        IrName read_name()
        {
            string text = strings[r.Index(strings.Length)];
            var candidates = new (int, int)[r.Count(context.Frames.Count)];
            int last = -1;
            for (int i = 0 ; i < candidates.Length ; i++)
            {
                int hops = r.Index(context.Frames.Count);
                if (hops <= last)
                    throw bad("이름의 영역 후보 순서가 올바르지 않습니다.");
                last = hops;
                candidates[i] = (hops , slot(context.Frames[hops]));
            }
            int import = r.Index(imports.Length + 1) - 1;
            if (import >= 0 && imports[import].Name != text)
                throw bad("가져오기 표의 이름과 맞지 않습니다.");
            return new IrName(text , candidates , import);
        }
        IrExpr[] expressions(int line)
        {
            IrExpr[] list = new IrExpr[r.Count(payload_length - r.Position)];
            for (int i = 0 ; i < list.Length ; i++)
                list[i] = expression(line);
            return list;
        }
        IrExpr expression(int line)
        {
            enter();
            IrExpr result;
            byte op = r.Byte();
            switch ((IrOp)op)
            {
                case IrOp.Const: result = new IrConst(constant()); break;
                case IrOp.Read: result = new IrRead(read_name()); break;
                case IrOp.Call:
                {
                    IrName n = read_name();
                    byte flags = r.Byte();
                    if (flags > 3)
                        throw bad("호출 형식이 올바르지 않습니다.");
                    result = new IrCall(n , expressions(line) , (flags & 1) != 0 , (flags & 2) != 0);
                    break;
                }
                case IrOp.Intrinsic:
                {
                    int import = r.Index(imports.Length);
                    IrExpr[] args = expressions(line);
                    if (!Interpreter.SupportsIntrinsic(imports[import].Intrinsic , args.Length))
                        throw bad("내장 함수 호출이 올바르지 않습니다.");
                    result = new IrIntrinsic(import , args);
                    break;
                }
                case IrOp.Assign:
                {
                    IrName n = read_name();
                    result = new IrAssign(n , slot(context.Frames[0]) , expression(line));
                    break;
                }
                case IrOp.ConstDef:
                {
                    int s = slot(context.Frames[0]);
                    object value = constant() ?? throw bad("상수 값이 없습니다.");
                    if (value is bool)
                        throw bad("상수 값이 올바르지 않습니다.");
                    result = new IrConstDef(s , value);
                    break;
                }
                //레이블의 값은 저장하지 않고 문장의 줄 번호를 씀
                case IrOp.Label: result = new IrLabel(slot(context.Frames[0]) , line); break;
                case IrOp.Paren: result = new IrParen(expression(line) , true); break;
                case IrOp.Unclosed: result = new IrParen(expression(line) , false); break;
                default: throw bad("알수 없는 식 종류입니다.");
            }
            context.Depth--;
            return result;
        }
        IrBlock block(IrStmt? owner)
        {
            enter();
            IrBlock b = new() { Owner = owner };
            context.Blocks.Add(b);
            IrStmt[] list = new IrStmt[r.Count(payload_length - r.Position)];
            for (int i = 0 ; i < list.Length ; i++)
            {
                list[i] = statement();
                list[i].Parent = b;
                list[i].Index = i;
            }
            b.Statements = list;
            context.Blocks.RemoveAt(context.Blocks.Count - 1);
            context.Depth--;
            return b;
        }
        IrBlock scoped(IrStmt owner , int size , bool function)
        {
            //새 영역 (함수라면 감싸는 블록/반복문도 새로 시작)
            context.Frames.Insert(0 , size);
            bool in_function = context.InFunction;
            List<IrBlock> blocks = new(context.Blocks);
            List<IrStmt> loops = new(context.Loops);
            if (function)
            {
                context.InFunction = true;
                context.Blocks.Clear();
                context.Loops.Clear();
            }
            else
                context.Loops.Add(owner);
            IrBlock body = block(owner);
            context.Frames.RemoveAt(0);
            context.InFunction = in_function;
            context.Blocks.Clear();
            context.Blocks.AddRange(blocks);
            context.Loops.Clear();
            context.Loops.AddRange(loops);
            return body;
        }
        IrStmt statement()
        {
            enter();
            byte kind = r.Byte();
            if (kind > (byte)IrKind.Empty)
                throw bad("알수 없는 문장 종류입니다.");
            int line = r.Index(program.LineCount);
            IrStmt s = new((IrKind)kind , line) { Id = all.Count };
            all.Add(s);
            s.Arguments = expressions(line);
            switch (s.Kind)
            {
                case IrKind.Expression:
                    s.Expression = expression(line);
                    break;
                case IrKind.If:
                    s.Body = block(s);
                    switch (r.Byte())
                    {
                        case 0: break;
                        case 1: s.Else = block(s); break;
                        default: throw bad("if 형식이 올바르지 않습니다.");
                    }
                    break;
                case IrKind.While:
                case IrKind.Repeat:
                    context.Loops.Add(s);
                    s.Body = block(s);
                    context.Loops.RemoveAt(context.Loops.Count - 1);
                    break;
                case IrKind.For:
                    s.FrameSize = count();
                    s.Slot = slot(s.FrameSize);
                    s.Body = scoped(s , s.FrameSize , false);
                    break;
                case IrKind.Func:
                    s.FrameSize = count();
                    s.Slot = slot(context.Frames[0]);
                    s.Body = scoped(s , s.FrameSize , true);
                    break;
                case IrKind.Goto:
                {
                    var targets = new (long, GotoTargetKind, int)[count()];
                    for (int i = 0 ; i < targets.Length ; i++)
                    {
                        ulong z = r.VarULong();
                        long target_line = (long)(z >> 1) ^ -(long)(z & 1);
                        byte target_kind = r.Byte();
                        if (target_kind > (byte)GotoTargetKind.Exit)
                            throw bad("goto 대상 종류가 올바르지 않습니다.");
                        targets[i] = (target_line , (GotoTargetKind)target_kind , (int)r.VarULong() - 1);
                    }
                    pending.Add(new Pending(s , new(context.Blocks) , new(context.Loops) , targets , -1));
                    break;
                }
                case IrKind.Break:
                    pending.Add(new Pending(s , new(context.Blocks) , new(context.Loops) , Array.Empty<(long, GotoTargetKind, int)>() , (int)Math.Min(r.VarULong() , int.MaxValue)));
                    break;
                case IrKind.Param:
                    if (!context.InFunction)
                        throw bad("param은 함수 안에서만 쓸수 있습니다.");
                    s.Slot = slot(context.Frames[0]);
                    s.ParameterIndex = r.Count(int.MaxValue);
                    break;
                case IrKind.Local:
                    s.Slot = slot(context.Frames[0]);
                    s.Name = strings[r.Index(strings.Length)];
                    switch (r.Byte())
                    {
                        case 0: break;
                        case 1: s.Expression = expression(line); break;
                        default: throw bad("local 형식이 올바르지 않습니다.");
                    }
                    break;
            }
            context.Depth--;
            return s;
        }

        program.Root = block(null);
        if (!r.End)
            throw bad("본문 뒤에 알수 없는 데이터가 있습니다.");
        program.StatementCount = all.Count;

        //break와 goto 대상: 감싸는 반복문/블록만 (같은 함수 안)
        foreach (Pending p in pending)
        {
            IrStmt s = p.Statement;
            if (s.Kind is IrKind.Break)
            {
                if (p.LoopId < 0 || p.LoopId >= all.Count || !p.Loops.Contains(all[p.LoopId]))
                    throw bad($"{s.Line}번 줄: break가 감싸는 반복문을 가리키지 않습니다.");
                s.Loop = all[p.LoopId];
                continue;
            }
            IrTarget[] targets = new IrTarget[p.Targets.Length];
            for (int i = 0 ; i < targets.Length ; i++)
            {
                var (target_line , kind , id) = p.Targets[i];
                IrStmt? target = null;
                if (kind is GotoTargetKind.Exit)
                {
                    if (id != -1)
                        throw bad("goto 대상이 올바르지 않습니다.");
                }
                else
                {
                    if (id < 0 || id >= all.Count)
                        throw bad("goto 대상이 올바르지 않습니다.");
                    target = all[id];
                    bool ok = kind is GotoTargetKind.Continue
                        ? target.Kind is IrKind.Repeat or IrKind.For && p.Loops.Contains(target)
                        : p.Blocks.Contains(target.Parent);
                    if (!ok)
                        throw bad($"{s.Line}번 줄: goto 대상이 감싸는 블록 밖이거나 블록 안쪽입니다.");
                }
                targets[i] = new IrTarget(target_line , kind , target);
            }
            s.Targets = targets;
        }
        return program;
    }

    #endregion

    #region XXH64 (seed 0)

    //헤더(체크섬 앞까지)와 본문 모두 보호
    internal static ulong Checksum(ReadOnlySpan<byte> header , ReadOnlySpan<byte> payload)
    {
        Span<byte> buffer = stackalloc byte[32];
        header[..24].CopyTo(buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[24..] , XxHash64(payload));
        return XxHash64(buffer);
    }

    const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL, P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;
    static ulong Round(ulong acc , ulong input) => System.Numerics.BitOperations.RotateLeft(acc + input * P2 , 31) * P1;
    static ulong Merge(ulong acc , ulong val) => (acc ^ Round(0 , val)) * P1 + P4;
    internal static ulong XxHash64(ReadOnlySpan<byte> data)
    {
        int i = 0;
        ulong h;
        if (data.Length >= 32)
        {
            ulong v1 = unchecked(P1 + P2) , v2 = P2 , v3 = 0 , v4 = unchecked(0 - P1);
            for (; i <= data.Length - 32 ; i += 32)
            {
                v1 = Round(v1 , BinaryPrimitives.ReadUInt64LittleEndian(data[i..]));
                v2 = Round(v2 , BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 8)..]));
                v3 = Round(v3 , BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 16)..]));
                v4 = Round(v4 , BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 24)..]));
            }
            h = System.Numerics.BitOperations.RotateLeft(v1 , 1) + System.Numerics.BitOperations.RotateLeft(v2 , 7) + System.Numerics.BitOperations.RotateLeft(v3 , 12) + System.Numerics.BitOperations.RotateLeft(v4 , 18);
            h = Merge(Merge(Merge(Merge(h , v1) , v2) , v3) , v4);
        }
        else
            h = P5;
        h += (ulong)data.Length;
        for (; i <= data.Length - 8 ; i += 8)
            h = System.Numerics.BitOperations.RotateLeft(h ^ Round(0 , BinaryPrimitives.ReadUInt64LittleEndian(data[i..])) , 27) * P1 + P4;
        if (i <= data.Length - 4)
        {
            h = System.Numerics.BitOperations.RotateLeft(h ^ (BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * P1) , 23) * P2 + P3;
            i += 4;
        }
        for (; i < data.Length ; i++)
            h = System.Numerics.BitOperations.RotateLeft(h ^ (data[i] * P5) , 11) * P1;
        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        h ^= h >> 32;
        return h;
    }

    #endregion

    sealed class Writer
    {
        readonly MemoryStream stream = new();
        public void Byte(byte b) => stream.WriteByte(b);
        public void Bytes(ReadOnlySpan<byte> bytes) => stream.Write(bytes);
        public void UInt16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b , v); Bytes(b); }
        public void UInt32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b , v); Bytes(b); }
        public void UInt64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b , v); Bytes(b); }
        public void Double(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b , BitConverter.DoubleToInt64Bits(v)); Bytes(b); }
        public void VarUInt(uint v) => VarULong(v);
        public void VarULong(ulong v)
        {
            while (v >= 0x80)
            {
                Byte((byte)(v | 0x80));
                v >>= 7;
            }
            Byte((byte)v);
        }
        public byte[] ToArray() => stream.ToArray();
    }

    //모든 읽기에서 범위를 검사하는 리더 (잘못된 파일은 항상 JyunoProgramException)
    sealed class Reader
    {
        public Reader(byte[] data)
        {
            this.data = data;
            position = 0;
        }
        readonly byte[] data;
        int position;
        public bool End => position == data.Length;
        public int Position => position;
        static JyunoProgramException truncated() => new("파일이 잘렸습니다.");
        public byte Byte() => position < data.Length ? data[position++] : throw truncated();
        public ReadOnlySpan<byte> Bytes(int length)
        {
            if ((uint)length > (uint)(data.Length - position))
                throw truncated();
            var span = data.AsSpan(position , length);
            position += length;
            return span;
        }
        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
        public double Double() => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Bytes(8)));
        public ulong VarULong()
        {
            ulong value = 0;
            for (int shift = 0 ; shift < 64 ; shift += 7)
            {
                byte b = Byte();
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    //가장 짧은 표현만 허용 (같은 값을 여러 방법으로 표현할수 없도록)
                    if (b is 0 && shift > 0)
                        throw new JyunoProgramException("정수 표현이 올바르지 않습니다.");
                    if (shift is 63 && b > 1)
                        throw new JyunoProgramException("정수가 범위를 벗어납니다.");
                    return value;
                }
            }
            throw new JyunoProgramException("정수 표현이 너무 깁니다.");
        }
        //0 이상 max 이하의 개수
        public int Count(int max)
        {
            ulong v = VarULong();
            return v <= (ulong)max ? (int)v : throw new JyunoProgramException($"개수가 허용 범위를 벗어납니다. ({v} > {max})");
        }
        //0 이상 count 미만의 인덱스
        public int Index(int count)
        {
            ulong v = VarULong();
            return v < (ulong)count ? (int)v : throw new JyunoProgramException("인덱스가 범위를 벗어납니다.");
        }
        static readonly UTF8Encoding strict = new(false , true);
        public string Utf8(int length)
        {
            try
            {
                return strict.GetString(Bytes(length));
            }
            catch (DecoderFallbackException)
            {
                throw new JyunoProgramException("문자열이 올바른 UTF-8이 아닙니다.");
            }
        }
    }}

public sealed class JyunoProgramException : JyunoException
{
    public JyunoProgramException(string message) : base(message) { }
}

/// <summary>호스트가 프로그램이 필요로 하는 전역을 제공하지 않음 (실행 전에 거부)</summary>
public sealed class JyunoLinkException : JyunoException
{
    public JyunoLinkException(IReadOnlyList<string> errors) : base(string.Join("\n" , errors)) { Errors = errors; }
    public IReadOnlyList<string> Errors { get; }
}
