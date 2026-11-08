using System.Security.Cryptography;

namespace Jyuno.Language;

//math.random(빠른 난수)과 math.csprng(암호학적으로 안전한 난수)
//인자 규칙은 둘이 같음
//  (없음)        → [0, 1) 실수
//  max           → [0, max) (정수라면 정수, 실수라면 실수)
//  min max       → [min, max) (둘다 정수라면 정수, 하나라도 실수라면 실수)
internal abstract class RandomValues
{
    public static readonly RandomValues Fast = new FastRandom();
    public static readonly RandomValues Secure = new SecureRandom();

    //[0, 1) 실수
    protected abstract double NextReal();
    //[min, max) 정수 (min < max)
    protected abstract long NextInteger(long min , long max);

    public object Next(object?[] args , string name)
    {
        switch (args.Length)
        {
            case 0:
                return NextReal();
            case 1:
                if (Values.TryGetInteger(args[0] , out long max_only))
                    return integer(0 , max_only , name);
                return real(0 , real_argument(args[0] , name) , name);
            default:
                if (Values.TryGetInteger(args[0] , out long min) && Values.TryGetInteger(args[1] , out long max))
                    return integer(min , max , name);
                return real(real_argument(args[0] , name) , real_argument(args[1] , name) , name);
        }
    }

    long integer(long min , long max , string name)
    {
        if (min >= max)
            throw new JyunoException($"{name}: 범위가 비어있습니다. (최솟값 {min}이/가 최댓값 {max}보다 작아야 합니다)");
        return NextInteger(min , max);
    }

    double real(double min , double max , string name)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
            throw new JyunoException($"{name}: 범위는 유한한 숫자여야 합니다.");
        if (min >= max)
            throw new JyunoException($"{name}: 범위가 비어있습니다. (최솟값 {min}이/가 최댓값 {max}보다 작아야 합니다)");
        while (true)
        {
            double u = NextReal();
            //min + u * (max - min)은 max - min이 무한대가 될수 있으므로 두 값을 섞는 형태로 계산
            double result = (1 - u) * min + u * max;
            //반올림으로 범위를 벗어났다면 다시 뽑음
            if (result >= min && result < max)
                return result;
        }
    }

    static double real_argument(object? value , string name)
    {
        if (Values.TryGetReal(value ?? throw JyunoCommands.null_exception , out double result))
            return result;
        throw new JyunoException($"{name}: '{value}'은/는 숫자가 아닙니다.");
    }

    //System.Random (스레드 안전한 Random.Shared)
    sealed class FastRandom : RandomValues
    {
        protected override double NextReal() => Random.Shared.NextDouble();
        protected override long NextInteger(long min , long max) => Random.Shared.NextInt64(min , max);
    }

    //System.Security.Cryptography.RandomNumberGenerator
    sealed class SecureRandom : RandomValues
    {
        static ulong next_bits()
        {
            Span<byte> bytes = stackalloc byte[8];
            RandomNumberGenerator.Fill(bytes);
            return BitConverter.ToUInt64(bytes);
        }
        //상위 53비트로 [0, 1) 실수를 만듦
        protected override double NextReal() => (next_bits() >> 11) * (1.0 / (1UL << 53));
        protected override long NextInteger(long min , long max)
        {
            ulong range = unchecked((ulong)(max - min));
            //2^64를 range로 나눈 나머지보다 작은 값을 버려서 치우침 없이 뽑음
            ulong threshold = unchecked(0 - range) % range;
            while (true)
            {
                ulong bits = next_bits();
                if (bits >= threshold)
                    return unchecked(min + (long)(bits % range));
            }
        }
    }
}
