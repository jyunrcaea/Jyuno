using Jyuno.Compiler;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Jyuno.Tests;

//미리 컴파일한 바이너리(.jyc) 프로토타입
public class ImageTests
{
    static readonly string[] Sample = {
        "func prime",
        "    param n",
        "    if (less n 2)",
        "        return false",
        "    end",
        "    local i = 2",
        "    while (not (greater (mul i i) n))",
        "        if (equal (mod n i) 0)",
        "            return false",
        "        end",
        "        i = add i 1",
        "    end",
        "    return true",
        "endfunc",
        "c = 0",
        "for x (array 2 3 4 5 6 7 8 9 10 11)",
        "    if (prime x)",
        "        c = add c 1",
        "    end",
        "end",
        "s = \"따옴표 \\\" 와 \\n 줄바꿈\"",
        "d = -1.25",
        "",
        "return (add c (length s) d)",
    };

    [Theory]
    [InlineData("", 0xEF46DB3751D8E999UL)]
    [InlineData("a", 0xD24EC4F1A98C6E5BUL)]
    [InlineData("abc", 0x44BC2CF5AD770999UL)]
    [InlineData("Nobody inspects the spammish repetition", 0xFBCEA83C8A378BF1UL)]
    public void XxHash64_MatchesReference(string text, ulong expected)
    {
        Assert.Equal(expected, JyunoImage.XxHash64(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void Image_RunsLikeSource()
    {
        object? source = new Runtime().Create(Sample).Run();
        object? image = new Runtime().Load(JyunoImage.Build(Sample)).Run();
        Assert.Equal(source, image);
    }

    [Fact]
    public void Image_KeepsLineNumbersAndErrors()
    {
        string[] lines = { "a = 1", "", "x: ", "b = \"닫히지 않은 문자열", "if 1" };
        var image = JyunoImage.Load(JyunoImage.Build(lines));
        Assert.Equal(lines.Length, image.LineCount);
        Assert.Equal(new Runtime().Create(lines).Run(), new Runtime().Create(image).Run());
    }

    [Fact]
    public void Image_KeepsSourceText_OrDetokenizes()
    {
        Assert.Equal(Sample[20], JyunoImage.Load(JyunoImage.Build(Sample)).GetLine(20));
        var stripped = JyunoImage.Load(JyunoImage.Build(Sample, new JyunoImageBuildOptions { IncludeSource = false }));
        Assert.False(stripped.HasSource);
        //다시 만든 텍스트를 토큰화하면 같은 토큰이 나와야 함
        for (int i = 0; i < Sample.Length; i++)
            Assert.Equal(Parser.Tokenizer(Sample[i]), Parser.Tokenizer(stripped.GetLine(i)));
    }

    [Theory]
    [InlineData(-1.25)]
    [InlineData(-1.250000001542503)]
    [InlineData(1e-300)]
    [InlineData(1.7976931348623157e308)]
    [InlineData(-0.0)]
    [InlineData(123456789.125)]
    public void StrippedReal_RoundTrips(double value)
    {
        //지수 표기 없이 쓴 텍스트를 다시 토큰화하면 비트까지 같은 실수가 나와야 함
        Token[] tokens = Parser.Tokenizer(JyunoImage.FormatReal(value)).ToArray();
        Assert.Single(tokens);
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits((double)tokens[0].value));
    }

    [Fact]
    public void Identifiers_ListsEveryName()
    {
        var image = JyunoImage.Load(JyunoImage.Build(new[] { "file.write \"a\" (add x 1)", "y = x" }));
        Assert.Equal(new[] { "add", "file.write", "x", "y" }, image.Identifiers);
    }

    [Fact]
    public void NamePolicy_RejectsBeforeRunning()
    {
        byte[] bytes = JyunoImage.Build(new[] { "console.writeline 1", "file.remove \"x\"" });
        var options = new JyunoImageLoadOptions { NamePolicy = name => !name.StartsWith("file.") };
        var e = Assert.Throws<JyunoImageException>(() => new Runtime().Load(bytes, options));
        Assert.Contains("file.remove", e.Message);
    }

    [Fact]
    public void Signature_VerifiedWithTrustedKey()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] bytes = JyunoImage.Build(Sample, new JyunoImageBuildOptions { Signer = key, KeyName = "plugin-author" });
        var trusted = new JyunoImageLoadOptions { RequireSignature = true, TrustedKeys = new Dictionary<string, ECDsa> { ["plugin-author"] = key } };
        Assert.Equal("plugin-author", JyunoImage.Load(bytes, trusted).SignedBy);
        //다른 키로 검증하면 실패
        var wrong = new JyunoImageLoadOptions { RequireSignature = true, TrustedKeys = new Dictionary<string, ECDsa> { ["plugin-author"] = other } };
        Assert.Throws<JyunoImageException>(() => JyunoImage.Load(bytes, wrong));
        //서명이 없는 파일은 거부
        Assert.Throws<JyunoImageException>(() => JyunoImage.Load(JyunoImage.Build(Sample), trusted));
        //본문을 바꾸고 체크섬을 다시 계산해도 서명에서 걸림
        byte[] tampered = Retouch(bytes, b => b[40] ^= 1);
        Assert.Throws<JyunoImageException>(() => JyunoImage.Load(tampered, trusted));
    }

