using Jyuno.Analysis;

namespace Jyuno.Tests;

//코드를 실행하지 않고 분석하는 API (Jyuno.Analysis): 구문 트리, 변수 영역, goto 대상, Native JS 제약
public class AnalysisTests
{
    static ScriptAnalysis Analyze(params string[] lines) => ScriptAnalysis.Analyze(lines);
    static readonly IReadOnlyDictionary<string , GlobalName> Globals = new Runtime().GetGlobals();
    static IReadOnlyList<Language.GrammarError> Check(params string[] lines) => Analyze(lines).CheckNativeJs(Globals);

    [Fact]
    public void ExpressionTree()
    {
        var a = Analyze("x = add 1 (mul y 2)");
        var statement = Assert.IsType<ExpressionStatement>(Assert.Single(a.Root!.Statements));
        var assign = Assert.IsType<AssignExpression>(statement.Expression);
        Assert.Equal("x" , assign.Name);
        var call = Assert.IsType<CallExpression>(assign.Value);
        Assert.Equal("add" , call.Name);
        Assert.False(call.Closed);
        Assert.Equal(2 , call.Arguments.Count);
        Assert.Equal(1L , Assert.IsType<ConstantExpression>(call.Arguments[0]).Value);
        //괄호 안의 호출은 ')' 전에 끝날수 있으므로 ParenthesisExpression으로 감쌈
        var inner = Assert.IsType<CallExpression>(Assert.IsType<ParenthesisExpression>(call.Arguments[1]).Inner);
        Assert.True(inner.Closed);
        Assert.Equal("y" , Assert.IsType<VariableExpression>(inner.Arguments[0]).Name);
    }

    //연산자 식 (1.6.0): 인터프리터와 같은 우선순위로 나뉨, 함수 호출이 먼저 묶임
    [Fact]
    public void OperatorTree()
    {
        var a = Analyze("x = f a + b * -c ?? d ? 1 : 2" , "y += 1" , "if i < length l && !done" , "end" , "return point (x + 1) y");
        var s = a.Root!.Statements;
        var conditional = Assert.IsType<ConditionalExpression>(Assert.IsType<AssignExpression>(Assert.IsType<ExpressionStatement>(s[0]).Expression).Value);
        var coalesce = Assert.IsType<BinaryExpression>(conditional.Condition);
        Assert.Equal("??" , coalesce.Operator);
        Assert.True(coalesce.IsShortCircuit);
        var plus = Assert.IsType<BinaryExpression>(coalesce.Left);
        Assert.Equal("+" , plus.Operator);
        var call = Assert.IsType<CallExpression>(plus.Left);
        Assert.Equal("f" , call.Name);
        Assert.Equal("a" , Assert.IsType<VariableExpression>(Assert.Single(call.Arguments)).Name);
        var times = Assert.IsType<BinaryExpression>(plus.Right);
        Assert.Equal("*" , times.Operator);
        var negate = Assert.IsType<UnaryExpression>(times.Right);
        Assert.Equal("-" , negate.Operator);
        Assert.Equal(1L , Assert.IsType<ConstantExpression>(conditional.WhenTrue).Value);

        var compound = Assert.IsType<CompoundAssignExpression>(Assert.IsType<ExpressionStatement>(s[1]).Expression);
        Assert.Equal(("y" , "+") , (compound.Name , compound.Operator));

        var and = Assert.IsType<BinaryExpression>(Assert.Single(s[2].Arguments));
        Assert.Equal("&&" , and.Operator);
        var less = Assert.IsType<BinaryExpression>(and.Left);
        Assert.Equal("length" , Assert.IsType<CallExpression>(less.Right).Name);
        Assert.Equal("!" , Assert.IsType<UnaryExpression>(and.Right).Operator);

        //키워드 다음의 이름도 함수 호출
        var point = Assert.IsType<CallExpression>(Assert.Single(s[3].Arguments));
        Assert.Equal(2 , point.Arguments.Count);
        Assert.IsType<BinaryExpression>(point.Arguments[0]);
    }

