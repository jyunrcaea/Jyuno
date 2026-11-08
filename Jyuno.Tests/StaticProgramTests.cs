using Jyuno.Compiler;
using System.Buffers.Binary;

namespace Jyuno.Tests;

//[프로토타입] 제한 모드 프로그램(.jyc v2): 연결(호스트 권한 대조)과 위조 파일 검증
public class StaticProgramTests
{
    static readonly string[] Sample = {
        "func fib",
        "    param n",
        "    if (less n 2)",
        "        return n",
        "    end",
        "    return (add (fib (sub n 1)) (fib (sub n 2)))",
        "endfunc",
        "total = 0",
        "for x (list 1 2 3 4 5)",
        "    total = add total (fib x)",
        "    if (greater total 6)",
        "        break",
        "    end",
        "end",
        "i = 0",
        "again:",
        "i = add i 1",
        "if (less i 3)",
        "    goto again",
        "end",
        "return (add total i (math.pi))",
    };

    [Fact]
    public void Program_RunsLikeInterpreter()
    {
        Runtime host = new();
        object? expected = new Runtime().Create(Sample).Run();
        object? actual = JyunoProgramImage.Load(JyunoProgramImage.Build(Sample, host), new Runtime()).Run();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Build_RejectsRestrictedFeatures()
    {
        Assert.Throws<JyunoBuildException>(() => JyunoProgramImage.Build(new[] { "x = 1", "x =" }, new Runtime()));
        Assert.Throws<JyunoBuildException>(() => JyunoProgramImage.Build(new[] { "func add", "endfunc" }, new Runtime()));
        Assert.Throws<JyunoBuildException>(() => JyunoProgramImage.Build(new[] { "goto 2", "if 1", "x = 1", "end" }, new Runtime()));
        Assert.Throws<JyunoBuildException>(() => JyunoProgramImage.Build(new[] { "x = 'unterminated" }, new Runtime()));
    }

    [Fact]
    public void Link_RejectsMissingPermission_BeforeRunning()
    {
        List<object?> output = new();
        Runtime profile = new(Runtime.AddJyunoCommandType.All);
        profile.AddFunction("out", a => { output.Add(a[0]); return null; });
        byte[] image = JyunoProgramImage.Build(new[] { "out 1", "file.write 'x.txt' 'hi'" }, profile);
        Assert.Contains(JyunoProgramImage.Requirements(image), r => r.Name == "file.write");

        //파일 권한이 없는 호스트: 한 줄도 실행하지 않고 거부
        Runtime host = new(Runtime.AddJyunoCommandType.Default);
        host.AddFunction("out", a => { output.Add(a[0]); return null; });
        var e = Assert.Throws<JyunoLinkException>(() => JyunoProgramImage.Load(image, host));
        Assert.Contains("file.write", e.Message);
        Assert.Empty(output);
    }

    [Fact]
    public void Link_AcceptsHostThatGrantsEverything()
    {
        byte[] image = JyunoProgramImage.Build(new[] { "return (add 1 (math.abs -2))" }, new Runtime());
        Assert.Equal(3L, JyunoProgramImage.Load(image, new Runtime(Runtime.AddJyunoCommandType.All)).Run());
    }

    [Fact]
    public void Link_RejectsKindMismatch()
    {
        Runtime profile = new();
        long score = 0;
        profile.AddVariable("game.score", () => score, v => score = (long)v!);
        byte[] image = JyunoProgramImage.Build(new[] { "game.score = 5" }, profile);
        Runtime host = new();
        host.AddFunction("game.score", _ => null);
        Assert.Throws<JyunoLinkException>(() => JyunoProgramImage.Load(image, host));
    }

    [Fact]
    public void Link_RejectsFakeBuiltin()
    {
        //빌드할 때 Jyuno 내장 math.sin으로 미리 계산했으므로, 호스트가 같은 이름의 다른 함수를 주면 거부
        byte[] image = JyunoProgramImage.Build(new[] { "return (math.sin 0)" }, new Runtime());
        Runtime host = new(Runtime.AddJyunoCommandType.Console);
        host.AddFunction("math.sin", _ => 42L);
        var e = Assert.Throws<JyunoLinkException>(() => JyunoProgramImage.Load(image, host));
        Assert.Contains("math.sin", e.Message);
    }

    [Fact]
    public void Link_RejectsHostNameCollision()
    {
        //빌드할 때 스크립트 변수였던 이름이 호스트 전역이라면 인터프리터와 뜻이 달라지므로 거부
        byte[] image = JyunoProgramImage.Build(new[] { "score = 1", "return score" }, new Runtime());
        Runtime host = new();
        host.AddFunction("score", _ => null);
        Assert.Throws<JyunoLinkException>(() => JyunoProgramImage.Load(image, host));
    }

    [Fact]
    public void Corruption_AlwaysDetected()
    {
        byte[] bytes = JyunoProgramImage.Build(Sample, new Runtime());
        for (int i = 0; i < bytes.Length; i++)
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] copy = (byte[])bytes.Clone();
                copy[i] ^= (byte)(1 << bit);
                Assert.Throws<JyunoProgramException>(() => JyunoProgramImage.Load(copy, new Runtime()));
            }
    }

    //체크섬까지 다시 계산한 위조 파일: 검증에서 거부되거나, 통과했다면 엔진이 깨지지 않아야 함
    //(칸 번호/영역/가져오기 번호/goto 대상이 모두 검증되었으므로 IndexOutOfRange, NullReference, InvalidCast가 엔진에서 나면 안 됨)
    [Fact]
    public void Fuzz_ForgedPrograms_NeverBreakEngine()
    {
        byte[] bytes = JyunoProgramImage.Build(Sample, new Runtime());
        Random random = new(4321);
        int loaded = 0, ran = 0;
        for (int n = 0; n < 10000; n++)
        {
            byte[] forged = (byte[])bytes.Clone();
            int count = random.Next(1, 4);
            for (int k = 0; k < count; k++)
                forged[random.Next(32, forged.Length)] = (byte)random.Next(256);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(forged.AsSpan(16));
            BinaryPrimitives.WriteUInt64LittleEndian(forged.AsSpan(24), JyunoProgramImage.Checksum(forged, forged.AsSpan(32, length)));
            JyunoProgram program;
            try
            {
                program = JyunoProgramImage.Load(forged, new Runtime());
            }
            catch (JyunoProgramException) { continue; }
            catch (JyunoLinkException) { continue; }
            loaded++;
            Task<object?> task = Task.Run(() => program.Run());
            try
            {
                if (!task.Wait(TimeSpan.FromMilliseconds(100)))
                    continue; //goto로 만든 무한 반복 (소스로도 만들수 있음)
                ran++;
            }
            catch (AggregateException e) when (e.InnerException is JyunoException) { ran++; }
            catch (AggregateException e) when (e.InnerException is IndexOutOfRangeException or NullReferenceException or InvalidCastException && e.InnerException.StackTrace!.Contains("StaticEngine"))
            {
                Assert.Fail($"엔진 내부 오류: {e.InnerException}");
            }
            catch (AggregateException) { ran++; }
        }
        Assert.True(loaded > 0 && ran > 0, $"loaded={loaded} ran={ran}");
    }
}
