using Jyuno.Analysis;
using Jyuno.Language;

namespace Jyuno.Compiler;

//[프로토타입] 제한 모드(Native JS 제약)로 미리 해석한 프로그램
//
//제한 모드에서는 삭제(이름 =)와 전역 함수/상수 재정의가 없고, goto는 같은/바깥 블록으로만 이동하므로
//  - 이름은 실행 전에 "영역의 칸(슬롯) 후보 + 전역(가져오기 표)"으로 정해짐 (사전 조회, 버전 캐시가 필요 없음)
//  - 내장 함수 호출은 가드 없이 바로 계산하고, 상수 인자 호출은 빌드할 때 계산함
//  - goto는 검증된 대상 문장으로만 이동함
//프로그램이 전역에 닿는 방법은 가져오기 표(Imports)의 번호뿐이므로, 불러올 때 이 표만 호스트와 대조하면 됨

/// <summary>프로그램이 기대하는 전역 이름 하나</summary>
internal sealed record IrImport(string Name , GlobalKind Kind , Intrinsic Intrinsic , bool Pure);

/// <summary>식 맨 앞/인자의 이름: 안쪽 영역부터 찾을 칸 후보와, 없을 때의 전역</summary>
internal sealed class IrName
{
    public IrName(string name , (int Hops, int Slot)[] candidates , int import)
    {
        Name = name;
        Candidates = candidates;
        Import = import;
    }
    public readonly string Name;
    public readonly (int Hops, int Slot)[] Candidates;
    /// <summary>가져오기 표 번호 (전역이 아니라면 -1)</summary>
    public readonly int Import;
}

internal enum IrOp : byte { Const, Read, Call, Intrinsic, Assign, ConstDef, Label, Paren, Unclosed }

internal abstract class IrExpr
{
    public abstract IrOp Op { get; }
}
internal sealed class IrConst : IrExpr
{
    public IrConst(object? value) { Value = value; }
    public readonly object? Value;
    public override IrOp Op => IrOp.Const;
}
internal sealed class IrRead : IrExpr
{
    public IrRead(IrName name) { Name = name; }
    public readonly IrName Name;
    public override IrOp Op => IrOp.Read;
}
internal sealed class IrCall : IrExpr
{
    public IrCall(IrName name , IrExpr[] arguments , bool has_arguments , bool closed)
    {
        Name = name;
        Arguments = arguments;
        HasArguments = has_arguments;
        Closed = closed;
    }
    public readonly IrName Name;
    public readonly IrExpr[] Arguments;
    public readonly bool HasArguments, Closed;
    public override IrOp Op => IrOp.Call;
}
//내장 함수 호출 (인자 1~3개, 가드 없음)
internal sealed class IrIntrinsic : IrExpr
{
    public IrIntrinsic(int import , IrExpr[] arguments) { Import = import; Arguments = arguments; }
    public readonly int Import;
    public readonly IrExpr[] Arguments;
    public override IrOp Op => IrOp.Intrinsic;
}
internal sealed class IrAssign : IrExpr
{
    public IrAssign(IrName name , int create , IrExpr value) { Name = name; Create = create; Value = value; }
    public readonly IrName Name;
    //없을 때 만들 칸 (지금 영역)
    public readonly int Create;
    public readonly IrExpr Value;
    public override IrOp Op => IrOp.Assign;
}
internal sealed class IrConstDef : IrExpr
{
    public IrConstDef(int slot , object value) { Slot = slot; Value = value; }
    public readonly int Slot;
    public readonly object Value;
    public override IrOp Op => IrOp.ConstDef;
}
internal sealed class IrLabel : IrExpr
{
    public IrLabel(int slot , int line) { Slot = slot; Line = line; }
    public readonly int Slot, Line;
    public override IrOp Op => IrOp.Label;
}
internal sealed class IrParen : IrExpr
{
    public IrParen(IrExpr inner , bool closed) { Inner = inner; Closed = closed; }
    public readonly IrExpr Inner;
    //true: ')' 전에 끝났다면 오류 (ParenthesisExpression), false: 닫히지 않은 괄호 (계산 후 항상 오류)
    public readonly bool Closed;
    public override IrOp Op => Closed ? IrOp.Paren : IrOp.Unclosed;
}

internal enum IrKind : byte { Expression, If, While, Repeat, For, Func, Return, Goto, Break, Param, Local, Empty }

