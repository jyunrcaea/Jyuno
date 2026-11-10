using Jyuno.Compiler;
using Jyuno.Language;

namespace Jyuno.Analysis;

/// <summary>
/// 스크립트를 실행하지 않고 분석한 결과: 토큰, 블록 구조, 구문 트리, 변수 영역, goto 대상
/// </summary>
public sealed class ScriptAnalysis
{
    /// <summary>줄마다 토큰</summary>
    public IReadOnlyList<Token[]> Tokens { get; }
    /// <summary>줄 수</summary>
    public int LineCount => Tokens.Count;
    /// <summary>블록 구조 오류 (if/end 짝 등). 있다면 Root는 null</summary>
    public IReadOnlyList<GrammarError> StructureErrors { get; }
    /// <summary>각 줄이 속한 가장 안쪽 func/struct 줄 (함수 밖이라면 -1)</summary>
    public IReadOnlyList<int> Owners { get; }
    /// <summary>스크립트 최상위 블록 (블록 구조 오류가 있다면 null)</summary>
    public Block? Root { get; }
    /// <summary>스크립트 최상위 영역</summary>
    public Scope TopScope { get; }
    /// <summary>모든 영역 (Id 순서)</summary>
    public IReadOnlyList<Scope> Scopes => scopes;
    /// <summary>모든 함수와 구조체 (정의된 줄 순서, 구조체는 IsStruct가 true)</summary>
    public IReadOnlyList<FunctionStatement> Functions => functions;
    /// <summary>최상위(함수 밖)의 goto</summary>
    public IReadOnlyList<GotoStatement> TopGotos => top_gotos;
    /// <summary>스크립트 어딘가에서 삭제(이름 =)하는 이름</summary>
    public IReadOnlySet<string> RemovedNames => removed;

    readonly List<Scope> scopes = new();
    readonly List<FunctionStatement> functions = new();
    readonly List<GotoStatement> top_gotos = new();
    readonly HashSet<string> removed = new();
    readonly Interpreter.BlockMap map;

    ScriptAnalysis(IReadOnlyList<Token[]> tokens)
    {
        Tokens = tokens;
        map = new Interpreter.BlockMap(tokens);
        Owners = map.Owner;
        var errors = new List<GrammarError>();
        for (int line = 0 ; line < map.Count ; line++)
            if (map.Errors[line] is string error)
                errors.Add(new GrammarError(line , error));
        StructureErrors = errors;
        TopScope = new_scope(ScopeKind.Top , null , null);
        if (errors.Count > 0)
            return;
        Block root = new(null , null , tokens.Count);
        build(root , 0 , tokens.Count);
        Root = root;
        walk(root , TopScope);
        resolve_gotos(root , null , top_gotos);
        foreach (FunctionStatement function in functions)
            resolve_gotos(function.Body , function , function.gotos);
    }

    /// <summary>스크립트를 분석합니다. (줄은 '\n'으로 나눈 그대로)</summary>
    public static ScriptAnalysis Analyze(IReadOnlyList<string> lines) => new(lines.Select(line => Parser.Tokenizer(line).ToArray()).ToArray());

    Scope new_scope(ScopeKind kind , Scope? parent , FunctionStatement? function)
    {
        Scope scope = new(kind , parent , function , scopes.Count);
        scopes.Add(scope);
        return scope;
    }

    //─────────── 식 (LineCompiler.cs의 compile_expression/compile_call/compile_arguments와 같은 구조) ───────────

    static bool is_prefix(Token[] tokens , int index , char prefix) => index < tokens.Length && tokens[index].type is TokenType.Prefix && tokens[index].value is char c && c == prefix;

    static Expression close(Expression inner) => inner.MayEndEarly ? new ParenthesisExpression(inner) : inner;

