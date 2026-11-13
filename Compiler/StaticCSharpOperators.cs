using Jyuno.Compiler;

namespace Jyuno;

//C# 생성의 1.6.0 연산자 (IL 엔진의 Compiler/StaticJitOperators.cs와 같은 규칙)
public partial class Interpreter
{
    internal sealed partial class StaticJit
    {
        V operand_cs(IrExpr ir)
        {
            V e = cs_expression(ir);
            return e.Type == typeof(object) ? let(new V($"S.Paren({e.Code})" , typeof(object))) : e;
        }
        string operand_truth_cs(IrExpr ir)
        {
            V e = operand_cs(ir);
            return e.Type == typeof(bool) ? e.Code : $"S.Truth({cbox(e)})";
        }

        V operator_cs(IrOperator op)
        {
            IrExpr[] o = op.Operands;
            switch (op.Symbol)
            {
                case "&&" or "||":
                {
                    string r = "t" + next_id();
                    line($"bool {r} = {operand_truth_cs(o[0])};");
                    open(op.Symbol is "&&" ? $"if ({r})" : $"if (!{r})");
                    line($"{r} = {operand_truth_cs(o[1])};");
                    close();
                    return new V(r , typeof(bool) , true);
                }
                case "?:":
                {
                    Type t = clr(type_of(op));
                    string r = "t" + next_id();
                    line($"{tname(t)} {r} = default;");
                    open($"if ({operand_truth_cs(o[0])})");
                    line($"{r} = {cconv(operand_cs(o[1]) , t)};");
                    close();
                    open("else");
                    line($"{r} = {cconv(operand_cs(o[2]) , t)};");
                    close();
                    return new V(r , t , true);
                }
                case "??":
                {
                    V left = operand_cs(o[0]);
                    //null일수도 있는 타입 (사실 모드): 값이 있으면 그 값, 없으면 오른쪽
                    bool value_nullable = Nullable.GetUnderlyingType(left.Type) is not null;
                    if (value_nullable || left.Type == typeof(string) && nullable(type_of(o[0])))
                    {
                        Type t = clr(type_of(op));
                        string n = "t" + next_id();
                        line($"{tname(t)} {n};");
                        open($"if ({left.Code}{(value_nullable ? ".HasValue" : " != null")})");
                        line($"{n} = {cconv(left , t)};");
                        close();
                        open("else");
                        line($"{n} = {cconv(operand_cs(o[1]) , t)};");
                        close();
                        return new V(n , t , true);
                    }
                    if (left.Type != typeof(object))
                        return left;
                    string r = "t" + next_id();
                    line($"object {r} = {left.Code};");
                    open($"if ({r} == null)");
                    line($"{r} = {cbox(operand_cs(o[1]))};");
                    close();
                    return new V(r , typeof(object) , true);
                }
            }
            int code = (int)OperatorOf(op.Symbol);
            Ty result = type_of(op);
            if (o.Length is 1)
            {
                V x = operand_cs(o[0]);
                Ty tx = norm(type_of(o[0]));
                if (unary_fast_cs(op.Symbol , x , tx) is V quick)
                    return quick;
                if (facts_mode && nullable(tx) && unary_fast_cs(op.Symbol , unwrap_cs(x) , non_null(tx)) is not null)
                    return guarded_cs(x , tx , new V("0L" , typeof(long) , true) , Ty.Long , clr(result) , (v , _) => unary_fast_cs(op.Symbol , v , non_null(tx)) ,
                        (v , _) => new V($"S.OpUnary({code} , {cbox(v)})" , typeof(object)));
                string generic1 = $"S.OpUnary({code} , {cbox(x)})";
                return result is Ty.Bool ? new V($"((bool){generic1})" , typeof(bool)) : new V(generic1 , typeof(object));
            }
            V a = operand_cs(o[0]), b = operand_cs(o[1]);
            Ty ta = norm(type_of(o[0])), tb = norm(type_of(o[1]));
            if (operator_fast_cs(op.Symbol , a , ta , b , tb) is V fast)
                return fast;
            if (facts_mode && (nullable(ta) || nullable(tb)))
            {
                Ty na = non_null(ta) , nb = non_null(tb);
                if (na is Ty.String && nb is Ty.String && op.Symbol is "+" && (ta is Ty.String || tb is Ty.String))
                    return new V(cconv(operator_fast_cs("+" , a , na , b , nb)!.Value , clr(result)) , clr(result));
                if (operator_fast_cs(op.Symbol , unwrap_cs(a) , na , unwrap_cs(b) , nb) is not null)
                    return guarded_cs(a , ta , b , tb , clr(result) , (x , y) => operator_fast_cs(op.Symbol , x , na , y , nb) ,
                        (x , y) => new V($"S.OpBinary({code} , {cbox(x)} , {cbox(y)})" , typeof(object)));
            }
            string generic = $"S.OpBinary({code} , {cbox(a)} , {cbox(b)})";
            return result is Ty.Bool ? new V($"((bool){generic})" , typeof(bool)) : new V(generic , typeof(object));
        }

