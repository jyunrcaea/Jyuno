using Jyuno.Analysis;
using Jyuno.Language;

namespace Jyuno.Tests;

public class StructTests
{
    static readonly string[] Person = {
        "struct person",
        "  param name",
        "  param rating",
        "  func show",
        "    out (string.format '{0} rating is {1}' name rating)",
        "  endfunc",
        "  func grow",
        "    param amount",
        "    rating = add rating amount",
        "  endfunc",
        "endstruct",
    };

    static (object? Result, List<object?> Output) Run(params string[] lines) => Script.Run(Person.Concat(lines).ToArray());

    [Fact]
    public void ConsoleWrite_Example()
    {
        //사용자가 제안한 예제 그대로 (console.write는 {1}이 첫번째 값)
        StringWriter writer = new();
        TextWriter original = Console.Out;
        Console.SetOut(writer);
        try
        {
            Interpreter interpreter = new Runtime().Create(new[] {
                "struct person",
                "  //이 공간이 사실상 생성자",
                "  param name",
                "  param rating",
                "  //여기서 선언된 함수는 구조체의 함수로 특별히 인식됨.",
                "  func show",
                "    console.write \"{1} rating is {2}\" name rating",
                "  endfunc",
                "",
                "endstruct",
                "me = person \"Jyunni\" 2424",
                "me.show",
            });
            Assert.Null(interpreter.Run());
        }
        finally
        {
            Console.SetOut(original);
        }
        Assert.Equal("Jyunni rating is 2424", writer.ToString());
    }

    [Fact]
    public void Method_ReadsFields()
    {
        var (_, output) = Run("me = person 'Jyunni' 2424", "me.show");
        Assert.Equal(new object?[] { "Jyunni rating is 2424" }, output);
    }

    [Fact]
    public void Fields_AreDeclaredAsMemberNames()
    {
        var (_, output) = Run("me = person 'Jyunni' 2424", "out me.name", "out me.rating");
        Assert.Equal(new object?[] { "Jyunni", 2424L }, output);
    }

    [Fact]
    public void Method_ChangesField()
    {
        var (_, output) = Run("me = person 'Jyunni' 2424", "me.grow 100", "out me.rating", "me.show");
        Assert.Equal(new object?[] { 2524L, "Jyunni rating is 2524" }, output);
    }

    [Fact]
    public void MemberAssignment_ChangesField()
    {
        var (_, output) = Run("me = person 'Jyunni' 2424", "me.name = 'Jyuno'", "me.show");
        Assert.Equal(new object?[] { "Jyuno rating is 2424" }, output);
    }

    [Fact]
    public void Creating_OverwritesExistingMemberName()
    {
        var (_, output) = Run(
            "func me.show",
            "  out 'old'",
            "endfunc",
            "me.show",
            "me = person 'Jyunni' 2424",
            "me.show");
        Assert.Equal(new object?[] { "old", "Jyunni rating is 2424" }, output);
    }

    [Fact]
    public void Copy_IsDeep()
    {
        var (_, output) = Run(
            "a = person 'Jyunni' 2424",
            "b = a",
            "b.name = 'Jyuno'",
            "b.grow 1",
            "a.show",
            "b.show");
        Assert.Equal(new object?[] { "Jyunni rating is 2424", "Jyuno rating is 2425" }, output);
    }

    [Fact]
    public void Copy_CopiesCollectionFields()
    {
        var (_, output) = Run(
            "struct bag",
            "  items = list 1 2",
            "endstruct",
            "a = bag",
            "b = a",
            "list.push b.items 3",
            "out (length a.items)",
            "out (length b.items)");
        Assert.Equal(new object?[] { 2L, 3L }, output);
    }

    [Fact]
    public void Assigning_ExistingStruct_ReplacesMembers()
    {
        var (_, output) = Run(
            "a = person 'A' 1",
            "b = person 'B' 2",
            "a = b",
            "a.show",
            "b.grow 10",
            "a.show",
            "b.show");
        Assert.Equal(new object?[] { "B rating is 2", "B rating is 2", "B rating is 12" }, output);
    }

    [Fact]
    public void Function_Parameter_IsCopy()
    {
        var (_, output) = Run(
            "func bump",
            "  param p",
            "  p.grow 1",
            "  p.show",
            "endfunc",
            "me = person 'Jyunni' 1",
            "bump me",
            "me.show");
        Assert.Equal(new object?[] { "Jyunni rating is 2", "Jyunni rating is 1" }, output);
    }

    [Fact]
    public void Function_CanReturnStruct()
    {
        var (_, output) = Run(
            "func make",
            "  param n",
            "  p = person n 0",
            "  p.grow 5",
            "  return p",
            "endfunc",
            "me = make 'Jyunni'",
            "me.show");
        Assert.Equal(new object?[] { "Jyunni rating is 5" }, output);
    }

    [Fact]
    public void Collection_HoldsCopies()
    {
        var (_, output) = Run(
            "me = person 'Jyunni' 1",
            "people = list me (person 'Jyuno' 2)",
            "me.grow 10",
            "for p people",
            "  p.show",
            "end",
            "first = people 0",
            "first.show");
        Assert.Equal(new object?[] { "Jyunni rating is 1", "Jyuno rating is 2", "Jyunni rating is 1" }, output);
    }