    /// <summary>start 위치부터 식 하나를 해석합니다. (end: 식 다음 토큰의 위치)</summary>
    public static Expression ParseExpression(Token[] tokens , int start , bool closed , out int end)
    {
        end = tokens.Length;
        if (start >= tokens.Length)
            return new ConstantExpression(null);
        Token token = tokens[start];
        switch (token.type)
        {
            case TokenType.Constant:
                if (is_prefix(tokens , start + 1 , '=') && tokens.Length > start + 2 && tokens[start + 2].type is TokenType.Name)
                    return new ConstantDefinitionExpression((string)tokens[start + 2].value , token.value);
                end = start + 1;
                return new ConstantExpression(token.value);
            case TokenType.Name:
                string name = (string)token.value;
                if (is_prefix(tokens , start + 1 , '='))
                {
                    if (start + 2 >= tokens.Length || is_prefix(tokens , start + 2 , ')'))
                    {
                        end = start + 2;
                        return new RemoveExpression(name);
                    }
                    Expression value = ParseExpression(tokens , start + 2 , closed , out end);
                    return new AssignExpression(name , value);
                }
                if (is_prefix(tokens , start + 1 , ':'))
                    return new LabelExpression(name);
                return parse_call(tokens , start , closed , out end);
            case TokenType.Prefix:
                if (token.value is '(')
                {
                    Expression inner = ParseExpression(tokens , start + 1 , true , out int inner_end);
                    if (!is_prefix(tokens , inner_end , ')'))
                        return new UnclosedParenthesisExpression(inner);
                    end = inner_end + 1;
                    return close(inner);
                }
                if (token.value is ')')
                {
                    end = start;
                    return new ConstantExpression(null);
                }
                return new ConstantExpression(null);
            default:
                return new InvalidExpression();
        }
    }

    static Expression parse_call(Token[] tokens , int start , bool closed , out int end)
    {
        string name = (string)tokens[start].value;
        bool has_arguments = start + 1 < tokens.Length && !is_prefix(tokens , start + 1 , ')');
        var arguments = ParseArguments(tokens , start + 1 , out end);
        return new CallExpression(name , arguments , has_arguments , closed);
    }

    /// <summary>start 위치부터 ')' 또는 줄 끝까지 인자를 해석합니다.</summary>
    public static IReadOnlyList<Expression> ParseArguments(Token[] tokens , int start , out int end)
    {
        List<Expression> arguments = new();
        for (int i = start ; i < tokens.Length ; i++)
        {
            Token token = tokens[i];
            switch (token.type)
            {
                case TokenType.Constant:
                    arguments.Add(new ConstantExpression(token.value));
                    break;
                case TokenType.Name:
                    arguments.Add(new VariableExpression((string)token.value));
                    break;
                case TokenType.Prefix:
                    if (token.value is '(')
                    {
                        Expression inner = ParseExpression(tokens , i + 1 , true , out int inner_end);
                        if (!is_prefix(tokens , inner_end , ')'))
                        {
                            arguments.Add(new UnclosedParenthesisExpression(inner));
                            end = tokens.Length;
                            return arguments;
                        }
                        arguments.Add(close(inner));
                        i = inner_end;
                    }
                    else if (token.value is ')')
                    {
                        end = i;
                        return arguments;
                    }
                    break;
            }
        }
        end = tokens.Length;
        return arguments;
    }

    //─────────── 문장 트리 ───────────

    KeywordType? keyword_at(int line) => Tokens[line].Length > 0 && Tokens[line][0].type is TokenType.Keyword ? (KeywordType)Tokens[line][0].value : null;
    string? token_error(int line) => Tokens[line].Length > 0 && Tokens[line][^1].type is TokenType.Error ? (string)Tokens[line][^1].value : null;
    string? name_at(int line , int index) => Tokens[line].Length > index && Tokens[line][index].type is TokenType.Name ? (string)Tokens[line][index].value : null;
    IReadOnlyList<Expression> arguments_at(int line , int start) => token_error(line) is null ? ParseArguments(Tokens[line] , start , out _) : Array.Empty<Expression>();

    readonly Dictionary<int , Statement> statement_at = new();

    void add(Block block , Statement statement)
    {
        statement.Parent = block;
        statement.Index = block.statements.Count;
        block.statements.Add(statement);
        statement_at[statement.Line] = statement;
    }

