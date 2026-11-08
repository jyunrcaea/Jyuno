namespace Jyuno.Tests;

public class CollectionTests
{
    [Fact]
    public void Div_String_Splits()
    {
        Assert.Equal(new[] { "a", "b", "", "c" }, Script.Eval("div 'a,b,,c' ','"));
        Assert.Equal(new[] { "1", "2", "3" }, Script.Eval("div '1<>2<>3' '<>'"));
        Assert.Equal(new[] { "abc" }, Script.Eval("div 'abc' ','"));
    }

    [Fact]
    public void Div_String_NullSeparator_Throws()
    {
        Assert.Throws<JyunoException>(() => Script.Eval("div 'a' null"));
    }

    [Fact]
    public void Array_InfersElementType()
    {
        Assert.Equal(new long[] { 1, 2, 3 }, Script.Eval("array 1 2 3"));
        Assert.Equal(new[] { "a", "b" }, Script.Eval("array 'a' 'b'"));
        Assert.Equal(new double[] { 1.5 }, Script.Eval("array 1.5"));
        Assert.Equal(new object?[] { 1L, "a", null }, Script.Eval("array 1 'a' null"));
        Assert.Equal(Array.Empty<object?>(), Script.Eval("array"));
    }

    [Fact]
    public void List_InfersElementType()
    {
        Assert.Equal(new List<long> { 1, 2, 3 }, Script.Eval("list 1 2 3"));
        Assert.Equal(new List<string> { "a" }, Script.Eval("list 'a'"));
        Assert.Equal(new List<object?> { 1L, 2.5 }, Script.Eval("list 1 2.5"));
        Assert.IsType<List<object?>>(Script.Eval("list"));
    }

    [Fact]
    public void Index_ReadAndWrite()
    {
        var (_, output) = Script.Run(
            "l = array 10 20 30",
            "out (l 0)",
            "l 0 (l 2)",
            "out (l 0)",
            "out l",
            "out (l)");
        Assert.Equal(10L, output[0]);
        Assert.Equal(30L, output[1]);
        Assert.Equal(new long[] { 30, 20, 30 }, output[2]);
        Assert.Equal(new long[] { 30, 20, 30 }, output[3]);
    }

    [Fact]
    public void Index_List_Write()
    {
        var (result, _) = Script.Run(
            "l = list 'a' 'b'",
            "l 1 'c'",
            "return l");
        Assert.Equal(new List<string> { "a", "c" }, result);
    }

    [Fact]
    public void Index_ReturnValue()
    {
        var (result, _) = Script.Run(
            "l = list 1 2 3",
            "return (add (l 0) (l 1) (l 2))");
        Assert.Equal(6L, result);
    }

    [Fact]
    public void Index_WriteNumber_IsConverted()
    {
        var (result, _) = Script.Run(
            "l = array 1.5 2.5",
            "l 0 3",
            "return l");
        Assert.Equal(new double[] { 3.0, 2.5 }, result);
    }

    [Fact]
    public void Index_ObjectArray_AcceptsAnyValue()
    {
        var (result, _) = Script.Run(
            "l = array 1 'a'",
            "l 0 (array 2)",
            "return (l 0)");
        Assert.Equal(new long[] { 2 }, result);
    }

    [Theory]
    [InlineData("l 3")]
    [InlineData("l -1")]
    [InlineData("l 'a'")]
    [InlineData("l 1.0")]
    [InlineData("l 0 'text'")]
    [InlineData("l 0 1 2")]
    public void Index_Invalid_Throws(string line)
    {
        Assert.Throws<JyunoException>(() => Script.Run("l = array 1 2 3", line));
    }

    [Fact]
    public void Index_Write_BlockedBySubstitute()
    {
        Interpreter interpreter = new Runtime().Create(new[] { "l = array 1 2" });
        interpreter.Run();
        interpreter.BlockSubstitute = true;
        Assert.Throws<JyunoException>(() => interpreter.ExecuteLine("l 0 5"));
        Assert.Equal(1L, interpreter.ExecuteLine("l 0"));
    }

