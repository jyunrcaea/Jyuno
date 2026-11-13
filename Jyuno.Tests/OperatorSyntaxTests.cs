using Jyuno.Compiler;

namespace Jyuno.Tests;

//C 계열 연산자 (1.6.0): 우선순위, 단락 평가, 복합 대입, 예약어(대입/삭제 불가), 구조체 연산자 다시 정의
public class OperatorSyntaxTests
{
    static object? Value(string expression) => Script.Run("out (" + expression + ")").Output.Single();

    //사용자가 제안한 예제 그대로
    [Fact]
    public void Example_Success()
    {
        var (_ , output) = Script.Run(
            "struct point",
            "  param x",
            "  param y",
            "  func +",
            "    param other",
            "    return point (x + other.x) (y + other.y)",
            "  endfunc",
            "endstruct",
            "",
            "p1 = point 10 20",
            "p2 = point -5 4",
            "if p1 + p2 == (add p1 p2)",
            "  out \"success\"",
            "end");
        Assert.Equal(new object?[] { "success" } , output);
    }

    [Theory]
    //C#과 같은 우선순위
    [InlineData("1 + 2 * 3" , 7L)]
    [InlineData("(1 + 2) * 3" , 9L)]
    [InlineData("10 - 2 - 3" , 5L)]
    [InlineData("100 / 10 / 5" , 2L)]
    [InlineData("7 % 4 * 2" , 6L)]
    [InlineData("1 + 2 << 3" , 24L)]
    [InlineData("1 << 2 + 1" , 8L)]
    [InlineData("-8 >> 1" , -4L)]
    [InlineData("1 << 65" , 2L)]
    [InlineData("6 & 3 | 8" , 10L)]
    [InlineData("1 | 6 ^ 3 & 5" , 7L)]
    [InlineData("5 ^ 3" , 6L)]
    [InlineData("~5" , -6L)]
    [InlineData("-(3 + 4)" , -7L)]
    [InlineData("- -3" , 3L)]
    [InlineData("+5" , 5L)]
    [InlineData("2 * -3" , -6L)]
    [InlineData("10-3" , 7L)]
    [InlineData("10 - -3" , 13L)]
    [InlineData("7 / 2" , 3L)]
    [InlineData("-7 / 2" , -3L)]
    [InlineData("-7 % 3" , -1L)]
    [InlineData("7.0 / 2" , 3.5)]
    [InlineData("9223372036854775807 + 1" , long.MinValue)]
    [InlineData("1 < 2 == 2 < 3" , true)]
    [InlineData("1 < 2 && 3 < 2 || 1 == 1" , true)]
    [InlineData("1 == 1 && 2 != 2" , false)]
    [InlineData("1 <= 1" , true)]
    [InlineData("2 >= 3" , false)]
    [InlineData("1 == 1.0" , true)]
    [InlineData("'a' < 'b'" , true)]
    [InlineData("'a' == 'a'" , true)]
    [InlineData("true & false" , false)]
    [InlineData("true | false" , true)]
    [InlineData("true ^ true" , false)]
    [InlineData("!0" , true)]
    [InlineData("!1" , false)]
    [InlineData("!'x'" , false)]
    [InlineData("!null" , true)]
    [InlineData("5 && 'a'" , true)]
    [InlineData("0 || ''" , false)]
    [InlineData("null ?? 3" , 3L)]
    [InlineData("4 ?? 3" , 4L)]
    [InlineData("null ?? null ?? 'c'" , "c")]
    [InlineData("1 < 2 ? 'yes' : 'no'" , "yes")]
    [InlineData("0 ? 1 : 2 ? 3 : 4" , 3L)]
    [InlineData("1 + 1 == 2 ? 10 : 20" , 10L)]
    [InlineData("'a' + 1 + 2" , "a12")]
    [InlineData("1 + 2 + 'a'" , "3a")]
    [InlineData("'ab' * 3" , "ababab")]
    [InlineData("'a b c' / ' ' == null" , false)]
    [InlineData("math.nan < 1" , false)]
    [InlineData("math.nan >= 1" , false)]
    [InlineData("math.nan != math.nan" , true)]
    public void Precedence_And_Semantics_LikeCSharp(string expression , object expected)
    {
        Assert.Equal(expected , Value(expression));
    }

    [Theory]
    [InlineData("1 + 2 * 3" , 7L)]
    [InlineData("10 / 4" , 2L)]
    [InlineData("x = 1 + 2" , null)]
    public void ExecuteLine_Works(string line , object? expected)
    {
        Assert.Equal(expected , Script.Eval(line));
    }

