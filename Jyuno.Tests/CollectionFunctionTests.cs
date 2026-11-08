namespace Jyuno.Tests;

//array.*, list.* 명령어
public class CollectionFunctionTests
{
    [Fact]
    public void Sort_InPlace_ReturnsSameCollection()
    {
        var (result, output) = Script.Run(
            "a = array 3 1 2",
            "b = array.sort a",
            "out a",
            "return (equal (length b) 3)");
        Assert.Equal(new long[] { 1, 2, 3 }, output[0]);
        Assert.Equal(true, result);
        var (_, same) = Script.Run("a = list 'b' 'c' 'a'", "out (array.sort a)", "out a");
        Assert.Same(same[0], same[1]);
        Assert.Equal(new List<string> { "a", "b", "c" }, same[1]);
    }

    [Fact]
    public void Sort_MixedNumbers_Descending_Strings()
    {
        Assert.Equal(new object?[] { 1L, 1.5, 2L, 10L }, Script.Eval("array.sort (array 10 1.5 2 1)"));
        Assert.Equal(new long[] { 3, 2, 1 }, Script.Eval("array.sort (array 1 3 2) true"));
        Assert.Equal(new long[] { 1, 2, 3 }, Script.Eval("list.sort (array 1 3 2) false"));
        //문자열은 less와 같은 유니코드 순서
        Assert.Equal(new List<string> { "B", "a", "b" }, Script.Eval("list.sort (list 'b' 'a' 'B')"));
        Assert.Equal(Array.Empty<object?>(), Script.Eval("array.sort (array)"));
    }

    [Fact]
    public void Sort_IsStable()
    {
        //1과 1.0은 같은 값이므로 원래 순서를 유지
        Assert.Equal(new object?[] { 1.0, 1L, 2L }, Script.Eval("array.sort (array 2 1.0 1)"));
        Assert.Equal(new object?[] { 1L, 1.0, 2L }, Script.Eval("array.sort (array 2 1 1.0)"));
        Assert.Equal(new object?[] { 2L, 1L, 1.0 }, Script.Eval("array.sort (array 1 2 1.0) true"));
    }

    [Fact]
    public void Sort_NaN_GoesFirst()
    {
        var sorted = (double[])Script.Eval("array.sort (array 2.0 math.nan 1.0)")!;
        Assert.True(double.IsNaN(sorted[0]));
        Assert.Equal(new[] { 1.0, 2.0 }, sorted[1..]);
        var descending = (double[])Script.Eval("array.sort (array 2.0 math.nan 1.0) true")!;
        Assert.Equal(new[] { 2.0, 1.0 }, descending[..2]);
        Assert.True(double.IsNaN(descending[2]));
    }

    [Fact]
    public void Sort_Incomparable_Throws()
    {
        Assert.Throws<JyunoException>(() => Script.Eval("array.sort (array 1 'a')"));
        Assert.Throws<JyunoException>(() => Script.Eval("array.sort (array 1 null)"));
        Assert.Throws<JyunoException>(() => Script.Eval("array.sort 1"));
        Assert.Throws<JyunoException>(() => Script.Eval("array.sort"));
        Assert.Throws<JyunoException>(() => Script.Eval("array.sort null"));
    }

    [Fact]
    public void Sort_LargeCollection()
    {
        var (result, _) = Script.Run(
            "l = list",
            "i = 0",
            "repeat 1000",
            "list.push l (mod (mul i 7919) 1000)",
            "i = add i 1",
            "end",
            "list.sort l",
            "ok = true",
            "i = 1",
            "repeat 999",
            "if (greater (l (sub i 1)) (l i))",
            "ok = false",
            "end",
            "i = add i 1",
            "end",
            "return ok");
        Assert.Equal(true, result);
    }

    [Fact]
    public void Reverse_InPlace()
    {
        var (_, output) = Script.Run("a = array 1 2 3", "array.reverse a", "out a", "l = list 'x' 'y'", "out (list.reverse l)");
        Assert.Equal(new long[] { 3, 2, 1 }, output[0]);
        Assert.Equal(new List<string> { "y", "x" }, output[1]);
    }