    void build(Block block , int from , int to)
    {
        FunctionStatement? function = block.Function;
        for (int line = from ; line < to ;)
        {
            Token[] tokens = Tokens[line];
            KeywordType? keyword = keyword_at(line);
            string? error = token_error(line);
            if (keyword is null)
            {
                if (error is not null)
                    add(block , new TokenErrorStatement(line , error));
                else if (tokens.Length > 0)
                    add(block , new ExpressionStatement(line , ParseExpression(tokens , 0 , false , out _)));
                else
                    add(block , new Statement(StatementKind.Empty , line));
                line++;
                continue;
            }
            switch (keyword.Value)
            {
                case KeywordType.If:
                {
                    int pair = map.Pair[line];
                    IfStatement statement = new(line) { TokenError = error , Arguments = arguments_at(line , 1) };
                    int end_line = pair;
                    if (keyword_at(pair) is KeywordType.Else)
                    {
                        statement.ElseLine = pair;
                        statement.ElseTokenError = token_error(pair);
                        end_line = map.Pair[pair];
                    }
                    statement.EndLine = end_line;
                    statement.EndTokenError = token_error(end_line);
                    add(block , statement);
                    statement.Then = new Block(statement , function , statement.ElseLine >= 0 ? statement.ElseLine : end_line);
                    build(statement.Then , line + 1 , statement.Then.EndLine);
                    if (statement.ElseLine >= 0)
                    {
                        statement.Else = new Block(statement , function , end_line);
                        build(statement.Else , statement.ElseLine + 1 , end_line);
                    }
                    line = end_line + 1;
                    break;
                }
                case KeywordType.While:
                case KeywordType.Repeat:
                {
                    LoopStatement statement = new(keyword is KeywordType.While ? StatementKind.While : StatementKind.Repeat , line) { TokenError = error , Arguments = arguments_at(line , 1) };
                    statement.EndLine = map.Pair[line];
                    statement.EndTokenError = token_error(statement.EndLine);
                    add(block , statement);
                    statement.Body = new Block(statement , function , statement.EndLine);
                    build(statement.Body , line + 1 , statement.EndLine);
                    line = statement.EndLine + 1;
                    break;
                }
                case KeywordType.For:
                {
                    ForStatement statement = new(line) { TokenError = error , Arguments = arguments_at(line , 2) };
                    statement.VariableName = tokens.Length >= 3 ? name_at(line , 1) : null;
                    statement.EndLine = map.Pair[line];
                    statement.EndTokenError = token_error(statement.EndLine);
                    add(block , statement);
                    statement.Body = new Block(statement , function , statement.EndLine);
                    build(statement.Body , line + 1 , statement.EndLine);
                    line = statement.EndLine + 1;
                    break;
                }
                case KeywordType.Func:
                case KeywordType.Struct:
                {
                    FunctionStatement statement = new(line , keyword is KeywordType.Struct ? StatementKind.Struct : StatementKind.Func) { TokenError = error };
                    statement.Name = tokens.Length == 2 ? name_at(line , 1) : null;
                    statement.EndLine = map.Pair[line];
                    add(block , statement);
                    functions.Add(statement);
                    statement.Body = new Block(statement , statement , statement.EndLine);
                    build(statement.Body , line + 1 , statement.EndLine);
                    line = statement.EndLine + 1;
                    break;
                }
                case KeywordType.Return:
                    add(block , new Statement(StatementKind.Return , line) { TokenError = error , Arguments = arguments_at(line , 1) });
                    line++;
                    break;
                case KeywordType.Goto:
                {
                    GotoStatement statement = new(line) { TokenError = error , Arguments = arguments_at(line , 1) };
                    add(block , statement);
                    if (function is null)
                        top_gotos.Add(statement);
                    else
                        function.gotos.Add(statement);
                    line++;
                    break;
                }
                case KeywordType.Break:
                    //반복문 문장은 이미 만들어져 있음 (break는 반복문 안에만 있음)
                    add(block , new BreakStatement(line) { TokenError = error , Loop = (BlockStatement)statement_at[map.Pair[map.Pair[line]]] });
                    line++;
                    break;
                case KeywordType.Param:
                    add(block , new ParameterStatement(line) { TokenError = error , Name = tokens.Length == 2 ? name_at(line , 1) : null , ParameterIndex = map.ParamIndex[line] });
                    if (function is not null)
                        function.ParameterCount = Math.Max(function.ParameterCount , map.ParamIndex[line] + 1);
                    line++;
                    break;
                case KeywordType.Local:
                {
                    LocalStatement statement = new(line) { TokenError = error , Name = name_at(line , 1) };
                    if (error is null && tokens.Length > 2)
                    {
                        if (tokens.Length > 3 && is_prefix(tokens , 2 , '='))
                            statement.Value = ParseExpression(tokens , 3 , false , out _);
                        else
                            statement.Malformed = true;
                    }
                    add(block , statement);
                    line++;
                    break;
                }
                default:
                    //else/end/endfunc는 블록을 만들때 건너뛰므로 여기에 올수 없음
                    throw new InvalidOperationException($"예상하지 못한 키워드 {keyword} ({line} 줄)");
            }
        }
    }

    //─────────── 변수 영역 ───────────