    //함수 호출(이름 + 인자들)은 모든 연산자보다 먼저 묶임
    [Fact]
    public void Call_BindsTighterThanOperators()
    {
        var (_ , output) = Script.Run(
            "func twice",
            "  param n",
            "  return n * 2",
            "endfunc",
            "out (twice 3 + 1)",
            "out (twice (3 + 1))",
            "out (twice 3 + twice 4)",
            "out (add 1 2 * 3)",
            "l = list 10 20 30",
            "out (l 1 + l 2)",
            "out (length l - 1)");
        Assert.Equal(new object?[] { 7L , 8L , 14L , 9L , 50L , 2L } , output);
    }

    //'-' 바로 뒤에 숫자가 붙어 있으면 음수, 단 값 바로 뒤에 붙은 '-'는 뺄셈
    [Fact]
    public void Minus_NegativeLiteral_Or_Subtraction()
    {
        var (_ , output) = Script.Run(
            "x = 10",
            "out (x-1)",
            "out (x - 1)",
            "out (array -1 -2)",
            "out ((x)-1)",
            "out (x * -1)");
        Assert.Equal(10L - 1 , output[0]);
        Assert.Equal(9L , output[1]);
        Assert.Equal(new long[] { -1 , -2 } , output[2]);
        Assert.Equal(9L , output[3]);
        Assert.Equal(-10L , output[4]);
    }

    [Fact]
    public void KeywordArguments_AreExpressions()
    {
        var (result , output) = Script.Run(
            "i = 0",
            "while i < 3",
            "  i += 1",
            "end",
            "out i",
            "if i == 3 && i > 2",
            "  out 'ok'",
            "end",
            "repeat 1 + 1",
            "  out 'r'",
            "end",
            "for x array 1 2",
            "  out (x * 10)",
            "end",
            "func five",
            "  return 5",
            "endfunc",
            "func g",
            "  return five + 1",
            "endfunc",
            "out (g)",
            "return i * 100");
        Assert.Equal(new object?[] { 3L , "ok" , "r" , "r" , 10L , 20L , 6L } , output);
        Assert.Equal(300L , result);
    }

    [Fact]
    public void ShortCircuit_SkipsRightSide()
    {
        var (_ , output) = Script.Run(
            "out (false && (out 'and'))",
            "out (true || (out 'or'))",
            "out (1 ?? (out 'coalesce'))",
            "out (true ? 'a' : (out 'else'))",
            "out (false ? (out 'then') : 'b')",
            "out (true && (out 'evaluated') == null)");
        Assert.Equal(new object?[] { false , true , 1L , "a" , "b" , "evaluated" , true } , output);
    }

    [Fact]
    public void CompoundAssignment()
    {
        var (_ , output) = Script.Run(
            "x = 10",
            "x += 5",
            "out x",
            "x -= 3",
            "x *= 2",
            "x /= 4",
            "x %= 4",
            "out x",
            "x <<= 4",
            "x >>= 1",
            "x |= 1",
            "x &= 13",
            "x ^= 3",
            "out x",
            "s = 'a'",
            "s += 'b' + 'c'",
            "out s",
            "n = null",
            "n ??= 'default'",
            "out n",
            "l = list 1",
            "l += 2",
            "out (length l)");
        Assert.Equal(new object?[] { 15L , 2L , 2L , "abc" , "default" , 2L } , output);
    }

    [Fact]
    public void CompoundAssignment_OnMissingVariable_IsError()
    {
        Assert.Throws<JyunoException>(() => Script.Run("nothing += 1"));
    }

    //연산자는 이름이 아니므로 대입, 삭제, 다시 정의할수 없음 (add 등 내장 함수를 다시 정의해도 연산자는 그대로)
    [Fact]
    public void Operators_AreNotNames()
    {
        var (_ , output) = Script.Run(
            "func add",
            "  return 'redefined'",
            "endfunc",
            "out (1 + 2)",
            "out (add 1 2)",
            "add =",
            "out (add 1 2)",
            "out (5 - 1)");
        Assert.Equal(new object?[] { 3L , "redefined" , 3L , 4L } , output);
    }