    [Fact]
    public void Index_SplitResult()
    {
        var (result, _) = Script.Run(
            "words = div 'hello jyuno world' ' '",
            "return (words 1)");
        Assert.Equal("jyuno", result);
    }

    [Fact]
    public void Add_Collections_SpreadOnce()
    {
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, Script.Eval("add (array 1 2) (array 3 4) (array 5 6)"));
        Assert.Equal(new List<long> { 1, 2, 3, 4 }, Script.Eval("add (list 1) 2 (array 3 4)"));
        Assert.Equal(new long[] { 1, 2 }, Script.Eval("add (array 1 2)"));
    }

    [Fact]
    public void Add_NestedCollection_IsNotSpreadTwice()
    {
        var result = Assert.IsType<object?[]>(Script.Eval("add (array 1) (array 2 (array 3 4))"));
        Assert.Equal(3, result.Length);
        Assert.Equal(1L, result[0]);
        Assert.Equal(2L, result[1]);
        Assert.Equal(new long[] { 3, 4 }, result[2]);
    }

    [Fact]
    public void Add_DifferentType_WidensToObject()
    {
        Assert.Equal(new object?[] { 1L, 2L, "a" }, Script.Eval("add (array 1 2) 'a'"));
        Assert.Equal(new List<object?> { 1L, 2.5 }, Script.Eval("add (list 1) 2.5"));
        //실수 배열에 정수를 더하면 실수로 변환
        Assert.Equal(new double[] { 1.5, 2.0 }, Script.Eval("add (array 1.5) 2"));
    }

    [Fact]
    public void Add_DoesNotChangeOriginal()
    {
        var (_, output) = Script.Run(
            "a = list 1 2",
            "b = add a (list 3)",
            "out a",
            "out b");
        Assert.Equal(new List<long> { 1, 2 }, output[0]);
        Assert.Equal(new List<long> { 1, 2, 3 }, output[1]);
    }

    [Fact]
    public void Sub_Collections_SetDifference()
    {
        Assert.Equal(new long[] { 1, 3, 5 }, Script.Eval("sub (array 1 2 3 2 4 5) (array 2 4)"));
        Assert.Equal(new List<string> { "a", "c" }, Script.Eval("sub (list 'a' 'b' 'c') (array 'b' 'x')"));
        Assert.Equal(new List<long> { 1, 3 }, Script.Eval("sub (list 1 2 3) 2"));
        Assert.Equal(new long[] { 1, 2 }, Script.Eval("sub (array 1 2) (array)"));
        Assert.Equal(Array.Empty<long>(), Script.Eval("sub (array 1 2) (array 2 1)"));
    }

    [Fact]
    public void Sub_ComparesNumbersByValue()
    {
        Assert.Equal(new object?[] { "a" }, Script.Eval("sub (array 1 'a' 2.0) (array 1.0 2)"));
    }

    [Fact]
    public void Collections_FromHost_CanBeIndexedAndAdded()
    {
        Runtime runtime = new();
        int[] numbers = { 1, 2, 3 };
        runtime.AddVariable("numbers", () => numbers, _ => { });
        Interpreter interpreter = runtime.Create();
        Assert.Equal(2, interpreter.ExecuteLine("numbers 1"));
        interpreter.ExecuteLine("numbers 0 7");
        Assert.Equal(7, numbers[0]);
        Assert.Equal(new[] { 7, 2, 3, 4 }, interpreter.ExecuteLine("add numbers 4"));
        Assert.Equal(new object?[] { 7, 2, 3, "a" }, interpreter.ExecuteLine("add numbers 'a'"));
        Assert.Equal(new[] { 7, 3 }, interpreter.ExecuteLine("sub numbers (array 2)"));
    }
}