        static V? unary_fast_cs(string symbol , V x , Ty tx) => symbol switch {
            "u!" when tx is Ty.Bool => new V($"(!{x.Code})" , typeof(bool)),
            "u-" when tx is Ty.Long or Ty.Double => new V($"unchecked(-({x.Code}))" , x.Type),
            "u+" when tx is Ty.Long or Ty.Double => x,
            "u~" when tx is Ty.Long => new V($"(~{x.Code})" , typeof(long)),
            _ => null
        };

        static V? operator_fast_cs(string symbol , V a , Ty ta , V b , Ty tb)
        {
            string p = a.Code, q = b.Code;
            if (ta is Ty.Long && tb is Ty.Long)
            {
                switch (symbol)
                {
                    case "+" or "-" or "*" or "&" or "|" or "^": return new V($"unchecked({p} {symbol} {q})" , typeof(long));
                    case "/": return new V($"S.OpDivLL({p} , {q})" , typeof(long));
                    case "%": return new V($"S.OpModLL({p} , {q})" , typeof(long));
                    case "<<" or ">>": return new V($"unchecked({p} {symbol} (int)({q} & 63L))" , typeof(long));
                    case "<" or ">" or "<=" or ">=" or "==" or "!=": return new V($"({p} {symbol} {q})" , typeof(bool));
                }
            }
            else if (numeric(ta) && numeric(tb) && symbol is "+" or "-" or "*" or "/" or "%")
            {
                string dp = ta is Ty.Double ? p : $"(double){p}";
                string dq = tb is Ty.Double ? q : $"(double){q}";
                if (tb is Ty.Long && symbol is "/") return new V($"S.OpDivDL({dp} , {q})" , typeof(double));
                if (tb is Ty.Long && symbol is "%") return new V($"S.OpModDL({dp} , {q})" , typeof(double));
                return new V($"({dp} {symbol} {dq})" , typeof(double));
            }
            else if (ta is Ty.String && tb is Ty.String && symbol is "+")
                return new V($"string.Concat({p} , {q})" , typeof(string));
            else if (ta is Ty.Bool && tb is Ty.Bool)
            {
                switch (symbol)
                {
                    case "&" or "|" or "==" or "!=": return new V($"({p} {symbol} {q})" , typeof(bool));
                    case "^": return new V($"({p} != {q})" , typeof(bool));
                }
            }
            return null;
        }
    }

    public static partial class AotSupport
    {
        public static object? OpBinary(int op , object? x , object? y) => StaticBinary((Op)op , x , y);
        public static object? OpUnary(int op , object? x) => StaticUnary((Op)op , x);
        public static long OpDivLL(long a , long b) => StaticJit.op_div_ll(a , b);
        public static long OpModLL(long a , long b) => StaticJit.op_mod_ll(a , b);
        public static double OpDivDL(double a , long b) => StaticJit.op_div_dl(a , b);
        public static double OpModDL(double a , long b) => StaticJit.op_mod_dl(a , b);
    }
}
