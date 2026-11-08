using Jyuno.Compiler;
using Jyuno.Language;

namespace Jyuno.Analysis;

//스크립트를 실행하지 않고 해석한 구문 트리
//인터프리터가 줄을 컴파일하는 방법(LineCompiler.cs)과 같은 규칙으로 식을 나누므로,
//다른 실행기(예: JyunoSdk의 Native JS 변환기)가 인터프리터와 같은 의미로 코드를 만들수 있음

/// <summary>식 (줄 하나 또는 키워드 인자 하나)</summary>
public abstract class Expression
{
    private protected Expression() { }
    /// <summary>괄호 안에서 ')' 전에 끝날수 있는지 (인자가 있는데 배열/리스트가 아닌 값을 읽은 경우)</summary>
    public virtual bool MayEndEarly => false;
}

/// <summary>상수 (long, double, string, 또는 식이 비어있을 때의 null)</summary>
public sealed class ConstantExpression : Expression
{
    internal ConstantExpression(object? value) { Value = value; }
    public object? Value { get; }
}

/// <summary>인자 자리의 이름: 변수의 값을 읽음 (함수라면 오류)</summary>
public sealed class VariableExpression : Expression
{
    internal VariableExpression(string name) { Name = name; }
    public string Name { get; }
}

/// <summary>식 맨 앞의 이름: 함수라면 호출, 변수라면 값 (인자가 있고 배열/리스트라면 원소 읽기/쓰기)</summary>
public sealed class CallExpression : Expression
{
    internal CallExpression(string name , IReadOnlyList<Expression> arguments , bool has_arguments , bool closed)
    {
        Name = name;
        Arguments = arguments;
        HasArguments = has_arguments;
        Closed = closed;
    }
    public string Name { get; }
    public IReadOnlyList<Expression> Arguments { get; }
    /// <summary>이름 바로 다음에 ')'가 아닌 토큰이 있는지</summary>
    public bool HasArguments { get; }
    /// <summary>괄호 안의 식인지</summary>
    public bool Closed { get; }
    public override bool MayEndEarly => Closed && HasArguments;
}

/// <summary>(이름) = (값)</summary>
public sealed class AssignExpression : Expression
{
    internal AssignExpression(string name , Expression value) { Name = name; Value = value; }
    public string Name { get; }
    public Expression Value { get; }
    public override bool MayEndEarly => Value.MayEndEarly;
}

/// <summary>(이름) = : 변수/함수 삭제</summary>
public sealed class RemoveExpression : Expression
{
    internal RemoveExpression(string name) { Name = name; }
    public string Name { get; }
}

/// <summary>(상수) = (이름): 상수 만들기</summary>
public sealed class ConstantDefinitionExpression : Expression
{
    internal ConstantDefinitionExpression(string name , object value) { Name = name; Value = value; }
    public string Name { get; }
    public object Value { get; }
}

/// <summary>(이름): 레이블 (지금 줄 번호를 값으로 갖는 변수)</summary>
public sealed class LabelExpression : Expression
{
    internal LabelExpression(string name) { Name = name; }
    public string Name { get; }
}

/// <summary>괄호로 닫힌 식 중 ')' 전에 끝날수 있는 식 (끝났다면 괄호 오류)</summary>
public sealed class ParenthesisExpression : Expression
{
    internal ParenthesisExpression(Expression inner) { Inner = inner; }
    public Expression Inner { get; }
}

/// <summary>'('가 ')'로 닫히지 않은 식 (안쪽 식을 계산한 다음 괄호 오류)</summary>
public sealed class UnclosedParenthesisExpression : Expression
{
    internal UnclosedParenthesisExpression(Expression inner) { Inner = inner; }
    public Expression Inner { get; }
}

/// <summary>처리할수 없는 식 (식 맨 앞의 키워드 등, 실행하면 '처리할수 없습니다.' 오류)</summary>
public sealed class InvalidExpression : Expression
{
    internal InvalidExpression() { }
}

public enum StatementKind
{
    Empty,
    /// <summary>토큰화 오류가 있는 줄 (실행하면 문법 오류)</summary>
    TokenError,
    Expression,
    If,
    While,
    Repeat,
    For,
    Func,
    Return,
    Goto,
    Break,
    Param,
    Local
}

/// <summary>문장 (줄 하나, 또는 블록 하나)</summary>
public class Statement
{
    internal Statement(StatementKind kind , int line)
    {
        Kind = kind;
        Line = line;
    }
    public StatementKind Kind { get; }
    /// <summary>문장이 시작하는 줄 (0부터)</summary>
    public int Line { get; }
    /// <summary>이 줄의 토큰화 오류 (키워드 줄은 실행하면 문법 오류)</summary>
    public string? TokenError { get; internal set; }
    /// <summary>이 문장이 들어있는 블록</summary>
    public Block Parent { get; internal set; } = null!;
    /// <summary>블록 안에서 몇번째 문장인지</summary>
    public int Index { get; internal set; }
    /// <summary>이 문장을 실행하는 변수 영역</summary>
    public Scope Scope { get; internal set; } = null!;
    /// <summary>이 문장이 들어있는 함수 (최상위라면 null)</summary>
    public FunctionStatement? Function => Parent.Function;
    /// <summary>키워드 다음의 인자 (return/goto/if/while/repeat는 키워드 다음부터, for는 이름 다음부터)</summary>
    public IReadOnlyList<Expression> Arguments { get; internal set; } = Array.Empty<Expression>();
}

