using Jyuno.Language;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Jyuno.Compiler;

//미리 컴파일한 Jyuno 스크립트 (.jyc) [프로토타입]
//
//바이너리에는 "토크나이저가 만들어 냈을 토큰 배열"만 담음 (클로저 트리는 직렬화할수 없고, 줄 단위 지연 컴파일은 그대로 둠)
//불러올 때는 모든 토큰이 실제 소스에서 나올수 있는 토큰인지 검증하므로, 바이너리로는 소스로 할수 없는 일을 할수 없음
//
//구조 (리틀 엔디언)
//  헤더 32바이트: 매직(8) 포맷 버전(u16) 플래그(u16) 최소 언어 버전(u32) 본문 길이(u32) 예약(u32, 0) 체크섬(8) = XXH64(헤더 앞 24바이트 + XXH64(본문))
//  (체크섬은 손상 감지용. 변조는 서명으로 막음. SHA-256은 첫 호출에 OpenSSL을 불러오느라 스타트업이 ~2ms 늘어나서 쓰지 않음)
//  본문: 문자열 표 → 줄(토큰 배열) → 식별자 목록(정렬, 중복 없음) → [원본 줄: (길이, UTF-8) × 줄 수]
//  (원본 줄은 화면에 보여주기만 하고 실행하지 않으므로, 불러올 때는 길이만 확인하고 필요할 때 꺼냄)
//  [서명]: 키 이름 길이(u8) 키 이름(UTF-8) 서명 길이(u16) 서명 (헤더 + 본문에 대한 ECDSA P-256/SHA-256, IEEE P1363)
public sealed class JyunoImage
{
    public static readonly byte[] Magic = { 0x89 , (byte)'J' , (byte)'Y' , (byte)'C' , 0x0D , 0x0A , 0x1A , 0x0A };
    public const ushort FormatVersion = 1;
    //이 포맷을 처음 지원하는 언어 버전 (1.3.x)
    public const uint LanguageVersion = 0x0001_0300;
    const int HeaderSize = 32;

    [Flags]
    enum ImageFlags : ushort
    {
        None = 0,
        Signed = 1,
        HasSource = 2,
        Known = Signed | HasSource
    }

    //토큰 종류 (1바이트)
    const byte PrefixBase = 0x01; //0x01 ~ 0x0E: PrefixTable의 연산자
    const byte KeywordBase = 0x40; //0x40 ~ 0x4C: KeywordType
    const byte NameTag = 0x80, LongTag = 0x81, DoubleTag = 0x82, StringTag = 0x83, ErrorTag = 0x84;
    static readonly char[] PrefixTable = { ':' , '[' , ']' , '+' , '-' , '*' , '/' , '~' , '@' , '=' , '<' , '>' , '(' , ')' };
    static readonly int KeywordCount = Enum.GetValues<KeywordType>().Length;

    JyunoImage(Token[][] lines , byte[]? source , int[]? source_offsets , string[] identifiers , string? signed_by)
    {
        Lines = lines;
        this.source = source;
        this.source_offsets = source_offsets;
        Identifiers = identifiers;
        SignedBy = signed_by;
    }
    internal readonly Token[][] Lines;
    readonly byte[]? source;
    readonly int[]? source_offsets;
    public int LineCount => Lines.Length;
    /// <summary>스크립트에 나오는 모든 이름 (정렬됨). Jyuno에는 문자열로 이름을 찾는 방법이 없으므로, 여기 없는 함수/변수는 절대 사용되지 않습니다.</summary>
    public IReadOnlyList<string> Identifiers { get; }
    /// <summary>서명한 키 이름 (서명이 없다면 null)</summary>
    public string? SignedBy { get; }
    public bool HasSource => source is not null;
    /// <summary>줄의 원본 텍스트 (원본을 넣지 않았다면 토큰으로 다시 만든 텍스트)</summary>
    public string GetLine(int line)
    {
        if (source is null || source_offsets is null)
            return Detokenize(Lines[line]);
        //잘못된 UTF-8은 대체 문자로 (실행에는 쓰이지 않음)
        return Encoding.UTF8.GetString(source , source_offsets[line] , source_offsets[line + 1] - source_offsets[line]);
    }

    #region 만들기