    [Theory]
    [InlineData("+ = 5")]
    [InlineData("+ =")]
    [InlineData("== = 1")]
    [InlineData("local + = 1")]
    [InlineData("1 = +")]
    public void Operators_CannotBeAssignedOrDeleted(string line)
    {
        Runtime runtime = new();
        Interpreter interpreter = runtime.Create(new[] { line , "out (1 + 2)" });
        List<object?> output = new();
        runtime.AddFunction("out" , args => { output.Add(args[0]); return null; });
        try
        {
            interpreter.Run();
        }
        catch (JyunoException)
        {
        }
        //1 + 2는 그대로 계산됨 (오류가 나지 않았다면)
        Assert.All(output , value => Assert.Equal(3L , value));
    }

    [Theory]
    [InlineData("func +")]
    [InlineData("func &&")]
    public void OperatorFunction_OutsideStruct_IsError(string line)
    {
        var e = Assert.Throws<JyunoException>(() => Script.Run(line , "endfunc"));
        Assert.Contains("연산자" , e.Message);
    }

    [Fact]
    public void OperatorFunction_NotOverloadable_IsError()
    {
        var e = Assert.Throws<JyunoException>(() => Script.Run("struct s" , "  func &&" , "  endfunc" , "endstruct" , "v = s"));
        Assert.Contains("다시 정의할수 없습니다" , e.Message);
    }

    [Theory]
    [InlineData("x = 1 +" , "다음에 값이 없습니다")]
    [InlineData("x = * 2" , "앞에 값이 없습니다")]
    [InlineData("x = true ? 1" , "':'")]
    [InlineData("x = (1 + )" , "다음에 값이 없습니다")]
    [InlineData("x = ~'a'" , "정수에만")]
    [InlineData("x = -'a'" , "숫자에만")]
    [InlineData("x = 1 / 0" , "0으로 나눌수 없습니다")]
    [InlineData("x = 'a' < 1" , "비교할수 없습니다")]
    [InlineData("x = 1.5 << 1" , "정수끼리만")]
    public void SyntaxAndRuntimeErrors(string line , string message)
    {
        var e = Assert.Throws<JyunoException>(() => Script.Run(line));
        Assert.Contains(message , e.Message);
    }

    [Fact]
    public void Comments_AreIgnored()
    {
        var (_ , output) = Script.Run(
            "// 주석",
            "  // 들여쓴 주석",
            "x = 1 + 2 // 줄 끝 주석",
            "out x",
            "out '// 문자열 안은 주석이 아님'");
        Assert.Equal(new object?[] { 3L , "// 문자열 안은 주석이 아님" } , output);
    }

    //─── 구조체 연산자 다시 정의 ───

    static readonly string[] Vector = {
        "struct vec",
        "  param x",
        "  param y",
        "  func +",
        "    param o",
        "    return vec (x + o.x) (y + o.y)",
        "  endfunc",
        "  func -",
        "    param o",
        "    if null == o",
        "      return vec (-x) (-y)",
        "    end",
        "    return vec (x - o.x) (y - o.y)",
        "  endfunc",
        "  func *",
        "    param k",
        "    return vec (x * k) (y * k)",
        "  endfunc",
        "  func ==",
        "    param o",
        "    return null != o && x == o.x && y == o.y",
        "  endfunc",
        "  func <",
        "    param o",
        "    return x * x + y * y < o.x * o.x + o.y * o.y",
        "  endfunc",
        "  func !",
        "    return x == 0 && y == 0",
        "  endfunc",
        "endstruct",
    };

    static List<object?> RunVector(params string[] lines) => Script.Run(Vector.Concat(lines).ToArray()).Output;

    [Fact]
    public void Struct_ArithmeticOperators()
    {
        var output = RunVector(
            "a = vec 1 2",
            "b = vec 10 20",
            "out (string (a + b))",
            "out (string (b - a))",
            "out (string (-a))",
            "out (string (a * 3 + b))",
            "out (string (add a b b))",
            "out (string (sub b a))",
            "out (string (mul a 2))",
            "a += b",
            "out (string a)",
            "out (string b)");
        Assert.Equal(new object?[] {
            "vec { x = 11, y = 22 }" , "vec { x = 9, y = 18 }" , "vec { x = -1, y = -2 }" , "vec { x = 13, y = 26 }" ,
            "vec { x = 21, y = 42 }" , "vec { x = 9, y = 18 }" , "vec { x = 2, y = 4 }" , "vec { x = 11, y = 22 }" , "vec { x = 10, y = 20 }" ,
        } , output);
    }

