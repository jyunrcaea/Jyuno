namespace Jyuno.Tests;

//math.random, math.csprng
public class RandomTests
{
    public static readonly TheoryData<string> Names = new() { "math.random", "math.csprng" };

    [Theory]
    [MemberData(nameof(Names))]
    public void NoArgument_RealBetweenZeroAndOne(string name)
    {
        for (int i = 0; i < 200; i++)
        {
            var value = Assert.IsType<double>(Script.Eval(name));
            Assert.InRange(value, 0.0, 0.9999999999999999);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void IntegerMax(string name)
    {
        HashSet<long> seen = new();
        for (int i = 0; i < 500; i++)
        {
            var value = Assert.IsType<long>(Script.Eval($"{name} 6"));
            Assert.InRange(value, 0, 5);
            seen.Add(value);
        }
        //6가지가 모두 나와야 함 (500번이면 사실상 항상)
        Assert.Equal(6, seen.Count);
        Assert.Equal(0L, Script.Eval($"{name} 1"));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void IntegerRange(string name)
    {
        for (int i = 0; i < 200; i++)
            Assert.InRange(Assert.IsType<long>(Script.Eval($"{name} -3 3")), -3, 2);
        Assert.Equal(10L, Script.Eval($"{name} 10 11"));
        //long 전체 범위도 처리
        Assert.IsType<long>(Script.Eval($"{name} -9223372036854775808 9223372036854775807"));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void RealRange(string name)
    {
        for (int i = 0; i < 200; i++)
        {
            Assert.InRange(Assert.IsType<double>(Script.Eval($"{name} 2.5")), 0.0, 2.5);
            Assert.InRange(Assert.IsType<double>(Script.Eval($"{name} 1 1.5")), 1.0, 1.5);
            double big = Assert.IsType<double>(Script.Eval($"{name} (sub 0.0 (math.pow 10 308)) (math.pow 10 308)"));
            Assert.True(double.IsFinite(big) && Math.Abs(big) <= 1e308);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EmptyRange_Throws(string name)
    {
        Assert.Throws<JyunoException>(() => Script.Eval($"{name} 0"));
        Assert.Throws<JyunoException>(() => Script.Eval($"{name} -5"));
        Assert.Throws<JyunoException>(() => Script.Eval($"{name} 3 3"));
        Assert.Throws<JyunoException>(() => Script.Eval($"{name} 1.5 1.0"));
        Assert.Throws<JyunoException>(() => Script.Eval($"{name} 0 math.nan"));
        Assert.Throws<JyunoException>(() => Script.Eval($"{name} 'a'"));
        Assert.Throws<JyunoException>(() => Script.Eval($"{name} null"));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void NotFoldedAtCompileTime(string name)
    {
        //같은 줄을 반복 실행해도 매번 새로 뽑아야 함
        var (_, output) = Script.Run("repeat 50", $"out ({name} 1000000)", "end");
        Assert.True(output.Distinct().Count() > 1);
    }

    [Fact]
    public void InMathGroup()
    {
        Assert.False(new Runtime(Runtime.AddJyunoCommandType.Essential).GetGlobals().ContainsKey("math.random"));
        var globals = new Runtime().GetGlobals();
        Assert.Equal(Analysis.GlobalKind.Function, globals["math.csprng"].Kind);
        Assert.False(globals["math.random"].Builtin);
    }
}