    [Fact]
    public void NestedStruct_DeclaresNestedMembers()
    {
        var (_, output) = Run(
            "struct point",
            "  param x",
            "  param y",
            "endstruct",
            "struct line",
            "  param from",
            "  param to",
            "  func width",
            "    return (sub to.x from.x)",
            "  endfunc",
            "endstruct",
            "p = point 1 2",
            "l = line p (point 4 6)",
            "p.x = 100",
            "out l.from.x",
            "out (l.width)",
            "l.to.x = 10",
            "out (l.width)",
            "l.from = (point 3 0)",
            "out l.from.x",
            "out (l.width)",
            "copy = l",
            "copy.to.x = 0",
            "out l.to.x");
        Assert.Equal(new object?[] { 1L, 3L, 9L, 3L, 7L, 10L }, output);
    }

    [Fact]
    public void Reassigning_ToOtherValue_RemovesMembers()
    {
        var (result, output) = Run(
            "me = person 'Jyunni' 1",
            "me = 5",
            "out me",
            "return (me.show =)");
        Assert.Equal(new object?[] { 5L }, output);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Removing_Variable_RemovesMembers()
    {
        var (result, _) = Run(
            "me = person 'Jyunni' 1",
            "me =",
            "return (me.name =)");
        Assert.Equal(false, result);
    }

    [Fact]
    public void StructValue_ToStringAndEquality()
    {
        var (result, output) = Run(
            "a = person 'Jyunni' 2424",
            "b = a",
            "out (string a)",
            "out (equal a b)",
            "b.grow 1",
            "out (equal a b)",
            "return a");
        Assert.Equal(new object?[] { "person { name = Jyunni, rating = 2424 }", true, false }, output);
        StructValue value = Assert.IsAssignableFrom<StructValue>(result);
        Assert.Equal("person", value.TypeName);
        Assert.Equal(new[] { "name", "rating" }, value.FieldNames);
        Assert.Equal(2424L, value["rating"]);
    }

    [Fact]
    public void Reassigning_InLoop_UpdatesMembers()
    {
        var (_, output) = Run(
            "i = 0",
            "while (less i 3)",
            "  me = person 'P' i",
            "  me.show",
            "  i = add i 1",
            "end");
        Assert.Equal(new object?[] { "P rating is 0", "P rating is 1", "P rating is 2" }, output);
    }

    [Fact]
    public void Method_CallsOtherMethod()
    {
        var (_, output) = Run(
            "struct acc",
            "  total = 0",
            "  func put",
            "    param n",
            "    total = add total n",
            "  endfunc",
            "  func twice",
            "    param n",
            "    put n",
            "    put n",
            "    return total",
            "  endfunc",
            "endstruct",
            "a = acc",
            "out (a.twice 5)",
            "out a.total");
        Assert.Equal(new object?[] { 10L, 10L }, output);
    }

    [Fact]
    public void Constructor_CanHaveCode()
    {
        var (_, output) = Run(
            "struct counter",
            "  param start",
            "  count = mul start 2",
            "  func next",
            "    count = add count 1",
            "    return count",
            "  endfunc",
            "endstruct",
            "c = counter 5",
            "out (c.next)",
            "out (c.next)",
            "out c.count");
        Assert.Equal(new object?[] { 11L, 12L, 12L }, output);
    }

    [Fact]
    public void Constructor_Return_EndsConstructor()
    {
        var (_, output) = Run(
            "struct box",
            "  param v",
            "  if (equal v null)",
            "    return 1",
            "  end",
            "  w = v",
            "endstruct",
            "a = box",
            "out (string a)",
            "b = box 3",
            "out (string b)");
        Assert.Equal(new object?[] { "box { v = null }", "box { v = 3, w = 3 }" }, output);
    }

    [Fact]
    public void StructInsideFunction_IsLocal()
    {
        var (_, output) = Run(
            "func f",
            "  local p = person 'L' 0",
            "  p.show",
            "endfunc",
            "p = person 'G' 1",
            "f",
            "p.show");
        Assert.Equal(new object?[] { "L rating is 0", "G rating is 1" }, output);
    }

    [Fact]
    public void Method_SeesOuterVariables()
    {
        var (_, output) = Run(
            "bonus = 10",
            "struct item",
            "  param price",
            "  func total",
            "    return (add price bonus)",
            "  endfunc",
            "endstruct",
            "i = item 5",
            "bonus = 20",
            "out (i.total)");
        Assert.Equal(new object?[] { 25L }, output);
    }

    [Theory]
    [InlineData("struct a|endfunc")]
    [InlineData("func a|endstruct")]
    [InlineData("struct a|  param x")]
    [InlineData("struct a|  if 1|endstruct|a")]
    [InlineData("repeat 2|  struct a|    break|  endstruct|  a|end")]
    public void BlockErrors(string script)
    {
        var (result, _) = Script.Run(script.Split('|'));
        Assert.IsType<GrammarError>(result);
    }

    [Fact]
    public void Analysis_RecognizesStruct()
    {
        ScriptAnalysis analysis = ScriptAnalysis.Analyze(Person.Concat(new[] { "me = person 'a' 1" }).ToArray());
        Assert.Empty(analysis.StructureErrors);
        FunctionStatement statement = Assert.IsType<FunctionStatement>(analysis.Root!.Statements[0]);
        Assert.Equal(StatementKind.Struct, statement.Kind);
        Assert.Equal("person", statement.Name);
        Assert.Equal(2, statement.ParameterCount);
        Assert.Equal(NameKinds.Struct, analysis.TopScope.Names["person"]);
        Assert.Equal(ScopeKind.Struct, statement.InnerScope.Kind);
        Assert.Contains(analysis.CheckNativeJs(new Runtime().GetGlobals()), o => o.Line == 0);
    }
}
