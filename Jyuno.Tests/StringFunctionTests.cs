namespace Jyuno.Tests;

//string.* 명령어
public class StringFunctionTests
{
    [Fact]
    public void Concat_Join_Format()
    {
        Assert.Equal("a1True2.5", Script.Eval("string.concat 'a' 1 null true 2.5"));
        Assert.Equal("", Script.Eval("string.concat"));
        Assert.Equal("1-2-3", Script.Eval("string.join '-' 1 2 3"));
        Assert.Equal("a, b", Script.Eval("string.join ', ' (list 'a' 'b')"));
        Assert.Equal("ab", Script.Eval("string.join null 'a' 'b'"));
        Assert.Equal("System.Int64[]x", Script.Eval("string.join '' (array 1) 'x'"));
        Assert.Equal("1 + 2.5 = 3.5, True", Script.Eval("string.format '{0} + {1} = {2}, {3}' 1 2.5 3.5 true"));
        Assert.Equal("[  7] 3.14 00042", Script.Eval("string.format '[{0,3}] {1:F2} {2:D5}' 7 3.14159 42"));
        Assert.Equal("{x}", Script.Eval("string.format '{{x}}'"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.format '{1}' 'a'"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.format 1"));
    }

    [Fact]
    public void Compare_Equals()
    {
        Assert.Equal(-1L, Script.Eval("string.compare 'B' 'a'"));
        Assert.Equal(1L, Script.Eval("string.compare 'b' 'a'"));
        Assert.Equal(0L, Script.Eval("string.compare 'a' 'a'"));
        Assert.Equal(-1L, Script.Eval("string.compare null 'a'"));
        Assert.Equal(true, Script.Eval("string.equals 'Jyuno' 'JYUNO' true"));
        Assert.Equal(false, Script.Eval("string.equals 'Jyuno' 'JYUNO'"));
        Assert.Equal(true, Script.Eval("string.equals null null"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.equals 1 1"));
    }

    [Fact]
    public void IsNullOrEmpty_WhiteSpace()
    {
        Assert.Equal(true, Script.Eval("string.isnullorempty null"));
        Assert.Equal(true, Script.Eval("string.isnullorempty ''"));
        Assert.Equal(false, Script.Eval("string.isnullorempty ' '"));
        Assert.Equal(true, Script.Eval("string.isnullorwhitespace ' \n\t'"));
        Assert.Equal(false, Script.Eval("string.isnullorwhitespace ' a'"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.isnullorempty 0"));
    }

    [Fact]
    public void Search()
    {
        Assert.Equal(2L, Script.Eval("string.indexof 'abcabc' 'c'"));
        Assert.Equal(5L, Script.Eval("string.indexof 'abcabc' 'c' 3"));
        Assert.Equal(-1L, Script.Eval("string.indexof 'abc' 'd'"));
        Assert.Equal(3L, Script.Eval("string.indexof 'abc' '' 3"));
        Assert.Equal(5L, Script.Eval("string.lastindexof 'abcabc' 'c'"));
        Assert.Equal(3L, Script.Eval("string.lastindexof 'abc' ''"));
        Assert.Equal(true, Script.Eval("string.contains 'jyuno' 'un'"));
        Assert.Equal(false, Script.Eval("string.contains 'jyuno' 'UN'"));
        Assert.Equal(true, Script.Eval("string.startswith 'jyuno' 'jy'"));
        Assert.Equal(true, Script.Eval("string.endswith 'jyuno' 'no'"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.indexof 'abc' 'a' 4"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.contains 'abc' 1"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.contains 'abc'"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.contains null 'a'"));
    }

    [Fact]
    public void Edit()
    {
        Assert.Equal("h3ll0", Script.Eval("string.replace (string.replace 'hello' 'e' '3') 'o' '0'"));
        Assert.Equal("hllo", Script.Eval("string.replace 'hello' 'e' null"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.replace 'a' '' 'b'"));
        Assert.Equal("yun", Script.Eval("string.substring 'jyuno' 1 3"));
        Assert.Equal("no", Script.Eval("string.substring 'jyuno' 3"));
        Assert.Equal("", Script.Eval("string.substring 'jyuno' 5"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.substring 'jyuno' 6"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.substring 'jyuno' 2 4"));
        Assert.Equal("jyu-no", Script.Eval("string.insert 'jyuno' 3 '-'"));
        Assert.Equal("jy", Script.Eval("string.remove 'jyuno' 2"));
        Assert.Equal("jno", Script.Eval("string.remove 'jyuno' 1 2"));
        Assert.Equal("ABCÀ", Script.Eval("string.toupper 'abcà'"));
        Assert.Equal("abcà", Script.Eval("string.tolower 'ABCÀ'"));
    }

    [Fact]
    public void Trim_Pad()
    {
        Assert.Equal("a b", Script.Eval("string.trim '  a b \n'"));
        Assert.Equal("a  ", Script.Eval("string.trimstart '  a  '"));
        Assert.Equal("  a", Script.Eval("string.trimend '  a  '"));
        Assert.Equal("abc", Script.Eval("string.trim 'xyabcyx' 'xy'"));
        Assert.Equal("abcyx", Script.Eval("string.trimstart 'xyabcyx' 'yx'"));
        Assert.Equal("a", Script.Eval("string.trim ' a ' ''"));
        Assert.Equal("  7", Script.Eval("string.padleft '7' 3"));
        Assert.Equal("007", Script.Eval("string.padleft '7' 3 '0'"));
        Assert.Equal("7..", Script.Eval("string.padright '7' 3 '.'"));
        Assert.Equal("1234", Script.Eval("string.padleft '1234' 2"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.padleft '7' 3 '00'"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.padleft '7' -1"));
    }

    [Fact]
    public void Split_Chars()
    {
        Assert.Equal(new[] { "a", "b", "", "c" }, Script.Eval("string.split 'a,b,,c' ','"));
        Assert.Equal(new[] { "a", "b", "", "c" }, Script.Eval("string.split 'a b\n\tc'"));
        Assert.Equal(new[] { "j", "y" }, Script.Eval("string.tochararray 'jy'"));
        Assert.Equal(Array.Empty<string>(), Script.Eval("string.tochararray ''"));
        Assert.Equal(65L, Script.Eval("string.charcode 'ABC'"));
        Assert.Equal(67L, Script.Eval("string.charcode 'ABC' 2"));
        Assert.Equal(0xAC00L, Script.Eval("string.charcode '가'"));
        Assert.Equal("Hi가", Script.Eval("string.fromcharcode 72 105 44032"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.charcode ''"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.charcode 'a' 1"));
        Assert.Throws<JyunoException>(() => Script.Eval("string.fromcharcode 65536"));
    }

    [Fact]
    public void IsEssential()
    {
        var globals = new Runtime(Runtime.AddJyunoCommandType.Essential).GetGlobals();
        foreach (string name in new[] { "string.concat", "string.join", "string.indexof", "string.format" })
            Assert.True(globals[name].Builtin, name);
        //기존 string 명령어는 그대로
        Assert.Equal("12", Script.Eval("string 12"));
    }
}