    [Fact]
    public void Struct_ComparisonOperators_AreDerived()
    {
        var output = RunVector(
            "a = vec 1 2",
            "b = vec 3 4",
            "out (a == (vec 1 2))",
            "out (a != (vec 1 2))",
            "out (a != b)",
            "out (a < b)",
            "out (a > b)",
            "out (b > a)",
            "out (a <= a)",
            "out (a >= b)",
            "out (equal a (vec 1 2))",
            "out (less a b)",
            "out (greater a b)",
            "out (!a)",
            "out (!(vec 0 0))",
            "out (not (vec 0 0))",
            "out (a == null)",
            "out (null == a)");
        Assert.Equal(new object?[] { true , false , true , true , false , true , true , false , true , true , false , false , true , true , false , false } , output);
    }

    [Fact]
    public void Struct_CollectionsUseOverloads()
    {
        var output = RunVector(
            "l = list (vec 3 4) (vec 1 0) (vec 0 2)",
            "array.sort l",
            "out (string (l 0))",
            "out (string (array.max l))",
            "out (array.contains l (vec 0 2))",
            "out (array.indexof l (vec 3 4))");
        Assert.Equal(new object?[] { "vec { x = 1, y = 0 }" , "vec { x = 3, y = 4 }" , true , 2L } , output);
    }

    //연산자 함수는 왼쪽 값의 복사본에서 실행되므로 필드를 바꿔도 원래 값은 그대로
    [Fact]
    public void Struct_OperatorRunsOnCopy()
    {
        var (_ , output) = Script.Run(
            "struct counter",
            "  param n",
            "  func +",
            "    param o",
            "    n = n + o",
            "    return n",
            "  endfunc",
            "endstruct",
            "c = counter 1",
            "out (c + 10)",
            "out (c + 10)",
            "out c.n",
            "l = list (counter 5)",
            "out ((l 0) + 1)",
            "out (string (l 0))");
        Assert.Equal(new object?[] { 11L , 11L , 1L , 6L , "counter { n = 5 }" } , output);
    }

    //다시 정의하지 않은 연산자: ==는 필드 비교, 산술은 원래 내장 함수와 같은 오류
    [Fact]
    public void Struct_WithoutOverloads()
    {
        var (_ , output) = Script.Run(
            "struct pt",
            "  param x",
            "endstruct",
            "out ((pt 1) == (pt 1))",
            "out ((pt 1) != (pt 2))",
            "out ((pt 1) ?? 0)");
        Assert.Equal(true , output[0]);
        Assert.Equal(true , output[1]);
        Assert.IsAssignableFrom<Language.StructValue>(output[2]);
        Assert.ThrowsAny<Exception>(() => Script.Run("struct pt" , "  param x" , "endstruct" , "a = (pt 1) < (pt 2)"));
    }

    //연산자 함수는 구조체 함수이므로 필드 목록에는 없음
    [Fact]
    public void Struct_OperatorIsNotAField()
    {
        var (result , _) = Script.Run(
            "struct pt",
            "  param x",
            "  func +",
            "    param o",
            "    return pt (x + o.x)",
            "  endfunc",
            "endstruct",
            "return (pt 1) + (pt 2)");
        var value = Assert.IsAssignableFrom<Language.StructValue>(result);
        Assert.Equal(new[] { "x" } , value.FieldNames);
        Assert.Equal(3L , value["x"]);
    }

    [Fact]
    public void Tokenizer_Operators()
    {
        string[] symbols(string text) => Parser.Tokenizer(text).Select(t => t.type is TokenType.Operator ? "op:" + t.value : t.type.ToString()).ToArray();
        Assert.Equal(new[] { "Name" , "op:<<=" , "Constant" } , symbols("a <<= 1"));
        Assert.Equal(new[] { "Name" , "op:==" , "Name" } , symbols("a==b"));
        Assert.Equal(new[] { "Name" , "op:!=" , "Name" , "op:&&" , "op:!" , "Name" } , symbols("a != b && !c"));
        Assert.Equal(new[] { "Name" , "op:-" , "Constant" } , symbols("a-1"));
        Assert.Equal(new[] { "Name" , "Constant" } , symbols("a -1"));
        Assert.Equal(new[] { "Name" , "op:??" , "Constant" , "op:?" , "Constant" , "Prefix" , "Constant" } , symbols("a ?? 1 ? 2 : 3"));
        Assert.Equal(new[] { "Name" , "Prefix" , "Constant" } , symbols("x = 1 // 주석"));
        Assert.Equal(new[] { "Keyword" , "op:+" } , symbols("func +"));
    }
}
