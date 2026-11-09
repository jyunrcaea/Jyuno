using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using E = System.Linq.Expressions.Expression;
static class FibExp
{
    public class Counter { public int Depth; }
    public static void Run(Func<Func<object>, int, double> med)
    {
        // C# 람다 재귀 (캡처된 델리게이트)
        Func<long, long>? f = null;
        var c = new Counter();
        f = n => { if (c.Depth >= 200) throw new Exception(); c.Depth++; long r = n < 2 ? n : f!(n - 1) + f!(n - 2); c.Depth--; return r; };
        Console.WriteLine($"C# 람다 재귀            {med(() => f(20), 60):F3} ms");
        foreach (bool hoist in new[] { false, true })
        foreach (bool depth in new[] { false, true })
        {
            // 식 트리: main 안에서 fib 람다를 만들어 변수에 넣고 호출
            var fib = E.Variable(typeof(Func<long, long>), "fib");
            var n = E.Parameter(typeof(long), "n");
            var counter = new Counter();
            var d = E.Field(E.Constant(counter), "Depth");
            E body = E.Condition(E.LessThan(n, E.Constant(2L)), n, E.Add(E.Invoke(fib, E.Subtract(n, E.Constant(1L))), E.Invoke(fib, E.Subtract(n, E.Constant(2L)))));
            if (depth)
            {
                var r = E.Variable(typeof(long), "r");
                body = E.Block(typeof(long), new[] { r }, E.IfThen(E.GreaterThanOrEqual(d, E.Constant(200)), E.Throw(E.New(typeof(Exception)))), E.PreIncrementAssign(d), E.Assign(r, body), E.PreDecrementAssign(d), r);
            }
            var lam = E.Lambda<Func<long, long>>(body, n);
            Func<long, long> compiled;
            if (hoist)
            {
                var main = E.Lambda<Func<long, long>>(E.Block(new[] { fib }, E.Assign(fib, lam), E.Invoke(fib, E.Constant(20L))), E.Parameter(typeof(long)));
                var m = main.Compile();
                Console.WriteLine($"식 트리 중첩 람다 depth={depth,-5} {med(() => m(0), 60):F3} ms");
            }
            else
            {
                // fib 변수를 StrongBox 상수로 (중첩 람다 없이)
                var box = new StrongBox<Func<long, long>>();
                var body2 = new ReplaceVar(fib, E.Field(E.Constant(box), "Value")).Visit(body);
                box.Value = E.Lambda<Func<long, long>>(body2, n).Compile();
                Console.WriteLine($"식 트리 단독 람다 depth={depth,-5} {med(() => box.Value(20), 60):F3} ms");
            }
        }
    }
    class ReplaceVar : ExpressionVisitor { readonly ParameterExpression p; readonly E r; public ReplaceVar(ParameterExpression p, E r) { this.p = p; this.r = r; } protected override E VisitParameter(ParameterExpression node) => node == p ? r : node; }
}