    /// <summary>소스를 토큰화해서 바이너리로 만듭니다.</summary>
    public static byte[] Build(IReadOnlyList<string> script , JyunoImageBuildOptions? options = null)
    {
        options ??= new();
        Token[][] lines = new Token[script.Count][];
        for (int i = 0 ; i < lines.Length ; i++)
            lines[i] = Parser.Tokenizer(script[i]).ToArray();

        Dictionary<string , int> string_index = new(StringComparer.Ordinal);
        List<string> strings = new();
        int intern(string text)
        {
            if (!string_index.TryGetValue(text , out int index))
            {
                string_index[text] = index = strings.Count;
                strings.Add(text);
            }
            return index;
        }
        SortedSet<string> identifiers = new(StringComparer.Ordinal);
        foreach (Token[] tokens in lines)
        {
            foreach (Token token in tokens)
            {
                if (token.type is TokenType.Name)
                    identifiers.Add((string)token.value);
                if (token.value is string text)
                    intern(text);
            }
        }
        foreach (string name in identifiers)
            intern(name);

        Writer body = new();
        body.VarUInt((uint)strings.Count);
        foreach (string text in strings)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            body.VarUInt((uint)bytes.Length);
            body.Bytes(bytes);
        }
        body.VarUInt((uint)lines.Length);
        foreach (Token[] tokens in lines)
        {
            body.VarUInt((uint)tokens.Length);
            foreach (Token token in tokens)
            {
                switch (token.type)
                {
                    case TokenType.Prefix:
                        body.Byte((byte)(PrefixBase + Array.IndexOf(PrefixTable , (char)token.value)));
                        break;
                    case TokenType.Keyword:
                        body.Byte((byte)(KeywordBase + (int)(KeywordType)token.value));
                        break;
                    case TokenType.Name:
                        body.Byte(NameTag);
                        body.VarUInt((uint)string_index[(string)token.value]);
                        break;
                    case TokenType.Error:
                        body.Byte(ErrorTag);
                        body.VarUInt((uint)string_index[(string)token.value]);
                        break;
                    case TokenType.Constant:
                        switch (token.value)
                        {
                            case long l:
                                body.Byte(LongTag);
                                body.VarULong((ulong)((l << 1) ^ (l >> 63)));
                                break;
                            case double d:
                                body.Byte(DoubleTag);
                                body.Double(d);
                                break;
                            case string s:
                                body.Byte(StringTag);
                                body.VarUInt((uint)string_index[s]);
                                break;
                            default:
                                throw new JyunoException($"바이너리로 만들수 없는 상수입니다: {token.value}");
                        }
                        break;
                    default:
                        throw new JyunoException($"바이너리로 만들수 없는 토큰입니다: {token}");
                }
            }
        }
        body.VarUInt((uint)identifiers.Count);
        foreach (string name in identifiers)
            body.VarUInt((uint)string_index[name]);
        if (options.IncludeSource)
        {
            foreach (string text in script)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                body.VarUInt((uint)bytes.Length);
                body.Bytes(bytes);
            }
        }
        byte[] payload = body.ToArray();

        ImageFlags flags = ImageFlags.None;
        if (options.IncludeSource)
            flags |= ImageFlags.HasSource;
        if (options.Signer is not null)
            flags |= ImageFlags.Signed;
        Writer file = new();
        file.Bytes(Magic);
        file.UInt16(FormatVersion);
        file.UInt16((ushort)flags);
        file.UInt32(LanguageVersion);
        file.UInt32((uint)payload.Length);
        file.UInt32(0);
        file.UInt64(Checksum(file.ToArray() , payload));
        file.Bytes(payload);
        if (options.Signer is ECDsa signer)
        {
            byte[] signature = signer.SignData(file.ToArray() , HashAlgorithmName.SHA256 , DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            byte[] key_name = Encoding.UTF8.GetBytes(options.KeyName ?? "");
            if (key_name.Length > byte.MaxValue)
                throw new JyunoException("키 이름이 너무 깁니다.");
            file.Byte((byte)key_name.Length);
            file.Bytes(key_name);
            file.UInt16((ushort)signature.Length);
            file.Bytes(signature);
        }
        return file.ToArray();
    }

    #endregion

    #region 불러오기 (검증)

    /// <summary>바이너리를 검증하고 불러옵니다. 검증에 실패하면 <see cref="JyunoImageException"/>이 발생합니다.</summary>
    public static JyunoImage Load(ReadOnlySpan<byte> data , JyunoImageLoadOptions? options = null)
    {
        options ??= new();
        //1. 헤더
        if (data.Length > options.MaxBytes)
            throw new JyunoImageException("파일이 너무 큽니다.");
        if (data.Length < HeaderSize || !data[..8].SequenceEqual(Magic))
            throw new JyunoImageException("Jyuno 바이너리 파일이 아닙니다.");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (version != FormatVersion)
            throw new JyunoImageException($"지원하지 않는 포맷 버전입니다. ({version})");
        ImageFlags flags = (ImageFlags)BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        if ((flags & ~ImageFlags.Known) != 0)
            throw new JyunoImageException("알수 없는 플래그가 있습니다.");
        uint language = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (language > LanguageVersion)
            throw new JyunoImageException("더 새로운 Jyuno 버전으로 만든 파일입니다.");
        uint payload_length = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (payload_length > data.Length - HeaderSize)
            throw new JyunoImageException("파일이 잘렸습니다.");
        int signed_length = HeaderSize + (int)payload_length;
        ReadOnlySpan<byte> payload = data.Slice(HeaderSize , (int)payload_length);

        if (BinaryPrimitives.ReadUInt32LittleEndian(data[20..]) != 0)
            throw new JyunoImageException("예약된 헤더 값이 0이 아닙니다.");

        //2. 무결성 (손상 감지. 의도적인 변조는 서명으로만 막을수 있음)
        if (Checksum(data , payload) != BinaryPrimitives.ReadUInt64LittleEndian(data[24..]))
            throw new JyunoImageException("파일이 손상되었습니다. (체크섬 불일치)");

        //3. 서명 (신뢰하는 키로 서명했는지)
        string? signed_by = null;
        ReadOnlySpan<byte> trailer = data[signed_length..];
        if (flags.HasFlag(ImageFlags.Signed))
        {
            Reader r = new(trailer);
            int name_length = r.Byte();
            signed_by = r.Utf8(name_length);
            int signature_length = r.UInt16();
            ReadOnlySpan<byte> signature = r.Bytes(signature_length);
            if (!r.End)
                throw new JyunoImageException("서명 뒤에 알수 없는 데이터가 있습니다.");
            if (options.TrustedKeys is null || !options.TrustedKeys.TryGetValue(signed_by , out ECDsa? key))
            {
                if (options.RequireSignature)
                    throw new JyunoImageException($"신뢰하지 않는 키 '{signed_by}'(으)로 서명되었습니다.");
                signed_by = null; //확인하지 않은 서명은 없는 것으로 취급
            }
            else if (!key.VerifyData(data[..signed_length] , signature , HashAlgorithmName.SHA256 , DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new JyunoImageException("서명이 올바르지 않습니다.");
        }
        else
        {
            if (!trailer.IsEmpty)
                throw new JyunoImageException("본문 뒤에 알수 없는 데이터가 있습니다.");
            if (options.RequireSignature)
                throw new JyunoImageException("서명이 없는 파일입니다.");
        }

        //4. 본문 (모든 길이/인덱스/값의 범위를 검사, 모든 토큰은 토크나이저가 만들수 있는 토큰이어야 함)
        Reader reader = new(payload);
        int string_count = reader.Count(payload.Length);
        string[] strings = new string[string_count];
        for (int i = 0 ; i < string_count ; i++)
            strings[i] = reader.Utf8(reader.Count(payload.Length));
        //이름으로 쓰인 문자열은 한번만 검사
        bool[] checked_name = new bool[string_count];
        string name_at(int index)
        {
            string name = strings[index];
            if (!checked_name[index])
            {
                if (!IsValidName(name))
                    throw new JyunoImageException($"'{name}'은/는 올바른 이름이 아닙니다.");
                checked_name[index] = true;
            }
            return name;
        }

        int line_count = reader.Count(options.MaxLines);
        Token[][] lines = new Token[line_count][];
        //이름으로 쓰인 문자열 (식별자 목록 검증용)
        bool[] used = new bool[string_count];
        int used_count = 0;
        for (int line = 0 ; line < line_count ; line++)
        {
            int token_count = reader.Count(options.MaxTokensPerLine);
            Token[] tokens = token_count is 0 ? Array.Empty<Token>() : new Token[token_count];
            for (int t = 0 ; t < token_count ; t++)
            {
                byte tag = reader.Byte();
                if (tag >= PrefixBase && tag < PrefixBase + PrefixTable.Length)
                    tokens[t] = new Token(TokenType.Prefix , PrefixTable[tag - PrefixBase]);
                else if (tag >= KeywordBase && tag < KeywordBase + KeywordCount)
                    tokens[t] = new Token(TokenType.Keyword , (KeywordType)(tag - KeywordBase));
                else switch (tag)
                {
                    case NameTag:
                        int name_index = reader.Index(string_count);
                        if (!used[name_index])
                        {
                            used[name_index] = true;
                            used_count++;
                        }
                        tokens[t] = new Token(TokenType.Name , name_at(name_index));
                        break;
                    case LongTag:
                        ulong zigzag = reader.VarULong();
                        tokens[t] = new Token(TokenType.Constant , (long)(zigzag >> 1) ^ -(long)(zigzag & 1));
                        break;
                    case DoubleTag:
                        double d = reader.Double();
                        //소스의 실수는 항상 유한한 값 ("123.45" 형식)
                        if (!double.IsFinite(d))
                            throw new JyunoImageException($"{line}번 줄: 실수 상수가 유한한 값이 아닙니다.");
                        tokens[t] = new Token(TokenType.Constant , d);
                        break;
                    case StringTag:
                        tokens[t] = new Token(TokenType.Constant , strings[reader.Index(string_count)]);
                        break;
                    case ErrorTag:
                        //토크나이저는 오류를 만나면 바로 끝내므로, 오류 토큰은 항상 마지막
                        if (t != token_count - 1)
                            throw new JyunoImageException($"{line}번 줄: 오류 토큰은 줄의 마지막에만 올수 있습니다.");
                        tokens[t] = new Token(TokenType.Error , strings[reader.Index(string_count)]);
                        break;
                    default:
                        throw new JyunoImageException($"{line}번 줄: 알수 없는 토큰 종류입니다. (0x{tag:X2})");
                }
            }
            lines[line] = tokens;
        }

        //5. 식별자 목록: 실제 토큰과 정확히 같아야 함 (목록을 보고 판단하는 도구를 속일수 없도록)
        int identifier_count = reader.Count(string_count);
        string[] identifiers = new string[identifier_count];
        for (int i = 0 ; i < identifier_count ; i++)
        {
            int index = reader.Index(string_count);
            identifiers[i] = strings[index];
            if (i > 0 && string.CompareOrdinal(identifiers[i - 1] , identifiers[i]) >= 0)
                throw new JyunoImageException("식별자 목록이 정렬되어 있지 않습니다.");
            if (!used[index])
                throw new JyunoImageException($"식별자 목록에 사용하지 않는 이름 '{identifiers[i]}'이/가 있습니다.");
        }
        if (identifier_count != used_count)
            throw new JyunoImageException("식별자 목록에 빠진 이름이 있습니다.");

        byte[]? source = null;
        int[]? source_offsets = null;
        if (flags.HasFlag(ImageFlags.HasSource))
        {
            //길이만 확인하고 그대로 복사
            int start = reader.Position;
            source_offsets = new int[line_count + 1];
            int offset = 0;
            for (int i = 0 ; i < line_count ; i++)
            {
                int length = reader.Count(payload.Length);
                reader.Bytes(length);
                source_offsets[i] = offset;
                offset += length;
            }
            source_offsets[line_count] = offset;
            //길이 표시를 빼고 이어붙임
            source = new byte[offset];
            Reader copy = new(payload[start..reader.Position]);
            for (int i = 0 ; i < line_count ; i++)
                copy.Bytes(copy.Count(payload.Length)).CopyTo(source.AsSpan(source_offsets[i]));
        }
        if (!reader.End)
            throw new JyunoImageException("본문 뒤에 알수 없는 데이터가 있습니다.");

        //6. 이름 정책 (예: file.* 금지) - 실행하기 전에 거부
        if (options.NamePolicy is Func<string , bool> policy)
        {
            foreach (string name in identifiers)
            {
                if (!policy(name))
                    throw new JyunoImageException($"허용하지 않는 이름 '{name}'을/를 사용합니다.");
            }
        }
        return new JyunoImage(lines , source , source_offsets , identifiers , signed_by);
    }

    //토크나이저가 Name 토큰으로 만들수 있는 문자열인지
    static bool IsValidName(string name)
    {
        if (name.Length is 0 || Grammar.Keywords.ContainsKey(name))
            return false;
        if (name[0] is >= '0' and <= '9' || (name[0] is '-' && name.Length > 1 && name[1] is >= '0' and <= '9'))
            return false;
        foreach (char c in name)
        {
            if (c is ' ' or '"' or '\'' || Array.IndexOf(PrefixTable , c) >= 0)
                return false;
        }
        //탭 등 ' ' 이외의 공백은 이름에 포함될수 있음
        return true;
    }

    //토큰으로 원본과 같은 의미의 줄을 다시 만듦 (원본을 넣지 않은 경우)
    internal static string Detokenize(Token[] tokens)
    {
        StringBuilder sb = new();
        foreach (Token token in tokens)
        {
            if (sb.Length > 0)
                sb.Append(' ');
            switch (token.value)
            {
                case string s when token.type is TokenType.Constant:
                    sb.Append('"').Append(s.Replace("\\" , "\\\\").Replace("\"" , "\\\"").Replace("\n" , "\\n").Replace("\r" , "\\r")).Append('"');
                    break;
                case double d:
                    sb.Append(FormatReal(d));
                    break;
                case KeywordType k:
                    sb.Append(k.ToString().ToLowerInvariant());
                    break;
                default:
                    sb.Append(Convert.ToString(token.value , System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }
        return sb.ToString();
    }

    //지수 표기 없이, 다시 읽으면 정확히 같은 값이 되는 실수 표현 (항상 '.'을 포함해야 실수로 토큰화됨)
    internal static string FormatReal(double d)
    {
        string r = d.ToString("R" , System.Globalization.CultureInfo.InvariantCulture);
        bool negative = r.StartsWith('-') || (d == 0 && double.IsNegative(d));
        r = r.TrimStart('-');
        int exponent = 0;
        int e = r.IndexOf('E');
        if (e >= 0)
        {
            exponent = int.Parse(r[(e + 1)..] , System.Globalization.CultureInfo.InvariantCulture);
            r = r[..e];
        }
        int point = r.IndexOf('.');
        string digits = point < 0 ? r : r.Remove(point , 1);
        int integer_length = (point < 0 ? r.Length : point) + exponent;
        string text;
        if (integer_length <= 0)
            text = "0." + new string('0' , -integer_length) + digits;
        else if (integer_length >= digits.Length)
            text = digits + new string('0' , integer_length - digits.Length) + ".0";
        else
            text = digits[..integer_length] + "." + digits[integer_length..];
        return (negative ? "-" : "") + text;
    }

    #endregion

    #region XXH64 (seed 0)

    //헤더(체크섬 앞까지)와 본문 모두 보호
    internal static ulong Checksum(ReadOnlySpan<byte> header , ReadOnlySpan<byte> payload)
    {
        Span<byte> buffer = stackalloc byte[32];
        header[..24].CopyTo(buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[24..] , XxHash64(payload));
        return XxHash64(buffer);
    }

    const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL, P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;
    static ulong Round(ulong acc , ulong input) => System.Numerics.BitOperations.RotateLeft(acc + input * P2 , 31) * P1;
    static ulong Merge(ulong acc , ulong val) => (acc ^ Round(0 , val)) * P1 + P4;
    internal static ulong XxHash64(ReadOnlySpan<byte> data)
    {
        int i = 0;
        ulong h;
        if (data.Length >= 32)
        {
            ulong v1 = unchecked(P1 + P2) , v2 = P2 , v3 = 0 , v4 = unchecked(0 - P1);
            for (; i <= data.Length - 32 ; i += 32)
            {
                v1 = Round(v1 , BinaryPrimitives.ReadUInt64LittleEndian(data[i..]));
                v2 = Round(v2 , BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 8)..]));
                v3 = Round(v3 , BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 16)..]));
                v4 = Round(v4 , BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 24)..]));
            }
            h = System.Numerics.BitOperations.RotateLeft(v1 , 1) + System.Numerics.BitOperations.RotateLeft(v2 , 7) + System.Numerics.BitOperations.RotateLeft(v3 , 12) + System.Numerics.BitOperations.RotateLeft(v4 , 18);
            h = Merge(Merge(Merge(Merge(h , v1) , v2) , v3) , v4);
        }
        else
            h = P5;
        h += (ulong)data.Length;
        for (; i <= data.Length - 8 ; i += 8)
            h = System.Numerics.BitOperations.RotateLeft(h ^ Round(0 , BinaryPrimitives.ReadUInt64LittleEndian(data[i..])) , 27) * P1 + P4;
        if (i <= data.Length - 4)
        {
            h = System.Numerics.BitOperations.RotateLeft(h ^ (BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * P1) , 23) * P2 + P3;
            i += 4;
        }
        for (; i < data.Length ; i++)
            h = System.Numerics.BitOperations.RotateLeft(h ^ (data[i] * P5) , 11) * P1;
        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        h ^= h >> 32;
        return h;
    }

    #endregion

    sealed class Writer
    {
        readonly MemoryStream stream = new();
        public void Byte(byte b) => stream.WriteByte(b);
        public void Bytes(ReadOnlySpan<byte> bytes) => stream.Write(bytes);
        public void UInt16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b , v); Bytes(b); }
        public void UInt32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b , v); Bytes(b); }
        public void UInt64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b , v); Bytes(b); }
        public void Double(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b , BitConverter.DoubleToInt64Bits(v)); Bytes(b); }
        public void VarUInt(uint v) => VarULong(v);
        public void VarULong(ulong v)
        {
            while (v >= 0x80)
            {
                Byte((byte)(v | 0x80));
                v >>= 7;
            }
            Byte((byte)v);
        }
        public byte[] ToArray() => stream.ToArray();
    }

    //모든 읽기에서 범위를 검사하는 리더 (잘못된 파일은 항상 JyunoImageException)
    ref struct Reader
    {
        public Reader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            position = 0;
        }
        readonly ReadOnlySpan<byte> data;
        int position;
        public bool End => position == data.Length;
        public int Position => position;
        static JyunoImageException truncated() => new("파일이 잘렸습니다.");
        public byte Byte() => position < data.Length ? data[position++] : throw truncated();
        public ReadOnlySpan<byte> Bytes(int length)
        {
            if ((uint)length > (uint)(data.Length - position))
                throw truncated();
            var span = data.Slice(position , length);
            position += length;
            return span;
        }
        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
        public double Double() => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Bytes(8)));
        public ulong VarULong()
        {
            ulong value = 0;
            for (int shift = 0 ; shift < 64 ; shift += 7)
            {
                byte b = Byte();
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    //가장 짧은 표현만 허용 (같은 값을 여러 방법으로 표현할수 없도록)
                    if (b is 0 && shift > 0)
                        throw new JyunoImageException("정수 표현이 올바르지 않습니다.");
                    if (shift is 63 && b > 1)
                        throw new JyunoImageException("정수가 범위를 벗어납니다.");
                    return value;
                }
            }
            throw new JyunoImageException("정수 표현이 너무 깁니다.");
        }
        //0 이상 max 이하의 개수
        public int Count(int max)
        {
            ulong v = VarULong();
            return v <= (ulong)max ? (int)v : throw new JyunoImageException($"개수가 허용 범위를 벗어납니다. ({v} > {max})");
        }
        //0 이상 count 미만의 인덱스
        public int Index(int count)
        {
            ulong v = VarULong();
            return v < (ulong)count ? (int)v : throw new JyunoImageException("인덱스가 범위를 벗어납니다.");
        }
        static readonly UTF8Encoding strict = new(false , true);
        public string Utf8(int length)
        {
            try
            {
                return strict.GetString(Bytes(length));
            }
            catch (DecoderFallbackException)
            {
                throw new JyunoImageException("문자열이 올바른 UTF-8이 아닙니다.");
            }
        }
    }
}