internal class IrStmt
{
    public IrStmt(IrKind kind , int line) { Kind = kind; Line = line; }
    public readonly IrKind Kind;
    public readonly int Line;
    //문장 번호 (전위 순회 순서, break/goto 대상 참조용)
    public int Id;
    public IrBlock Parent = null!;
    public int Index;
    public IrExpr[] Arguments = Array.Empty<IrExpr>();
    public IrExpr? Expression; //Expression, Local의 값
    public int Slot = -1; //For: 변수 칸(반복 영역), Func/Param/Local: 지금 영역의 칸
    public int FrameSize; //For/Func: 새 영역의 칸 수
    public int ParameterIndex;
    public string? Name; //Local, Func: 오류 메시지용
    public IrBlock? Body, Else;
    public IrStmt? Loop; //Break
    public IrTarget[] Targets = Array.Empty<IrTarget>(); //Goto
}

internal sealed class IrBlock
{
    public IrStmt? Owner;
    public IrStmt[] Statements = Array.Empty<IrStmt>();
}

internal sealed record IrTarget(long Line , GotoTargetKind Kind , IrStmt? Statement);

internal sealed class IrProgram
{
    public int LineCount;
    public int TopFrameSize;
    public IrBlock Root = null!;
    public IrImport[] Imports = Array.Empty<IrImport>();
    //줄마다 속한 함수의 줄 (밖이라면 -1, goto 검사용)
    public int[] LineOwner = Array.Empty<int>();
    //스크립트가 만들수 있는 모든 이름 (호스트 전역과 겹치는지 검사)
    public string[] ScriptNames = Array.Empty<string>();
    //이름 표 (IrName.Name, 오류 메시지용)
    public int StatementCount;
}

/// <summary>빌드 오류 (제한 모드를 지키지 않는 코드)</summary>
public sealed class JyunoBuildException : JyunoException
{
    public JyunoBuildException(IReadOnlyList<GrammarError> errors) : base(string.Join("\n" , errors)) { Errors = errors; }
    public IReadOnlyList<GrammarError> Errors { get; }
}

//구문 트리(Jyuno.Analysis) → IR
internal sealed class IrBuilder
{
    readonly ScriptAnalysis analysis;
    readonly IReadOnlyDictionary<string , GlobalName> globals;
    readonly VariableDictionary objects;
    readonly List<IrImport> imports = new();
    readonly Dictionary<string , int> import_index = new();
    readonly List<GrammarError> errors = new();
    readonly Dictionary<Statement , IrStmt> made = new();
    readonly List<(IrStmt ir, Statement source)> fixups = new();
    int statement_count;

    IrBuilder(ScriptAnalysis analysis , Runtime profile)
    {
        this.analysis = analysis;
        globals = profile.GetGlobals();
        objects = profile.Global;
    }

    public static IrProgram Build(IReadOnlyList<string> lines , Runtime profile)
    {
        ScriptAnalysis analysis = ScriptAnalysis.Analyze(lines);
        IrBuilder builder = new(analysis , profile);
        builder.errors.AddRange(analysis.CheckNativeJs(builder.globals));
        if (analysis.Root is null || builder.errors.Count > 0)
            throw new JyunoBuildException(builder.errors);
        IrProgram program = new() { LineCount = analysis.LineCount , TopFrameSize = analysis.TopScope.Names.Count };
        program.Root = builder.block(analysis.Root , null);
        foreach (var (ir , source) in builder.fixups)
            builder.fixup(ir , source);
        if (builder.errors.Count > 0)
        {
            builder.errors.Sort((a , b) => a.Line.CompareTo(b.Line));
            throw new JyunoBuildException(builder.errors);
        }
        program.Imports = builder.imports.ToArray();
        program.LineOwner = analysis.Owners.ToArray();
        program.ScriptNames = analysis.Scopes.SelectMany(s => s.NameOrder).Distinct().OrderBy(o => o , StringComparer.Ordinal).ToArray();
        program.StatementCount = builder.statement_count;
        return program;
    }

    void error(int line , string message) => errors.Add(new GrammarError(line , message));

    //영역마다 이름→슬롯 표를 한번만 만듦 (큰 스크립트에서 이름마다 NameOrder를 훑으면 제곱 시간)
    readonly Dictionary<Scope , Dictionary<string , int>> slot_tables = new(ReferenceEqualityComparer.Instance);

