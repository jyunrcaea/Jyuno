using System.Globalization;
using Jyuno.Compiler;
using Jyuno.Language;

namespace Jyuno.Tests;

//1.6.2에서 고친 문제들의 회귀 테스트 (전체 코드리뷰의 H1~H3, M1~M8, L1~L9)
public class ReviewFixTests
{
    //Windows의 기본 스택(1MB)과 같은 크기의 스레드에서 실행 (스택 오버플로는 잡을수 없어서 프로세스가 끝나므로, 오류가 되는지 확인)
    static object? RunSmallStack(Interpreter interpreter) => RunOnThread(interpreter , 1024 * 1024);
    static object? RunOnThread(Interpreter interpreter , int stack)
    {
        object? result = null;
        Exception? error = null;
        Thread thread = new(() => {
            try { result = interpreter.Run(); }
            catch (Exception e) { error = e; }
        } , stack);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)) , "스크립트가 끝나지 않습니다.");
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    static Interpreter Create(params string[] lines) => new Runtime().Create(lines);

    static string Nest(int depth , string open , string inner , string close)
        => string.Concat(Enumerable.Repeat(open , depth)) + inner + string.Concat(Enumerable.Repeat(close , depth));

    //──────── H1: 자기 자신을 담은 리스트 ────────

    [Fact]
    public void CyclicListField_CanBeCompared()
    {
        Assert.Equal(true , RunSmallStack(Create(
            "struct s" , "param items" , "endstruct" , "l = list" , "list.push l l" , "a = s l" , "b = a" , "return (equal a b)")));
    }

    [Fact]
    public void CyclicList_ToString()
    {
        Assert.Equal("[1, [...]]" , RunSmallStack(Create("l = list" , "list.push l 1" , "list.push l l" , "return (string l)")));
    }

    [Fact]
    public void DeeplyNestedList_ThrowsInsteadOfStackOverflow()
    {
        var error = Assert.Throws<JyunoException>(() => RunSmallStack(Create(
            "l = list" , "repeat 200000" , "m = list l" , "l = m" , "end" , "return (string l)")));
        Assert.Equal(5 , error.Line);
    }

    //──────── H2: 한 줄의 깊은 식 ────────

    [Theory]
    [InlineData("parens")]
    [InlineData("unary")]
    [InlineData("coalesce")]
    [InlineData("conditional")]
    [InlineData("calls")]
    public void DeepExpression_IsErrorInsteadOfStackOverflow(string kind)
    {
        string expression = kind switch {
            "parens" => Nest(20000 , "(" , "1" , ")"),
            "unary" => string.Concat(Enumerable.Repeat("- " , 200000)) + "1",
            "coalesce" => string.Join(" ?? " , Enumerable.Repeat("null" , 100000)),
            "conditional" => string.Concat(Enumerable.Repeat("false ? 1 : " , 50000)) + "2",
            _ => Nest(20000 , "add 1 (" , "1" , ")"),
        };
        var error = Assert.Throws<JyunoException>(() => RunSmallStack(Create("x = 1" , "return " + expression)));
        Assert.Contains("너무 깊게" , error.Message);
        Assert.Equal(1 , error.Line);
    }

    [Fact]
    public void AllowedExpressionDepth_RunsInsideDeepRecursion()
    {
        //허용되는 깊이의 식은 함수 호출 깊이가 최대일 때도 1MB 스택에서 계산할수 있어야 함
        Assert.Equal(-1L , RunSmallStack(Create(
            "func f" , "param n" , "if (n > 0)" , "return (f (n - 1))" , "end" , "return " + string.Concat(Enumerable.Repeat("- " , 591)) + "1" , "endfunc" , "return (f 198)")));
        Assert.Equal(141L , RunSmallStack(Create(
            "func f" , "param n" , "if (n > 0)" , "return (f (n - 1))" , "end" , "return " + Nest(140 , "add 1 (" , "1" , ")") , "endfunc" , "return (f 198)")));
    }

    //──────── H3: 실행 중단, 큰 할당 ────────

    [Fact]
    public void CancellationToken_StopsInfiniteLoop()
    {
        Interpreter interpreter = Create("i = 0" , "while true" , "i = i + 1" , "end");
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(200));
        interpreter.CancellationToken = cancel.Token;
        Task<object?> task = Task.Run(() => interpreter.Run());
        Assert.True(Task.WhenAny(task , Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult() == task , "취소되지 않습니다.");
        var error = Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        Assert.Equal(cancel.Token , error.CancellationToken);
        //다시 실행할수 있음
        interpreter.CancellationToken = default;
        Assert.Equal(3L , interpreter.ExecuteLine("(add 1 2)"));
    }

    [Fact]
    public void HugeStringRepeat_IsErrorInsteadOfHang()
    {
        Task<object?> task = Task.Run(() => Create("return (mul 'ab' 3000000000)").Run());
        Assert.True(Task.WhenAny(task , Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult() == task , "멈췄습니다.");
        Assert.Throws<JyunoException>(() => task.GetAwaiter().GetResult());
        Assert.Equal("ababab" , Script.Eval("mul 'ab' 3"));
    }

    //──────── M2: 깊게 중첩된 구조체 ────────

    [Fact]
    public void DeeplyNestedStruct_IsFastAndReadable()
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        object? result = RunSmallStack(Create(
            "struct node" , "param inner" , "param v" , "endstruct" ,
            "a = node null 0" , "i = 1" , "while (i <= 600)" , "a = node a i" , "i = i + 1" , "end" ,
            "b = a" , "b.inner.inner.v = 77" ,
            "return (list (equal a b) a.inner.inner.v b.inner.inner.v a.inner.inner.inner.v)"));
        watch.Stop();
        Assert.Equal(new List<object?> { false , 598L , 77L , 597L } , (List<object?>)result!);
        //1.6.1에서는 깊이 400에서 1분이 넘게 걸렸음
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20) , $"너무 느립니다: {watch.Elapsed}");
    }

    [Fact]
    public void MemberNames_FollowLatestValue()
    {
        var (result , output) = Script.Run(
            "struct pt" , "param x" , "param y" , "endstruct" ,
            "struct box" , "param p" , "endstruct" ,
            "b = box (pt 1 2)" , "out b.p.x" ,
            "b.p = pt 10 20" , "out b.p.x" ,
            "b.p.x = 5" , "out (string b)" ,
            "b.p =" , "out b.p.y" ,
            "b = 3" , "return (b.p.x =)");
        Assert.Equal(new object?[] { 1L , 10L , "box { p = pt { x = 5, y = 20 } }" , 20L } , output);
        Assert.Equal(false , result);
    }

    //──────── M3, M4: 실행 도중에 프로그램이 Run/ExecuteLine을 호출 ────────

    [Fact]
    public void HostCallsRunDuringFunction_ReturnsToCaller()
    {
        Runtime runtime = new();
        Interpreter interpreter = null!;
        List<object?> output = new();
        runtime.AddFunction("out" , args => { output.Add(args[0]); return null; });
        runtime.AddFunction("rerun" , args => interpreter.Run((int)(long)args[0]!));
        interpreter = runtime.Create(new[] {
            "func f" , "param p" , "rerun 6" , "out p" , "out 'after'" , "endfunc" ,
            "return 0" , "x = 1" });
        interpreter.Run();
        interpreter.ExecuteLine("f 5");
        Assert.Equal(new object?[] { 5L , "after" } , output);
        Assert.Equal(-1 , interpreter.CurrentExecuteLine);
    }

    [Fact]
    public void HostExecuteLineDuringRun_RunsOnce()
    {
        Runtime runtime = new();
        Interpreter interpreter = null!;
        List<object?> output = new();
        runtime.AddFunction("out" , args => { output.Add(args[0]); return null; });
        runtime.AddFunction("eval" , args => interpreter.ExecuteLine((string)args[0]!));
        interpreter = runtime.Create(new[] { "eval \"out 'once'\"" , "out 'end'" });
        interpreter.Run();
        Assert.Equal(new object?[] { "once" , "end" } , output);
    }

    //──────── M5: .NET 예외 대신 Jyuno 오류 ────────

    [Theory]
    [InlineData("sub 'abc' 1")]
    [InlineData("console.write 1 2")]
    [InlineData("console.writeline '{5}' 1")]
    [InlineData("math.abs 'a'")]
    [InlineData("math.abs -9223372036854775808")]
    [InlineData("add true 1")]
    [InlineData("add null 1")]
    [InlineData("mul 1 'a'")]
    [InlineData("div -9223372036854775808 -1")]
    [InlineData("true + 1")]
    [InlineData("x = null\nx += 1")]
    public void WrongArguments_ThrowJyunoException(string script)
    {
        Assert.Throws<JyunoException>(() => Create(script.Split('\n')).Run());
    }

    [Fact]
    public void Arithmetic_KeepsWorkingForOtherNumberTypes()
    {
        Runtime runtime = new();
        runtime.AddVariable("i32" , () => 7 , _ => { });
        runtime.AddVariable("dec" , () => 1.5m , _ => { });
        Interpreter interpreter = runtime.Create(new[] { "return (list (add i32 1) (mul dec 2) (math.abs (sub 0 i32)) (mod -9223372036854775808 -1) (sub 'abc' ''))" });
        Assert.Equal(new List<object?> { 8L , 3.0m , 7L , 0L , "abc" } , (List<object?>)interpreter.Run()!);
    }

    //──────── M6: 오류가 난 줄 ────────

    [Fact]
    public void RuntimeError_HasLine()
    {
        var error = Assert.Throws<JyunoException>(() => Create("x = 1" , "func f" , "return (div 1 0)" , "endfunc" , "y = f").Run());
        Assert.Equal(2 , error.Line);
    }

    [Fact]
    public void HostException_LineIsInLastErrorLine()
    {
        Runtime runtime = new();
        runtime.AddFunction("boom" , args => throw new InvalidOperationException("host"));
        Interpreter interpreter = runtime.Create(new[] { "x = 1" , "" , "boom" });
        Assert.Throws<InvalidOperationException>(() => interpreter.Run());
        Assert.Equal(2 , interpreter.LastErrorLine);
    }

    //──────── M7: 필드 순서와 비교 ────────

    [Fact]
    public void FieldOrder_IsCreationOrder_AndEqualityIgnoresIt()
    {
        var (result , output) = Script.Run(
            "struct s" , "param first" , "a = 1" , "tmp = 0" , "tmp =" , "if first" , "c = 3" , "d = 4" , "else" , "d = 4" , "c = 3" , "end" , "endstruct" ,
            "x = s true" , "y = s false" , "out (string x)" , "out (string y)" , "return (equal (s false) (s false) (s true))");
        Assert.Equal(new object?[] { "s { first = True, a = 1, c = 3, d = 4 }" , "s { first = False, a = 1, d = 4, c = 3 }" } , output);
        Assert.Equal(false , result);
        var (same , _) = Script.Run(
            "struct s" , "param first" , "if first" , "c = 3" , "d = 4" , "else" , "d = 4" , "c = 3" , "end" , "first = 0" , "endstruct" ,
            "return (equal (s true) (s false))");
        Assert.Equal(true , same);
    }

    //──────── M1, M8: 비교 연산자 함수 ────────

    [Fact]
    public void LessGreater_CallOneOperator()
    {
        var (_ , output) = Script.Run(
            "struct s" , "param v" , "func >" , "param o" , "out 'gt'" , "return false" , "endfunc" , "func <" , "param o" , "out 'lt'" , "return false" , "endfunc" , "endstruct" ,
            "p = s 1" , "out (less 1 p)" , "out (greater 1 p)" , "out (1 < p)" , "out (less p 1)");
        Assert.Equal(new object?[] { "gt" , false , "lt" , false , "gt" , false , "lt" , false } , output);
    }

    [Theory]
    [InlineData(false , false)]
    [InlineData(true , false)]
    [InlineData(false , true)]
    [InlineData(true , true)]
    public void ComparisonWithSideEffects_SameInAllConfigurations(bool escape , bool inline)
    {
        Interpreter interpreter = Create(
            "struct inner" , "param v" , "func ==" , "param o" , "g.tail = 99" , "return true" , "endfunc" , "endstruct" ,
            "struct outer" , "param head" , "param tail" , "endstruct" ,
            "struct S" , "param a" , "func <" , "param o" , "return a != 1" , "endfunc" , "func >" , "param o" , "a = 3" , "return false" , "endfunc" , "endstruct" ,
            "g = outer (inner 1) 5" , "h = outer (inner 1) 5" ,
            "return (list (equal g h) g.tail (greater 0 (S 1)))");
        interpreter.EscapeAnalysis = escape;
        interpreter.Inlining = inline;
        Assert.Equal(new List<object?> { true , 99L , false } , (List<object?>)interpreter.Run()!);
    }

    //──────── L1: 같은 객체끼리의 비교 ────────

    [Fact]
    public void SameStructObject_ComparesFields()
    {
        Assert.Equal(new List<bool> { false , false , false } , (List<bool>)Create(
            "struct s" , "param x" , "endstruct" , "g = s (0.0 / 0.0)" , "l = list g" ,
            "return (list (equal g g) (equal (l 0) (l 0)) (array.contains l (l 0)))").Run()!);
    }

    //──────── L2, L3, L4: 문법 ────────

    [Fact]
    public void TrailingTokens_AreError()
    {
        var error = Assert.Throws<JyunoException>(() => Create("x = 5 -3").Run());
        Assert.Contains("-3" , error.Message);
        Assert.Throws<JyunoException>(() => Create("w = 1 2 3").Run());
        Assert.Equal(2L , Create("x = 5 - 3" , "return x").Run());
        Assert.Throws<JyunoException>(() => Create("func f" , "local y = 5 -3" , "return y" , "endfunc" , "return (f)").Run());
    }

    //오류 메시지의 값은 문화권과 관계없이, 배열/리스트는 [1, 2] 형식
    [Fact]
    public void ErrorMessages_ShowValues()
    {
        Assert.Contains("'[1]'" , Assert.Throws<JyunoException>(() => Create("x = (list 1) < 2").Run()).Message);
        Assert.Contains("'1.5'" , Assert.Throws<JyunoException>(() => Create("x = list 1" , "y = x 1.5").Run()).Message);
        Assert.Contains("'True'" , Assert.Throws<JyunoException>(() => Create("for i true" , "end").Run()).Message);
    }

    //정적 분석(Native JS, LSP)도 인터프리터와 같은 문법 오류
    [Fact]
    public void Analysis_ReportsSameSyntaxErrors()
    {
        static string? error(string line)
        {
            var statement = Assert.IsType<Jyuno.Analysis.ExpressionStatement>(Assert.Single(Jyuno.Analysis.ScriptAnalysis.Analyze(new[] { line }).Root!.Statements));
            var expression = statement.Expression is Jyuno.Analysis.AssignExpression assign ? assign.Value : statement.Expression;
            return ((statement.Expression as Jyuno.Analysis.SyntaxErrorExpression) ?? (expression as Jyuno.Analysis.SyntaxErrorExpression))?.Message;
        }
        Assert.Contains("-3" , error("x = 5 -3"));
        Assert.NotNull(error("w = 1 2 3"));
        Assert.Null(error("x = 5 - 3"));
        string deep = "x = " + string.Concat(Enumerable.Repeat("(" , 2000)) + "1" + string.Concat(Enumerable.Repeat(")" , 2000));
        Assert.Contains("깊게 중첩" , error(deep));
        Assert.Null(error("x = " + string.Concat(Enumerable.Repeat("(" , 50)) + "1" + string.Concat(Enumerable.Repeat(")" , 50))));
    }

    [Fact]
    public void Tab_IsWhitespace()
    {
        Assert.Equal(6L , Create("x\t=\t5" , "return\t(add\tx\t1)").Run());
        Assert.Empty(Parser.Checker(new[] { "x\t=\t1\t+\t2" }));
    }

    [Fact]
    public void Checker_KnowsOperators()
    {
        Assert.Empty(Parser.Checker(new[] { "x = c ? 1 : 2" , "y = (a ?? 1) + -2" }));
        Assert.Single(Parser.Checker(new[] { "y = 1 +" }));
        Assert.Single(Parser.Checker(new[] { "5:" }));
    }

    //──────── L6: 배열/리스트의 문자열 ────────

    [Fact]
    public void List_ToString()
    {
        Assert.Equal("[1, a, null, 1.5, True, [2]]" , Script.Eval("string (list 1 'a' null 1.5 true (array 2))"));
        Assert.Equal("[]x" , Script.Eval("add '' (list) 'x'"));
    }

    [Fact]
    public void Falsy_IncludesEmptyString()
    {
        Assert.Equal(false , Parser.IsTrue(""));
        Assert.Equal(false , Parser.IsTrue(0.0));
    }

    //──────── L9: 호출 깊이 ────────

    [Fact]
    public void CallDepthLimit_CanBeChanged()
    {
        string[] lines = { "func f" , "param n" , "if (n > 0)" , "return (add 1 (f (n - 1)))" , "end" , "return 0" , "endfunc" , "return (f 500)" };
        Assert.Throws<JyunoException>(() => Create(lines).Run());
        Interpreter interpreter = Create(lines);
        interpreter.CallDepthLimit = 1000;
        Assert.Equal(500L , RunOnThread(interpreter , 16 * 1024 * 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => interpreter.CallDepthLimit = 0);
    }
}