/// <summary>키워드가 없는 줄</summary>
public sealed class ExpressionStatement : Statement
{
    internal ExpressionStatement(int line , Expression expression) : base(StatementKind.Expression , line) { Expression = expression; }
    public Expression Expression { get; }
}

/// <summary>토큰화 오류가 있는 줄</summary>
public sealed class TokenErrorStatement : Statement
{
    internal TokenErrorStatement(int line , string message) : base(StatementKind.TokenError , line) { TokenError = message; }
    public string Message => TokenError!;
}

/// <summary>블록을 닫는 줄(end, else)의 정보가 있는 문장</summary>
public abstract class BlockStatement : Statement
{
    internal BlockStatement(StatementKind kind , int line) : base(kind , line) { }
    /// <summary>블록을 닫는 줄 (end 또는 endfunc)</summary>
    public int EndLine { get; internal set; }
    /// <summary>end 줄의 토큰화 오류</summary>
    public string? EndTokenError { get; internal set; }
}

public sealed class IfStatement : BlockStatement
{
    internal IfStatement(int line) : base(StatementKind.If , line) { }
    public Block Then { get; internal set; } = null!;
    public Block? Else { get; internal set; }
    /// <summary>else 줄 (없다면 -1)</summary>
    public int ElseLine { get; internal set; } = -1;
    public string? ElseTokenError { get; internal set; }
}

/// <summary>while, repeat</summary>
public sealed class LoopStatement : BlockStatement
{
    internal LoopStatement(StatementKind kind , int line) : base(kind , line) { }
    public Block Body { get; internal set; } = null!;
}

public sealed class ForStatement : BlockStatement
{
    internal ForStatement(int line) : base(StatementKind.For , line) { }
    /// <summary>원소를 받을 변수 이름 (형식이 잘못되었다면 null)</summary>
    public string? VariableName { get; internal set; }
    public Block Body { get; internal set; } = null!;
    /// <summary>반복 한번마다 새로 만들어지는 영역</summary>
    public Scope InnerScope { get; internal set; } = null!;
}

public sealed class FunctionStatement : BlockStatement
{
    internal FunctionStatement(int line) : base(StatementKind.Func , line) { }
    /// <summary>함수 이름 (형식이 잘못되었다면 null)</summary>
    public string? Name { get; internal set; }
    public int ParameterCount { get; internal set; }
    public Block Body { get; internal set; } = null!;
    /// <summary>호출 한번마다 새로 만들어지는 영역</summary>
    public Scope InnerScope { get; internal set; } = null!;
    /// <summary>이 함수 안(안쪽 함수 제외)의 goto</summary>
    public IReadOnlyList<GotoStatement> Gotos => gotos;
    internal readonly List<GotoStatement> gotos = new();
}

public sealed class GotoStatement : Statement
{
    internal GotoStatement(int line) : base(StatementKind.Goto , line) { }
    /// <summary>
    /// 이동할수 있는 줄과 위치 (Native JS처럼 구조적인 제어 흐름으로 바꿀때 사용)
    /// 같은 블록 또는 바깥 블록의 줄만 포함하며, 블록 안쪽으로 들어가는 줄은 포함하지 않음
    /// </summary>
    public IReadOnlyList<GotoTarget> Targets { get; internal set; } = Array.Empty<GotoTarget>();
}

public sealed class BreakStatement : Statement
{
    internal BreakStatement(int line) : base(StatementKind.Break , line) { }
    /// <summary>빠져나갈 반복문</summary>
    public BlockStatement Loop { get; internal set; } = null!;
}

public sealed class ParameterStatement : Statement
{
    internal ParameterStatement(int line) : base(StatementKind.Param , line) { }
    public string? Name { get; internal set; }
    /// <summary>함수의 몇번째 파라미터인지</summary>
    public int ParameterIndex { get; internal set; }
}

public sealed class LocalStatement : Statement
{
    internal LocalStatement(int line) : base(StatementKind.Local , line) { }
    public string? Name { get; internal set; }
    /// <summary>local (이름) = (값) 의 값</summary>
    public Expression? Value { get; internal set; }
    /// <summary>'local (이름)' 또는 'local (이름) = (값)' 형식이 아님</summary>
    public bool Malformed { get; internal set; }
}

/// <summary>문장의 목록 (스크립트 전체, 블록의 본문)</summary>
public sealed class Block
{
    internal Block(BlockStatement? owner , FunctionStatement? function , int end_line)
    {
        Owner = owner;
        Function = function;
        EndLine = end_line;
    }
    /// <summary>이 블록을 가진 문장 (스크립트 최상위라면 null)</summary>
    public BlockStatement? Owner { get; }
    /// <summary>이 블록이 들어있는 함수 (최상위라면 null)</summary>
    public FunctionStatement? Function { get; }
    /// <summary>블록이 끝나는 줄 (else/end/endfunc 줄, 최상위라면 줄 수)</summary>
    public int EndLine { get; }
    public IReadOnlyList<Statement> Statements => statements;
    internal readonly List<Statement> statements = new();
}

public enum GotoTargetKind
{
    /// <summary>Statement 앞으로 이동</summary>
    Before,
    /// <summary>Statement 다음으로 이동 (if의 else/end 줄)</summary>
    After,
    /// <summary>goto를 감싸는 반복문(Statement)의 다음 반복 (repeat/for의 end 줄)</summary>
    Continue,
    /// <summary>함수 또는 스크립트를 끝냄 (endfunc 줄, 마지막 줄 다음)</summary>
    Exit
}

/// <summary>goto가 이동할수 있는 줄 하나</summary>
public sealed record GotoTarget(long Line , GotoTargetKind Kind , Statement? Statement);