    [Fact]
    public void Shuffle_KeepsElements()
    {
        var (_, output) = Script.Run("a = array 1 2 3 4 5 6 7 8 9 10", "array.shuffle a", "out a", "out (array.sort a)");
        Assert.IsType<long[]>(output[0]);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, output[1]);
        //충분히 많이 섞으면 순서가 바뀜
        bool changed = false;
        for (int i = 0; i < 20 && !changed; i++)
            changed = !new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }.SequenceEqual((long[])Script.Eval("array.shuffle (array 1 2 3 4 5 6 7 8 9 10)")!);
        Assert.True(changed);
    }

    [Fact]
    public void Contains_IndexOf()
    {
        Assert.Equal(true, Script.Eval("array.contains (array 1 2 3) 2.0"));
        Assert.Equal(false, Script.Eval("list.contains (list 'a') 'b'"));
        Assert.Equal(1L, Script.Eval("array.indexof (array 'a' 'b' 'b') 'b'"));
        Assert.Equal(-1L, Script.Eval("list.indexof (list 1 2) 3"));
        Assert.Equal(1L, Script.Eval("array.indexof (array 1 null) null"));
        Assert.Throws<JyunoException>(() => Script.Eval("array.indexof (array 1)"));
    }

    [Fact]
    public void Slice_ReturnsNewCollectionOfSameKind()
    {
        Assert.Equal(new long[] { 2, 3 }, Script.Eval("array.slice (array 1 2 3 4) 1 3"));
        Assert.Equal(new List<string> { "c" }, Script.Eval("list.slice (list 'a' 'b' 'c') 2"));
        Assert.Equal(Array.Empty<long>(), Script.Eval("array.slice (array 1 2) 2"));
        var (_, output) = Script.Run("a = array 1 2 3", "b = array.slice a 0", "b 0 9", "out (a 0)");
        Assert.Equal(1L, output[0]);
        Assert.Throws<JyunoException>(() => Script.Eval("array.slice (array 1 2) 3"));
        Assert.Throws<JyunoException>(() => Script.Eval("array.slice (array 1 2) 1 0"));
        Assert.Throws<JyunoException>(() => Script.Eval("array.slice (array 1 2) -1"));
        Assert.Throws<JyunoException>(() => Script.Eval("array.slice (array 1 2) 0 3"));
    }

    [Fact]
    public void Join()
    {
        Assert.Equal("1, 2.5, a, True, ", Script.Eval("array.join (array 1 2.5 'a' true null) ', '"));
        Assert.Equal("abc", Script.Eval("list.join (list 'a' 'b' 'c')"));
        Assert.Equal("", Script.Eval("array.join (array) '-'"));
    }

    [Fact]
    public void New_FillsWithValue()
    {
        Assert.Equal(new long[] { 0, 0, 0 }, Script.Eval("array.new 3 0"));
        Assert.Equal(new object?[] { null, null }, Script.Eval("array.new 2"));
        Assert.Equal(new List<string> { "x", "x" }, Script.Eval("list.new 2 'x'"));
        Assert.IsType<List<object?>>(Script.Eval("list.new 0 'x'"));
        Assert.Throws<JyunoException>(() => Script.Eval("array.new -1"));
        Assert.Throws<JyunoException>(() => Script.Eval("array.new 'a'"));
    }

    [Fact]
    public void List_PushPop()
    {
        var (result, output) = Script.Run(
            "l = list",
            "list.push l 1 'a'",
            "out (length l)",
            "out (list.pop l)",
            "out (list.pop l)",
            "return l");
        Assert.Equal(2L, output[0]);
        Assert.Equal("a", output[1]);
        Assert.Equal(1L, output[2]);
        Assert.Equal(new List<object?>(), result);
        Assert.Throws<JyunoException>(() => Script.Eval("list.pop (list)"));
    }

    [Fact]
    public void List_Push_FitsElementType()
    {
        Assert.Equal(new List<double> { 1.5, 2.0 }, Script.Eval("list.push (list 1.5) 2"));
        //넣을수 없는 값이 있다면 아무것도 추가하지 않음
        Runtime runtime = new();
        List<long> numbers = new() { 1, 2 };
        runtime.AddVariable("numbers", () => numbers, _ => { });
        Assert.Throws<JyunoException>(() => runtime.Create().ExecuteLine("list.push numbers 3 'x'"));
        Assert.Equal(new List<long> { 1, 2 }, numbers);
    }

    [Fact]
    public void List_InsertRemove()
    {
        var (result, output) = Script.Run(
            "l = list 'a' 'c'",
            "list.insert l 1 'b'",
            "list.insert l 3 'd'",
            "out (list.join l)",
            "out (list.removeat l 0)",
            "out (list.remove l 'c')",
            "out (list.remove l 'z')",
            "return l");
        Assert.Equal("abcd", output[0]);
        Assert.Equal("a", output[1]);
        Assert.Equal(true, output[2]);
        Assert.Equal(false, output[3]);
        Assert.Equal(new List<string> { "b", "d" }, result);
        Assert.Throws<JyunoException>(() => Script.Eval("list.insert (list 1) 2 3"));
        Assert.Throws<JyunoException>(() => Script.Eval("list.removeat (list 1) 1"));
        Assert.Throws<JyunoException>(() => Script.Eval("list.removeat (list) 0"));
    }

    [Fact]
    public void List_Clear()
    {
        Assert.Equal(new List<long>(), Script.Eval("list.clear (list 1 2)"));
    }

    [Fact]
    public void List_ResizingArray_Throws()
    {
        Assert.Throws<JyunoException>(() => Script.Eval("list.push (array 1) 2"));
        Assert.Throws<JyunoException>(() => Script.Eval("list.pop (array 1)"));
        Assert.Throws<JyunoException>(() => Script.Eval("list.clear (array 1)"));
    }

    [Fact]
    public void ProgramCollections_Work()
    {
        Runtime runtime = new();
        List<int> numbers = new() { 3, 1, 2 };
        runtime.AddVariable("numbers", () => numbers, _ => { });
        runtime.Create(new[] { "list.sort numbers", "list.push numbers 10" }).Run();
        Assert.Equal(new List<int> { 1, 2, 3, 10 }, numbers);
        var readOnly = new System.Collections.ObjectModel.ReadOnlyCollection<long>(new long[] { 2, 1 });
        runtime.AddVariable("fixed", () => readOnly, _ => { });
        Assert.Throws<JyunoException>(() => runtime.Create().ExecuteLine("array.sort fixed"));
        Assert.Equal(true, runtime.Create().ExecuteLine("array.contains fixed 1"));
    }

    [Fact]
    public void IsEssential()
    {
        var globals = new Runtime(Runtime.AddJyunoCommandType.Essential).GetGlobals();
        foreach (string name in new[] { "array.sort", "list.sort", "array.shuffle", "list.push", "list.pop", "array.new" })
        {
            Assert.True(globals.ContainsKey(name), name);
            Assert.True(globals[name].Builtin, name);
        }
    }
}