    void walk_expression(Expression expression , Scope scope)
    {
        switch (expression)
        {
            case AssignExpression assign:
                scope.Add(assign.Name , NameKinds.Variable);
                walk_expression(assign.Value , scope);
                break;
            case ConstantDefinitionExpression constant:
                scope.Add(constant.Name , NameKinds.Constant);
                break;
            case LabelExpression label:
                scope.Add(label.Name , NameKinds.Variable);
                break;
            case RemoveExpression remove:
                removed.Add(remove.Name);
                break;
            case CallExpression call:
                foreach (Expression argument in call.Arguments)
                    walk_expression(argument , scope);
                break;
            case ParenthesisExpression paren:
                walk_expression(paren.Inner , scope);
                break;
            case UnclosedParenthesisExpression unclosed:
                walk_expression(unclosed.Inner , scope);
                break;
        }
    }

    void walk(Block block , Scope scope)
    {
        foreach (Statement statement in block.Statements)
        {
            statement.Scope = scope;
            foreach (Expression argument in statement.Arguments)
                walk_expression(argument , scope);
            switch (statement)
            {
                case ExpressionStatement expression:
                    walk_expression(expression.Expression , scope);
                    break;
                case IfStatement branch:
                    walk(branch.Then , scope);
                    if (branch.Else is not null)
                        walk(branch.Else , scope);
                    break;
                case LoopStatement loop:
                    walk(loop.Body , scope);
                    break;
                case ForStatement each:
                    each.InnerScope = new_scope(ScopeKind.For , scope , scope.Function);
                    if (each.VariableName is not null)
                        each.InnerScope.Add(each.VariableName , NameKinds.Variable);
                    walk(each.Body , each.InnerScope);
                    break;
                case FunctionStatement function:
                    if (function.Name is not null && function.TokenError is null)
                        scope.Add(function.Name , function.IsStruct ? NameKinds.Struct : NameKinds.Function);
                    function.InnerScope = new_scope(function.IsStruct ? ScopeKind.Struct : ScopeKind.Function , scope , function);
                    walk(function.Body , function.InnerScope);
                    break;
                case ParameterStatement parameter:
                    if (parameter.Name is not null)
                        scope.Add(parameter.Name , NameKinds.Variable);
                    break;
                case LocalStatement local:
                    if (local.Name is not null)
                        scope.Add(local.Name , NameKinds.Variable);
                    if (local.Value is not null)
                        walk_expression(local.Value , scope);
                    break;
            }
        }
    }

    /// <summary>문장들이 지금 영역에 만들거나 지울수 있는 이름 (안쪽 for/func 영역은 제외)</summary>
    public static ISet<string> NamesCreatedIn(IEnumerable<Statement> statements)
    {
        HashSet<string> names = new();
        collect(statements , names);
        return names;
    }

    static void collect(IEnumerable<Statement> statements , HashSet<string> names)
    {
        void expression(Expression e)
        {
            switch (e)
            {
                case AssignExpression assign: names.Add(assign.Name); expression(assign.Value); break;
                case ConstantDefinitionExpression constant: names.Add(constant.Name); break;
                case LabelExpression label: names.Add(label.Name); break;
                case RemoveExpression remove: names.Add(remove.Name); break;
                case CallExpression call: foreach (var a in call.Arguments) expression(a); break;
                case ParenthesisExpression paren: expression(paren.Inner); break;
                case UnclosedParenthesisExpression unclosed: expression(unclosed.Inner); break;
            }
        }
        foreach (Statement statement in statements)
        {
            foreach (Expression argument in statement.Arguments)
                expression(argument);
            switch (statement)
            {
                case ExpressionStatement e: expression(e.Expression); break;
                case IfStatement branch:
                    collect(branch.Then.Statements , names);
                    if (branch.Else is not null)
                        collect(branch.Else.Statements , names);
                    break;
                case LoopStatement loop: collect(loop.Body.Statements , names); break;
                case FunctionStatement function: if (function.Name is not null) names.Add(function.Name); break;
                case ParameterStatement parameter: if (parameter.Name is not null) names.Add(parameter.Name); break;
                case LocalStatement local:
                    if (local.Name is not null) names.Add(local.Name);
                    if (local.Value is not null) expression(local.Value);
                    break;
            }
        }
    }

    //─────────── goto 대상 ───────────

