// [실험] Jyuno 벤치 프로그램을 손으로 C#에 옮겨, 값 표현(object/타입)과 실행 방식(클로저/식 트리/IL)별 상한을 잰 코드
// dotnet run -c Release   (AOT: dotnet publish -c Release -r linux-x64 -p:CeilingAot=true)
using System.Diagnostics;
using System.Runtime.CompilerServices;
// 같은 Jyuno 프로그램을 손으로 옮긴 C#: 실행 모델별 상한 측정
static class B
{
    static readonly object T = true, F = false;
    // 동적 연산 (값은 object, Jyuno add/less와 같은 규칙: long끼리 순환, long/double 섞이면 double)
    [MethodImpl(MethodImplOptions.NoInlining)] public static object Add(object x, object y) { if (x is long a && y is long b) return unchecked(a + b); return Real(x) + Real(y); }
    [MethodImpl(MethodImplOptions.NoInlining)] public static object Sub(object x, object y) { if (x is long a && y is long b) return unchecked(a - b); return Real(x) - Real(y); }
    [MethodImpl(MethodImplOptions.NoInlining)] public static object Mul(object x, object y) { if (x is long a && y is long b) return unchecked(a * b); return Real(x) * Real(y); }
    [MethodImpl(MethodImplOptions.NoInlining)] public static object Div(object x, object y) { if (x is long a && y is long b) return a / b; return Real(x) / Real(y); }
    [MethodImpl(MethodImplOptions.NoInlining)] public static object Less(object x, object y) { if (x is long a && y is long b) return a < b ? T : F; return Real(x) < Real(y) ? T : F; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static object Greater(object x, object y) { if (x is long a && y is long b) return a > b ? T : F; return Real(x) > Real(y) ? T : F; }
    static double Real(object v) => v is double d ? d : (long)v;
    public static bool Truth(object v) => v is bool b ? b : throw new Exception();

    // ---- while_sum_200k ----
    public static object WhileSumBoxed() { object i = 0L, s = 0L; while (Truth(Less(i, 200000L))) { s = Add(s, i); i = Add(i, 1L); } return s; }
    public static long WhileSumTyped() { long i = 0, s = 0; while (i < 200000) { s = unchecked(s + i); i = unchecked(i + 1); } return s; }
    // ---- fib_20 ----
    static int depth;
    public static object FibBoxed(object n) { if (++depth > 200) throw new Exception(); try { if (Truth(Less(n, 2L))) return n; return Add(FibBoxed(Sub(n, 1L)), FibBoxed(Sub(n, 2L))); } finally { depth--; } }
    public static long FibTyped(long n) { if (++depth > 200) throw new Exception(); try { if (n < 2) return n; return unchecked(FibTyped(n - 1) + FibTyped(n - 2)); } finally { depth--; } }
    // ---- double_math_100k ----
    public static object DoubleBoxed() { object x = 0.0, i = 0L; for (long r = 0; r < 100000; r++) { x = Add(Mul(x, 0.5), Div(i, 3.0)); i = Add(i, 1L); } return x; }
    public static double DoubleTyped() { double x = 0.0; long i = 0; for (long r = 0; r < 100000; r++) { x = x * 0.5 + i / 3.0; i = unchecked(i + 1); } return x; }
    // ---- sieve_list_20k ----
    public static object SieveBoxed()
    {
        object n = 20000L; var flags = new List<object?> { T }; for (int r = 0; r < 15; r++) flags.AddRange(flags.ToArray());
        object i = 2L;
        while (!Truth(Greater(Mul(i, i), n))) { if (Truth(flags[(int)(long)i]!)) { object j = Mul(i, i); while (!Truth(Greater(j, n))) { flags[(int)(long)j] = F; j = Add(j, i); } } i = Add(i, 1L); }
        object c = 0L; i = 2L; while (!Truth(Greater(i, n))) { if (Truth(flags[(int)(long)i]!)) c = Add(c, 1L); i = Add(i, 1L); } return c;
    }
    public static long SieveTyped()
    {
        long n = 20000; var flags = new List<object?> { T }; for (int r = 0; r < 15; r++) flags.AddRange(flags.ToArray());
        long i = 2; while (!(i * i > n)) { if (Truth(flags[(int)i]!)) { long j = i * i; while (!(j > n)) { flags[(int)j] = F; j += i; } } i++; }
        long c = 0; i = 2; while (!(i > n)) { if (Truth(flags[(int)i]!)) c++; i++; } return c;
    }
}
static class P
{
    public static double Med(Func<object> f, int n = 60)
    {
        var t = new List<double>(); object? r = null;
        for (int k = 0; k < n; k++) { var sw = Stopwatch.StartNew(); r = f(); t.Add(sw.Elapsed.TotalMilliseconds); }
        var s = t.Skip(n / 2).OrderBy(x => x).ToList(); return s[s.Count / 2];
    }
    static void Row(string name, Func<object> boxed, Func<object> typed)
    {
        long a0 = GC.GetAllocatedBytesForCurrentThread(); boxed(); long a1 = GC.GetAllocatedBytesForCurrentThread(); typed(); long a2 = GC.GetAllocatedBytesForCurrentThread();
        Console.WriteLine($"{name,-18} boxed {Med(boxed),7:F3} ms ({(a1 - a0) / 1024,6} KB)   typed {Med(typed),7:F3} ms ({(a2 - a1) / 1024,5} KB)   result {boxed()} / {typed()}");
    }
    static void Main()
    {
        Row("while_sum_200k", B.WhileSumBoxed, () => B.WhileSumTyped());
        Row("fib_20", () => B.FibBoxed(20L), () => B.FibTyped(20));
        Row("double_math_100k", B.DoubleBoxed, () => B.DoubleTyped());
        Row("sieve_list_20k", B.SieveBoxed, () => B.SieveTyped());
        if (Environment.GetEnvironmentVariable("NO_EXPR") is null) FibExp.Run(Med);
        var bw = TypedClosure.BoxedWhileSum(); var tw = TypedClosure.TypedWhileSum(); var tf = TypedClosure.TypedFib();
        Console.WriteLine($"while_sum 박싱 클로저 {Med(() => bw()!):F3} ms, 타입 클로저 {Med(() => tw()!):F3} ms   fib_20 타입 클로저 {Med(() => tf()!):F3} ms   결과 {bw()} {tw()} {tf()}");
    }
}