    int slot_of(Scope scope , string name)
    {
        if (!slot_tables.TryGetValue(scope , out Dictionary<string , int>? table))
        {
            table = new(StringComparer.Ordinal);
            int i = 0;
            foreach (string n in scope.NameOrder)
                table.TryAdd(n , i++);
            slot_tables[scope] = table;
        }
        return table.TryGetValue(name , out int slot) ? slot : throw new InvalidOperationException($"영역에 '{name}'이/가 없습니다.");
    }

    int import(string name)
    {
        if (import_index.TryGetValue(name , out int index))
            return index;
        GlobalName global = globals[name];
        objects.TryGetValue(name , out object? holder);
        NativeFunction? native = holder as NativeFunction;
        imports.Add(new IrImport(name , global.Kind , native?.Intrinsic ?? Intrinsic.None , native?.Foldable is not null));
        import_index[name] = index = imports.Count - 1;
        return index;
    }

    IrName name(string name , Scope scope)
    {
        List<(int, int)> candidates = new();
        int hops = 0;
        for (Scope? s = scope ; s is not null ; s = s.Parent , hops++)
        {
            if (s.Names.ContainsKey(name))
                candidates.Add((hops , slot_of(s , name)));
        }
        int import_number = globals.ContainsKey(name) ? import(name) : -1;
        return new IrName(name , candidates.ToArray() , import_number);
    }

    IrBlock block(Block source , IrStmt? owner)
    {
        IrBlock block = new() { Owner = owner };
        List<IrStmt> list = new();
        foreach (Statement statement in source.Statements)
        {
            IrStmt ir = this.statement(statement);
            ir.Parent = block;
            ir.Index = list.Count;
            list.Add(ir);
        }
        block.Statements = list.ToArray();
        return block;
    }

    IrStmt make(IrKind kind , Statement source)
    {
        IrStmt ir = new(kind , source.Line) { Id = statement_count++ };
        made[source] = ir;
        if (source.TokenError is not null)
            error(source.Line , source.TokenError);
        ir.Arguments = source.Arguments.Select(o => expression(o , source.Scope , source.Line)).ToArray();
        return ir;
    }

    IrStmt statement(Statement source)
    {
        Scope scope = source.Scope;
        switch (source)
        {
            case TokenErrorStatement token:
                error(token.Line , token.Message);
                return make(IrKind.Empty , source);
            case ExpressionStatement e:
            {
                IrStmt ir = make(IrKind.Expression , source);
                ir.Expression = expression(e.Expression , scope , e.Line);
                return ir;
            }
            case IfStatement branch:
            {
                IrStmt ir = make(IrKind.If , source);
                if (branch.ElseTokenError is not null) error(branch.ElseLine , branch.ElseTokenError);
                if (branch.EndTokenError is not null) error(branch.EndLine , branch.EndTokenError);
                ir.Body = block(branch.Then , ir);
                if (branch.Else is not null)
                    ir.Else = block(branch.Else , ir);
                return ir;
            }
            case LoopStatement loop:
            {
                IrStmt ir = make(loop.Kind is StatementKind.While ? IrKind.While : IrKind.Repeat , source);
                if (loop.EndTokenError is not null) error(loop.EndLine , loop.EndTokenError);
                ir.Body = block(loop.Body , ir);
                return ir;
            }
            case ForStatement each:
            {
                IrStmt ir = make(IrKind.For , source);
                if (each.EndTokenError is not null) error(each.EndLine , each.EndTokenError);
                if (each.VariableName is null)
                {
                    error(each.Line , "for는 'for (이름) (배열/리스트/문자열)' 형식으로만 쓸수 있습니다.");
                    return ir;
                }
                ir.FrameSize = each.InnerScope.Names.Count;
                ir.Slot = slot_of(each.InnerScope , each.VariableName);
                ir.Body = block(each.Body , ir);
                return ir;
            }
            case FunctionStatement function:
            {
                IrStmt ir = make(IrKind.Func , source);
                if (function.Name is null)
                {
                    error(function.Line , "func 다음에는 함수 이름 하나만 와야 합니다.");
                    return ir;
                }
                ir.Slot = slot_of(scope , function.Name);
                ir.Name = function.Name;
                ir.FrameSize = function.InnerScope.Names.Count;
                ir.Body = block(function.Body , ir);
                return ir;
            }
            case GotoStatement jump:
            {
                IrStmt ir = make(IrKind.Goto , source);
                fixups.Add((ir , source));
                return ir;
            }
            case BreakStatement stop:
            {
                IrStmt ir = make(IrKind.Break , source);
                fixups.Add((ir , source));
                return ir;
            }
            case ParameterStatement parameter:
            {
                IrStmt ir = make(IrKind.Param , source);
                if (parameter.Name is null)
                {
                    error(parameter.Line , "param 다음에는 파라미터 이름 하나만 와야 합니다.");
                    return ir;
                }
                ir.Slot = slot_of(scope , parameter.Name);
                ir.ParameterIndex = parameter.ParameterIndex;
                return ir;
            }
            case LocalStatement local:
            {
                IrStmt ir = make(IrKind.Local , source);
                if (local.Name is null || local.Malformed)
                {
                    error(local.Line , "local은 'local (이름)' 또는 'local (이름) = (값)' 형식으로만 쓸수 있습니다.");
                    return ir;
                }
                ir.Slot = slot_of(scope , local.Name);
                ir.Name = local.Name;
                if (local.Value is not null)
                    ir.Expression = expression(local.Value , scope , local.Line);
                return ir;
            }
            default:
                if (source.Kind is StatementKind.Return)
                    return make(IrKind.Return , source);
                return make(IrKind.Empty , source);
        }
    }

