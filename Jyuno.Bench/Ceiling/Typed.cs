// 클로저 엔진에서 박싱만 없앤 경우 (타입별 델리게이트 + 타입별 슬롯 배열): while_sum, fib
static class TypedClosure
{
    public sealed class Frame { public long[] L; public object?[] O; public Frame(int nl, int no) { L = new long[nl]; O = new object?[no]; } }
    delegate long LongE(Frame f);
    delegate bool BoolE(Frame f);
    delegate object? ObjE(Frame f);
    delegate void Stmt(Frame f);
    static readonly object T = true, F = false;

    // ---- 박싱 클로저 (지금 엔진과 비슷한 모양) ----
    static object Add(object x, object y) => x is long a && y is long b ? unchecked(a + b) : throw new Exception();
    static object Less(object x, object y) => x is long a && y is long b ? (a < b ? T : F) : throw new Exception();
    public static Func<object?> BoxedWhileSum()
    {
        ObjE i = f => f.O[0], s = f => f.O[1];
        ObjE c200k = _ => 200000L, c1 = _ => 1L;
        ObjE cond = f => Less(i(f)!, c200k(f)!);
        ObjE addSI = f => Add(s(f)!, i(f)!), addI1 = f => Add(i(f)!, c1(f)!);
        Stmt body = f => { f.O[1] = addSI(f); f.O[0] = addI1(f); };
        return () => { var fr = new Frame(0, 2); fr.O[0] = 0L; fr.O[1] = 0L; while ((bool)cond(fr)!) body(fr); return fr.O[1]; };
    }
    // ---- 타입 클로저 ----
    public static Func<object?> TypedWhileSum()
    {
        LongE i = f => f.L[0], s = f => f.L[1];
        LongE c200k = _ => 200000L, c1 = _ => 1L;
        BoolE cond = f => i(f) < c200k(f);
        LongE addSI = f => unchecked(s(f) + i(f)), addI1 = f => unchecked(i(f) + c1(f));
        Stmt body = f => { f.L[1] = addSI(f); f.L[0] = addI1(f); };
        return () => { var fr = new Frame(2, 0); while (cond(fr)) body(fr); return fr.L[1]; };
    }
    // fib: 타입 클로저 (인자 long, 반환 long), 호출마다 새 프레임
    public static Func<object?> TypedFib()
    {
        LongE fib = null!;
        LongE n = f => f.L[0];
        LongE c1 = _ => 1, c2 = _ => 2;
        BoolE lt = f => n(f) < c2(f);
        LongE sub1 = f => n(f) - c1(f), sub2 = f => n(f) - c2(f);
        int depth = 0;
        LongE call(LongE arg) => f => { long a = arg(f); if (depth >= 200) throw new Exception(); depth++; var fr = new Frame(1, 0); fr.L[0] = a; long r = fib(fr); depth--; return r; };
        LongE c_1 = call(sub1), c_2 = call(sub2);
        fib = f => lt(f) ? n(f) : unchecked(c_1(f) + c_2(f));
        LongE top = call(_ => 20);
        return () => top(new Frame(0, 0));
    }
}