public sealed class JyunoImageBuildOptions
{
    /// <summary>원본 텍스트를 함께 저장합니다. (오류 메시지, 디버깅용) 끄면 파일이 작아지지만 줄 텍스트는 토큰으로 다시 만듭니다.</summary>
    public bool IncludeSource { get; init; } = true;
    /// <summary>서명할 키 (ECDSA P-256). null이면 서명하지 않습니다.</summary>
    public ECDsa? Signer { get; init; }
    public string? KeyName { get; init; }
}

public sealed class JyunoImageLoadOptions
{
    public int MaxBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxLines { get; init; } = 1_000_000;
    public int MaxTokensPerLine { get; init; } = 1 << 16;
    /// <summary>신뢰하는 공개 키 (키 이름 → 키)</summary>
    public IReadOnlyDictionary<string , ECDsa>? TrustedKeys { get; init; }
    /// <summary>신뢰하는 키로 서명된 파일만 불러옵니다.</summary>
    public bool RequireSignature { get; init; }
    /// <summary>스크립트에 나오는 모든 이름을 검사합니다. false를 돌려주면 불러오지 않습니다. (예: name => !name.StartsWith("file."))</summary>
    public Func<string , bool>? NamePolicy { get; init; }
}

public sealed class JyunoImageException : JyunoException
{
    public JyunoImageException(string message) : base(message) { }
}