    [Fact]
    public void OperatorFunctionAndErrors()
    {
        var a = Analyze("struct p" , "  func ==" , "    param o" , "    return true" , "  endfunc" , "endstruct" , "x = 1 +" , "y = * 2");
        var structure = Assert.IsType<FunctionStatement>(a.Root!.Statements[0]);
        var op = Assert.IsType<FunctionStatement>(structure.Body.Statements[0]);
        Assert.Null(op.Name);
        Assert.Equal("==" , op.Operator);
        Assert.IsType<SyntaxErrorExpression>(Assert.IsType<AssignExpression>(Assert.IsType<ExpressionStatement>(a.Root.Statements[1]).Expression).Value);
        Assert.IsType<SyntaxErrorExpression>(Assert.IsType<AssignExpression>(Assert.IsType<ExpressionStatement>(a.Root.Statements[2]).Expression).Value);
    }

    [Fact]
    public void SpecialExpressions()
    {
        var b = Analyze("x =" , "5 = k" , "top:" , "(add 1");
        Assert.Empty(b.StructureErrors);
        var s = b.Root!.Statements.Cast<ExpressionStatement>().Select(o => o.Expression).ToList();
        Assert.Equal("x" , Assert.IsType<RemoveExpression>(s[0]).Name);
        Assert.Equal(5L , Assert.IsType<ConstantDefinitionExpression>(s[1]).Value);
        Assert.Equal("top" , Assert.IsType<LabelExpression>(s[2]).Name);
        Assert.IsType<UnclosedParenthesisExpression>(s[3]);
        Assert.Equal(new[] { "x" } , b.RemovedNames);
    }

    [Fact]
    public void BlocksAndStatements()
    {
        var a = Analyze(
            "if (less x 1)" ,   //0
            "  y = 1" ,         //1
            "else" ,            //2
            "  y = 2" ,         //3
            "end" ,             //4
            "func f" ,          //5
            "  param a" ,       //6
            "  param b" ,       //7
            "  repeat 3" ,      //8
            "    break" ,       //9
            "  end" ,           //10
            "  return a" ,      //11
            "endfunc" ,         //12
            "for i (array 1 2)" , //13
            "  local z = i" ,   //14
            "end");             //15
        var root = a.Root!.Statements;
        var branch = Assert.IsType<IfStatement>(root[0]);
        Assert.Equal(2 , branch.ElseLine);
        Assert.Equal(4 , branch.EndLine);
        Assert.Single(branch.Then.Statements);
        Assert.Single(branch.Else!.Statements);
        var function = Assert.IsType<FunctionStatement>(root[1]);
        Assert.Equal("f" , function.Name);
        Assert.Equal(2 , function.ParameterCount);
        Assert.Equal(12 , function.EndLine);
        var loop = Assert.IsType<LoopStatement>(function.Body.Statements[2]);
        Assert.Equal(StatementKind.Repeat , loop.Kind);
        Assert.Same(loop , Assert.IsType<BreakStatement>(loop.Body.Statements[0]).Loop);
        Assert.Same(function , loop.Function);
        var each = Assert.IsType<ForStatement>(root[2]);
        Assert.Equal("i" , each.VariableName);
        Assert.Equal("z" , Assert.IsType<LocalStatement>(each.Body.Statements[0]).Name);
    }

    [Fact]
    public void Scopes()
    {
        var a = Analyze(
            "x = 1" ,
            "func f" ,
            "  param p" ,
            "  y = 2" ,
            "  x = 3" ,
            "endfunc" ,
            "for i (array 1)" ,
            "  z = i" ,
            "end");
        Assert.Equal(new[] { "x" , "f" } , a.TopScope.NameOrder);
        Assert.Equal(NameKinds.Function , a.TopScope.Names["f"]);
        var function = a.Functions[0];
        Assert.Same(a.TopScope , function.InnerScope.Parent);
        //함수 안의 대입은 함수 영역에 만들어질수 있음 (바깥에 있으면 바깥을 바꿈)
        Assert.Equal(new[] { "p" , "y" , "x" } , function.InnerScope.NameOrder);
        var each = (ForStatement)a.Root!.Statements[2];
        Assert.Equal(ScopeKind.For , each.InnerScope.Kind);
        Assert.Equal(new[] { "i" , "z" } , each.InnerScope.NameOrder);
    }

