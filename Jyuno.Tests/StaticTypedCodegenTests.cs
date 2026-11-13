using Jyuno.Compiler;

namespace Jyuno.Tests;

//[실험] 정적 타입 검사기가 증명한 타입으로 만든 코드 (IlTyped 엔진, 타입을 쓴 C# 생성)
public class StaticTypedCodegenTests
{
    static readonly string[] Input = {
        "func input",
        "  param ask",
        "  say ask",
        "  local response = listen",
        "  return response ?? ''",
        "endfunc",
        "x = input '1 + 1 = '",
        "y = input 2",
        "return (add x '|' y)",
    };

    //검사기에 알려주는 호스트 함수의 타입 (listen은 입력이 끝나면 null)
    static readonly IReadOnlyDictionary<string, string> HostTypes = new Dictionary<string, string> { ["say"] = "(any) -> null", ["listen"] = "() -> string|null" };

    static Runtime Host(string input)
    {
        Runtime runtime = new();
        StringReader reader = new(input);
        runtime.AddFunction("say", a => null);
        runtime.AddFunction("listen", a => reader.ReadLine());
        return runtime;
    }

    [Fact]
    public void Function_IsSpecializedPerArgumentType_InCSharp()
    {
        string? code = JyunoProgramImage.EmitCSharp(Input, Host(""), "G", "P", out string? reason, out bool typed, HostTypes);
        Assert.NotNull(code);
        Assert.True(typed, reason);
        //input(int) -> string, input(string) -> string: ??로 반환값이 string임을 증명
        Assert.Matches(@"string f\d+_\d+\(long p", code);
        Assert.Matches(@"string f\d+_\d+\(string p", code);
        Assert.DoesNotContain("dynamic", code);
    }

    [Fact]
    public void TypedIl_RunsLikeInterpreter()
    {
        object? expected = Host("a\n").Create(Input).Run();
        JyunoProgram program = JyunoProgramImage.Load(JyunoProgramImage.Build(Input, Host("")), Host("a\n"), JyunoEngine.IlTyped, HostTypes);
        Assert.Equal("IlTyped", program.Engine);
        Assert.Equal(expected, program.Run());
        Assert.Equal("a|", expected);
    }

    [Fact]
    public void NullableInt_ChecksNullThenComputes()
    {
        //int 'ab'는 null: 덧셈은 null, 곱셈은 오류 (인터프리터와 같게)
        var (_, output) = Script.Run(
            "x = int 'ab'",
            "y = int '5'",
            "out (x ?? 7)",
            "out (y * 2)",
            "out (add x 1)",
            "out (y + 1)",
            "out (y < 9)");
        Assert.Equal(new object?[] { 7L, 10L, null, 6L, true }, output);
        var error = Assert.Throws<JyunoException>(() => Script.Run("x = int 'ab'", "out (x * 2)"));
        Assert.Contains("null", error.Message);
    }

    [Fact]
    public void Checker_AddWithNull_IsNull()
    {
        //Values.Add: null과 숫자의 덧셈은 null (오류가 아님)
        TypeCheckResult r = JyunoTypeChecker.Check(new[] { "x = int 'ab'", "y = add x 1" }, new Runtime(), TypeCheckLevel.Strict);
        Assert.Contains(r.Types, t => t.StartsWith("y:") && t.Contains("null"));
    }
}