//Console을 바꾸는 테스트는 같은 컬렉션에서 차례대로 실행
[Collection("Console")]
public class ReviewFixConsoleTests
{
    static string RunConsole(string input , params string[] lines)
    {
        StringWriter writer = new();
        TextWriter original = Console.Out;
        TextReader original_in = Console.In;
        Console.SetOut(writer);
        Console.SetIn(new StringReader(input));
        try
        {
            object? result = new Runtime().Create(lines).Run();
            return writer.ToString().Replace("\r\n" , "\n") + "=> " + Show(result);
        }
        finally
        {
            Console.SetOut(original);
            Console.SetIn(original_in);
        }
    }

    //Jyuno의 string 함수와 같은 문자열 (null은 "null")
    static string Show(object? value)
    {
        if (value is null)
            return "null";
        Runtime runtime = new();
        runtime.AddVariable("value" , () => value , _ => { });
        return (string)runtime.Create().ExecuteLine("(string value)")!;
    }

    [Fact]
    public void ConsoleRead_ReturnsString()
    {
        Assert.Equal("=> [True, b, null]" , RunConsole("ab" , "a = console.read" , "b = console.read" , "c = console.read" , "return (list (equal a 'a') b c)"));
    }

    [Fact]
    public void ConsoleWrite_IgnoresCulture()
    {
        CultureInfo culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal("1.5|1.5 2.5\n[1.5]\n=> null" , RunConsole("" , "console.write 1.5" , "console.write '|{2} {3}' '' 1.5 2.5" , "console.writeline ''" , "console.writeline (list 1.5)"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
