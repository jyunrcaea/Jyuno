using System.Globalization;

namespace Jyuno.Tests;

//[실험] 탈출 분석과 인라이닝: 켜고 끈 실행의 결과/출력/오류가 같아야 함
public class OptimizationTests
{
    static string RunWith(string[] lines , bool escape , bool inline)
    {
        Runtime runtime = new();
        List<string> output = new();
        List<object?> kept = new();
        runtime.AddFunction("out" , args => { output.Add(string.Join(" " , args.Select(o => o is null ? "null" : Convert.ToString(o , CultureInfo.InvariantCulture)))); return null; });
        //C# 함수가 받은 구조체 값을 보관했다가 돌려줌
        runtime.AddFunction("keep" , args => { kept.Add(args[0]); return args[0]; });
        runtime.AddFunction("kept" , args => kept[^1]);
        Interpreter interpreter = runtime.Create(lines);
        interpreter.EscapeAnalysis = escape;
        interpreter.Inlining = inline;
        runtime.AddFunction("line" , args => (long)interpreter.CurrentExecuteLine);
        string result;
        try
        {
            object? value = interpreter.Run();
            result = "ok " + (value is null ? "null" : Convert.ToString(value , CultureInfo.InvariantCulture));
        }
        catch (JyunoException e)
        {
            result = "error " + e.Message;
        }
        return result + "\n" + string.Join("\n" , output);
    }

    //네 가지 조합이 모두 같은 결과를 내는지 확인하고, 그 결과를 돌려줌
    static string Run(params string[] lines)
    {
        string expected = RunWith(lines , false , false);
        Assert.Equal(expected , RunWith(lines , true , false));
        Assert.Equal(expected , RunWith(lines , false , true));
        Assert.Equal(expected , RunWith(lines , true , true));
        return expected;
    }

    static readonly string[] Pt = { "struct pt" , "  param x" , "endstruct" };
    static string RunPt(params string[] lines) => Run(Pt.Concat(lines).ToArray());

    [Fact]
    public void Constructor_FieldsSharingOuterList_AreStillCopied()
    {
        //생성자 결과가 바깥의 리스트를 담고 있다면 그대로 넣으면 안 됨
        Assert.Equal("ok null\n3\n2\n2" , Run(
            "struct bag" , "  param x" , "  param y" , "endstruct" ,
            "a = list 1 2" , "s = bag a a" , "t = s" , "list.push t.x 9" ,
            "out (length t.y)" , "out (length s.x)" , "list.push a 5" , "out (length s.y)"));
    }

    [Fact]
    public void Param_InLoop_ReadsOriginalArgumentEachTime()
    {
        Assert.Equal("ok pt { x = 2 }\npt { x = 1 }\npt { x = 1 }" , RunPt(
            "func f" , "  i = 0" , "  while (less i 2)" , "    param q" , "    out (string q)" , "    q.x = add q.x 1" , "    i = add i 1" , "  end" , "  return q" , "endfunc" ,
            "return (f (pt 1))"));
    }

    [Fact]
    public void Param_WithGoto_ReadsOriginalArgumentEachTime()
    {
        Assert.Equal("ok null\n1\n1" , RunPt(
            "func f" , "  n = 0" , "  again:" , "  param p" , "  out p.x" , "  p.x = 10" , "  n = add n 1" , "  if (less n 2)" , "    goto again" , "  end" , "endfunc" ,
            "f (pt 1)"));
    }

    [Fact]
    public void ValuesGivenToCSharp_AreNotReusedByVariables()
    {
        Assert.Equal("ok null\npt { x = 7 }\npt { x = 7 }\npt { x = 8 }" , RunPt(
            "c = keep (pt 7)" , "c.x = 8" , "out (string (kept))" ,
            "d = kept" , "d.x = 9" , "out (string (kept))" , "out (string c)"));
    }

    [Fact]
    public void ValuesStoredInCollections_AreNotReusedByVariables()
    {
        Assert.Equal("ok null\npt { x = 1 }\npt { x = 40 }\npt { x = 1 }" , RunPt(
            "p = pt 1" , "l = add (list) p" , "for e l" , "  e.x = 99" , "end" , "out (string (l 0))" ,
            "l 0 (pt 40)" , "y = l 0" , "y.x = 41" , "out (string (l 0))" , "out (string p)"));
    }

    [Fact]
    public void BorrowedArgument_SeesValueBeforeLaterArguments()
    {
        //equal의 첫 인자는 두번째 인자(함수 호출)가 a를 바꾸기 전의 값이어야 함
        Assert.Equal("ok null\nFalse\nTrue" , RunPt(
            "a = pt 1" , "func bump" , "  a.x = add a.x 1" , "  return a" , "endfunc" ,
            "out (equal a (bump))" , "out (equal a a)"));
    }

    [Fact]
    public void Inlined_Recursion_StopsAtMaxCallDepth()
    {
        Assert.Equal($"error 함수 호출이 너무 깊습니다. (최대 {Interpreter.MaxCallDepth}번)\n" , Run(
            "func f" , "  param n" , "  return (f (add n 1))" , "endfunc" , "f 0"));
    }

    [Fact]
    public void Inlined_StructParameter_IsACopy()
    {
        Assert.Equal("ok null\n1\n1\n0\n11\n11\n10\n4" , Run(
            "struct counter" , "  param count" , "  func next" , "    count = add count 1" , "    return count" , "  endfunc" , "endstruct" ,
            "func bump" , "  param c" , "  return (c.next)" , "endfunc" ,
            "func get" , "  param c" , "  return c.count" , "endfunc" ,
            "c = counter 0" , "out (bump c)" , "out (bump c)" , "out c.count" ,
            "l = list (counter 10)" , "out (bump (l 0))" , "out (bump (l 0))" , "out (get (l 0))" ,
            "c.count = 4" , "out (get c)"));
    }

    [Fact]
    public void Inlined_ParameterShadowsBuiltin()
    {
        Assert.Equal("ok null\n4" , Run("func h" , "  param mul" , "  return (sub mul 1)" , "endfunc" , "out (h 5)"));
        Assert.StartsWith("error 괄호" , Run("func g" , "  param add" , "  return (add 1 2)" , "endfunc" , "out (g 5)"));
    }

    [Fact]
    public void Inlined_MemberOfNonStructParameter_ReadsOuterName()
    {
        Assert.Equal("ok null\n5\n2" , RunPt(
            "p.x = 5" , "func f" , "  param p" , "  return p.x" , "endfunc" , "out (f 1)" , "out (f (pt 2))"));
    }

    [Fact]
    public void Inlined_AssignmentInReturn_StaysLocal()
    {
        Assert.Equal("error 'y'은/는 알수없는 변수/함수 입니다.\n" , Run(
            "func f" , "  param x" , "  return (y = x)" , "endfunc" , "f 3" , "out y"));
    }

    [Fact]
    public void Inlined_ExecuteLine_IsReturnLine()
    {
        Assert.Equal("ok null\n2" , Run("func f" , "  param x" , "  return (line)" , "endfunc" , "" , "out (f 1)"));
    }

    [Fact]
    public void Inlined_MissingAndExtraArguments()
    {
        Assert.Equal("ok null\n3\nab\nnull" , Run(
            "func f" , "  param a" , "  param b" , "  return (add a b)" , "endfunc" ,
            "out (f 1 2 3)" , "out (f 'a' 'b')" , "out (f 1)"));
    }
}