    //함수(또는 최상위) 안의 문장들 (안쪽 함수 제외)
    static void index(Block block , Dictionary<int , Statement> at)
    {
        foreach (Statement statement in block.Statements)
        {
            at[statement.Line] = statement;
            switch (statement)
            {
                case IfStatement branch:
                    index(branch.Then , at);
                    if (branch.Else is not null)
                        index(branch.Else , at);
                    break;
                case LoopStatement loop: index(loop.Body , at); break;
                case ForStatement each: index(each.Body , at); break;
            }
        }
    }

    /// <summary>goto가 같은 블록 또는 바깥 블록으로만 이동할 때 대상 줄을 실행 위치로 바꿈</summary>
    GotoTarget? position(long target , GotoStatement statement , FunctionStatement? function , Dictionary<int , Statement> at , out bool into_block)
    {
        into_block = false;
        if (target < 0 || target > LineCount)
            return null;
        int owner_line = function?.Line ?? -1;
        if ((target < LineCount ? Owners[(int)target] : -1) != owner_line)
            return null;
        if (target == LineCount || (function is not null && target == function.EndLine))
            return new GotoTarget(target , GotoTargetKind.Exit , null);
        //goto를 감싸는 블록과 문장
        List<Block> enclosing_blocks = new();
        List<Statement> enclosing = new();
        for (Block block = statement.Parent ; ;)
        {
            enclosing_blocks.Add(block);
            if (block.Owner is null || block.Owner == function)
                break;
            enclosing.Add(block.Owner);
            block = block.Owner.Parent;
        }
        GotoTarget result;
        if (at.TryGetValue((int)target , out Statement? found))
            result = new GotoTarget(target , GotoTargetKind.Before , found);
        else
        {
            //else/end 줄: 인터프리터가 그 줄을 실행했을 때와 같은 위치
            Statement? opener = at.Values.FirstOrDefault(o => o is BlockStatement b && b is not FunctionStatement && (b.EndLine == target || (b is IfStatement i && i.ElseLine == target)));
            if (opener is null)
                return null;
            if (opener is IfStatement)
                result = new GotoTarget(target , GotoTargetKind.After , opener);
            else if (opener.Kind is StatementKind.While)
                result = new GotoTarget(target , GotoTargetKind.Before , opener);
            else
                result = enclosing.Contains(opener) ? new GotoTarget(target , GotoTargetKind.Continue , opener) : new GotoTarget(target , GotoTargetKind.After , opener);
        }
        if (result.Kind is GotoTargetKind.Before or GotoTargetKind.After && !enclosing_blocks.Contains(result.Statement!.Parent))
        {
            into_block = true;
            return null;
        }
        return result;
    }

    void resolve_gotos(Block body , FunctionStatement? function , List<GotoStatement> gotos)
    {
        Dictionary<int , Statement> at = new();
        index(body , at);
        Dictionary<string , List<int>> labels = new();
        foreach (var (line , statement) in at.OrderBy(o => o.Key))
            if (statement is ExpressionStatement { Expression: LabelExpression label })
            {
                if (!labels.TryGetValue(label.Name , out var list))
                    labels[label.Name] = list = new();
                list.Add(line);
            }
        foreach (GotoStatement statement in gotos)
        {
            if (statement.TokenError is not null || statement.Arguments.Count is 0)
                continue;
            IEnumerable<long> candidates = statement.Arguments[0] switch {
                ConstantExpression { Value: long line } => new[] { line },
                ConstantExpression => Array.Empty<long>(),
                VariableExpression v when labels.ContainsKey(v.Name) => labels[v.Name].Select(o => (long)o),
                //계산한 대상: 이 함수의 레이블 줄 중 하나
                _ => labels.Values.SelectMany(o => o).Select(o => (long)o)
            };
            List<GotoTarget> targets = new();
            bool any_into_block = false;
            foreach (long candidate in candidates)
            {
                if (position(candidate , statement , function , at , out bool into_block) is GotoTarget target)
                    targets.Add(target);
                any_into_block |= into_block;
            }
            statement.Targets = targets;
            //레이블 이름 또는 정수 상수로 이동하는데, 이동할수 있는 줄이 모두 블록 안쪽이라면 Native JS에서 쓸수 없음
            if (targets.Count is 0 && any_into_block && statement.Arguments[0] is ConstantExpression or VariableExpression)
                into_block_gotos.Add(statement);
        }
    }
    readonly List<GotoStatement> into_block_gotos = new();

    //─────────── Native JS 제약 ───────────