    [Fact]
    public void Corruption_AlwaysDetected()
    {
        byte[] bytes = JyunoImage.Build(Sample);
        for (int i = 0; i < bytes.Length; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] copy = (byte[])bytes.Clone();
                copy[i] ^= (byte)(1 << bit);
                Assert.Throws<JyunoImageException>(() => JyunoImage.Load(copy));
            }
        }
        for (int length = 0; length < bytes.Length; length++)
            Assert.Throws<JyunoImageException>(() => JyunoImage.Load(bytes.AsSpan(0, length)));
    }

    //체크섬까지 다시 계산한 (의도적으로 조작한) 파일: 검증을 통과하지 못하거나, 통과했다면 소스로도 만들수 있는 토큰만 있어야 함
    [Fact]
    public void Fuzz_ForgedImages_OnlyFailWithImageException()
    {
        byte[] bytes = JyunoImage.Build(Sample);
        Random random = new(1234);
        int loaded = 0;
        for (int n = 0; n < 20000; n++)
        {
            byte[] forged = Retouch(bytes, b => {
                int count = random.Next(1, 4);
                for (int k = 0; k < count; k++)
                    b[random.Next(32, b.Length)] = (byte)random.Next(256);
            });
            JyunoImage image;
            try
            {
                image = JyunoImage.Load(forged);
            }
            catch (JyunoImageException)
            {
                continue;
            }
            loaded++;
            //통과한 파일의 모든 토큰은 소스로도 쓸수 있어야 함: 다시 만든 텍스트를 토큰화하면 같은 토큰이 나옴
            //(줄 양 끝의 공백은 토크나이저가 지우므로 괄호로 감싸서 비교, 오류 토큰은 메시지일 뿐이므로 제외)
            for (int i = 0; i < image.LineCount; i++)
            {
                Token[] tokens = image.Lines[i];
                if (tokens.Length > 0 && tokens[^1].type is TokenType.Error)
                    tokens = tokens[..^1];
                Token[] expected = tokens.Prepend(new Token(TokenType.Prefix, '(')).Append(new Token(TokenType.Prefix, ')')).ToArray();
                Assert.Equal(expected, Parser.Tokenizer("( " + JyunoImage.Detokenize(tokens) + " )").ToArray());
            }
        }
        Assert.True(loaded > 0);
    }

    //본문을 고치고 체크섬을 다시 계산
    static byte[] Retouch(byte[] bytes, Action<byte[]> edit)
    {
        byte[] copy = (byte[])bytes.Clone();
        edit(copy);
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(16));
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(24), JyunoImage.Checksum(copy, copy.AsSpan(32, length)));
        return copy;
    }
}