    void fixup(IrStmt ir , Statement source)
    {
        switch (source)
        {
            case BreakStatement stop:
                ir.Loop = made[stop.Loop];
                break;
            case GotoStatement jump:
                ir.Targets = jump.Targets.Select(t => new IrTarget(t.Line , t.Kind , t.Statement is null ? null : made[t.Statement])).ToArray();
                break;
        }
    }

    //빌드할 때 계산한 상수 (null이면 상수가 아님)
    sealed class Folded
    {
        public Folded(object? value) { Value = value; }
        public readonly object? Value;
    }
    readonly Dictionary<IrExpr , Folded> constants = new();

    IrExpr expression(Expression source , Scope scope , int line)
    {
        switch (source)
        {
            case ConstantExpression constant:
            {
                IrConst ir = new(constant.Value);
                constants[ir] = new Folded(constant.Value);
                return ir;
            }
            case VariableExpression variable:
                return new IrRead(name(variable.Name , scope));
            case CallExpression call:
            {
                IrName target = name(call.Name , scope);
                IrExpr[] arguments = call.Arguments.Select(o => expression(o , scope , line)).ToArray();
                //전역 함수만 가리키는 이름 (제한 모드에서는 다시 정의할수 없음)
                if (target.Candidates.Length is 0 && target.Import >= 0 && objects.TryGetValue(call.Name , out object? holder) && holder is NativeFunction native)
                {
                    //상수 계산: 인자가 모두 상수이고 미리 계산할수 있는 내장 함수
                    if (native.Foldable is Func<object?[] , bool> foldable && arguments.All(constants.ContainsKey))
                    {
                        object?[] values = arguments.Select(o => constants[o].Value).ToArray();
                        try
                        {
                            if (foldable(values))
                            {
                                object? result = native.Execute(values);
                                if (result is null or long or double or bool or string)
                                {
                                    IrConst folded = new(result);
                                    constants[folded] = new Folded(result);
                                    return folded;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            //오류는 실행할 때 그대로 나도록
                        }
                    }
                    if (Interpreter.SupportsIntrinsic(native.Intrinsic , arguments.Length))
                        return new IrIntrinsic(target.Import , arguments);
                }
                return new IrCall(target , arguments , call.HasArguments , call.Closed);
            }
            case AssignExpression assign:
                return new IrAssign(name(assign.Name , scope) , slot_of(scope , assign.Name) , expression(assign.Value , scope , line));
            case ConstantDefinitionExpression definition:
                return new IrConstDef(slot_of(scope , definition.Name) , definition.Value);
            case LabelExpression label:
                return new IrLabel(slot_of(scope , label.Name) , line);
            case ParenthesisExpression paren:
            {
                IrExpr inner = expression(paren.Inner , scope , line);
                //상수였다면 ')' 전에 끝날 일이 없음
                if (constants.TryGetValue(inner , out Folded? folded) && inner is IrConst)
                    return inner;
                return new IrParen(inner , true);
            }
            case UnclosedParenthesisExpression unclosed:
                return new IrParen(expression(unclosed.Inner , scope , line) , false);
            case RemoveExpression:
                //CheckNativeJs에서 이미 오류
                error(line , "제한 모드: 삭제를 쓸수 없습니다.");
                return new IrConst(null);
            default:
                error(line , "처리할수 없습니다.");
                return new IrConst(null);
        }
    }
}