    [Fact]
    public void GotoTargets()
    {
        var a = Analyze(
            "i = 0" ,           //0
            "top:" ,            //1
            "i = add i 1" ,     //2
            "if (less i 3)" ,   //3
            "  goto top" ,      //4
            "end" ,             //5
            "repeat 2" ,        //6
            "  goto 9" ,        //7
            "  out 1" ,         //8
            "end" ,             //9
            "goto 11");         //10
        var goto_top = Assert.Single(a.TopGotos , o => o.Line == 4);
        var target = Assert.Single(goto_top.Targets);
        Assert.Equal(GotoTargetKind.Before , target.Kind);
        Assert.Equal(1 , target.Statement!.Line);
        //repeat의 end 줄: 다음 반복
        var to_end = Assert.Single(Assert.Single(a.TopGotos , o => o.Line == 7).Targets);
        Assert.Equal(GotoTargetKind.Continue , to_end.Kind);
        //마지막 줄 다음: 스크립트 끝
        Assert.Equal(GotoTargetKind.Exit , Assert.Single(Assert.Single(a.TopGotos , o => o.Line == 10).Targets).Kind);
    }

    [Fact]
    public void NativeJsAllowsOrdinaryCode()
    {
        Assert.Empty(Check(
            "x = 0" ,
            "loop:" ,
            "x = add x 1" ,
            "if (less x 10)" ,
            "  goto loop" ,
            "end" ,
            "console.title = 'a'" ,
            "func twice" ,
            "  param n" ,
            "  return (mul n 2)" ,
            "endfunc" ,
            "return (twice x)"));
    }

    [Theory]
    [InlineData("x = 1|x =" , 1 , "삭제")]
    [InlineData("out (seen =)" , 0 , "삭제")]
    [InlineData("func add|return 1|endfunc" , 0 , "'add'")]
    [InlineData("mul = 3" , 0 , "'mul'")]
    [InlineData("local length = 1" , 0 , "'length'")]
    [InlineData("for not (array 1)|end" , 0 , "'not'")]
    [InlineData("func f|param less|endfunc" , 1 , "'less'")]
    [InlineData("5 = console.writeline" , 0 , "'console.writeline'")]
    [InlineData("true = 1" , 0 , "'true'")]
    [InlineData("math.pi:" , 0 , "'math.pi'")]
    [InlineData("goto 2|if 1|out 1|end" , 0 , "goto")]
    [InlineData("goto inside|while 1|inside:|end" , 0 , "goto")]
    public void NativeJsRestrictions(string code , int line , string word)
    {
        var errors = Check(code.Split('|'));
        var error = Assert.Single(errors);
        Assert.Equal(line , error.Line);
        Assert.StartsWith("Native JS 제약" , error.Message);
        Assert.Contains(word , error.Message);
    }

    [Fact]
    public void NativeJsReportsStructureErrors()
    {
        var error = Assert.Single(Check("if 1" , "out 1"));
        Assert.Equal(0 , error.Line);
        Assert.Null(Analyze("if 1").Root);
    }

    [Fact]
    public void RuntimeGlobals()
    {
        Runtime runtime = new(Runtime.AddJyunoCommandType.Essential);
        runtime.AddFunction("host.f" , _ => null);
        runtime.AddVariable("host.v" , () => 1L , _ => { });
        var globals = runtime.GetGlobals();
        Assert.Equal(GlobalKind.Function , globals["add"].Kind);
        Assert.True(globals["add"].Builtin);
        Assert.Equal(GlobalKind.Constant , globals["true"].Kind);
        Assert.Equal(true , globals["true"].Value);
        Assert.Equal(GlobalKind.Function , globals["host.f"].Kind);
        Assert.False(globals["host.f"].Builtin);
        Assert.Equal(GlobalKind.Variable , globals["host.v"].Kind);
        Assert.False(globals.ContainsKey("console.writeline"));
        Assert.Equal(Math.PI , new Runtime().GetGlobals()["math.pi"].Value);
    }
}