    /// <summary>
    /// 스크립트를 JavaScript로 변환할때(Native JS) 지켜야 하는 제약을 검사합니다.
    /// 1. 변수/함수 삭제(이름 =)를 쓸수 없음
    /// 2. 내장 함수와 전역 함수/상수(add, mul, console.writeline, null 등)를 다시 정의하거나 대입할수 없음
    /// 3. goto로 블록(if/while/repeat/for) 안쪽으로 들어갈수 없음 (계산한 대상은 실행할때 검사)
    /// 4. 구조체(struct) 생성자 안에서 구조체를 정의할수 없음
    /// </summary>
    /// <param name="globals">전역 이름 (Runtime.GetGlobals())</param>
    public IReadOnlyList<GrammarError> CheckNativeJs(IReadOnlyDictionary<string , GlobalName> globals)
    {
        List<GrammarError> errors = new(StructureErrors);
        if (Root is null)
            return errors;
        bool reserved(string name) => globals.TryGetValue(name , out GlobalName? global) && global.Kind is not GlobalKind.Variable;
        void define(int line , string? name , string what)
        {
            if (name is not null && reserved(name))
                errors.Add(new GrammarError(line , $"Native JS 제약: 내장 함수/상수 '{name}'을/를 {what}할수 없습니다."));
        }
        void expression(int line , Expression e)
        {
            switch (e)
            {
                case RemoveExpression remove:
                    errors.Add(new GrammarError(line , $"Native JS 제약: 변수/함수 삭제('{remove.Name} =')를 쓸수 없습니다."));
                    break;
                case AssignExpression assign:
                    define(line , assign.Name , "대입");
                    expression(line , assign.Value);
                    break;
                case ConstantDefinitionExpression constant: define(line , constant.Name , "상수로 정의"); break;
                case LabelExpression label: define(line , label.Name , "레이블로 정의"); break;
                case CallExpression call: foreach (var a in call.Arguments) expression(line , a); break;
                case ParenthesisExpression paren: expression(line , paren.Inner); break;
                case UnclosedParenthesisExpression unclosed: expression(line , unclosed.Inner); break;
            }
        }
        void block(Block b)
        {
            foreach (Statement statement in b.Statements)
            {
                foreach (Expression argument in statement.Arguments)
                    expression(statement.Line , argument);
                switch (statement)
                {
                    case ExpressionStatement e: expression(e.Line , e.Expression); break;
                    case IfStatement branch:
                        block(branch.Then);
                        if (branch.Else is not null)
                            block(branch.Else);
                        break;
                    case LoopStatement loop: block(loop.Body); break;
                    case ForStatement each:
                        define(each.Line , each.VariableName , "for 변수로 정의");
                        block(each.Body);
                        break;
                    case FunctionStatement { IsStruct: true } structure when structure.InnerScope.Parent?.Kind is ScopeKind.Struct:
                        //구조체 생성자 안에서 정의한 구조체는 구조체 값을 복사할때마다 새 구조체 값에 다시 묶어야 해서 지원하지 않음
                        errors.Add(new GrammarError(structure.Line , "Native JS 제약: 구조체(struct) 안에서 구조체를 정의할수 없습니다."));
                        break;
                    case FunctionStatement function:
                        define(function.Line , function.Name , "함수로 정의");
                        block(function.Body);
                        break;
                    case ParameterStatement parameter: define(parameter.Line , parameter.Name , "파라미터로 정의"); break;
                    case LocalStatement local:
                        define(local.Line , local.Name , "지역 변수로 정의");
                        if (local.Value is not null)
                            expression(local.Line , local.Value);
                        break;
                }
            }
        }
        block(Root);
        foreach (GotoStatement statement in into_block_gotos)
            errors.Add(new GrammarError(statement.Line , "Native JS 제약: goto로 블록(if, while, repeat, for) 안쪽으로 들어갈수 없습니다."));
        errors.Sort((a , b) => a.Line.CompareTo(b.Line));
        return errors;
    }
}

public enum GlobalKind
{
    /// <summary>함수 (Jyuno 내장 함수 또는 프로그램이 추가한 함수)</summary>
    Function,
    /// <summary>값을 읽고 쓸수 있는 변수 (console.title 등)</summary>
    Variable,
    /// <summary>상수 (null, true, false, math.pi 등)</summary>
    Constant
}

/// <summary>런타임의 전역 이름 하나</summary>
/// <param name="Builtin">Jyuno의 필수 명령어(add, length 등)인지</param>
/// <param name="Value">상수라면 그 값</param>
public sealed record GlobalName(string Name , GlobalKind Kind , bool Builtin , object? Value);
