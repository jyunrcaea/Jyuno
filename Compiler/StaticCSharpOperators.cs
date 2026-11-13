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
                switch (op.Symbol)
                {
                    case "u!" when tx is Ty.Bool: return new V($"(!{x.Code})" , typeof(bool));
                    case "u-" when tx is Ty.Long or Ty.Double: return new V($"unchecked(-({x.Code}))" , x.Type);
                    case "u+" when tx is Ty.Long or Ty.Double: return x;
                    case "u~" when tx is Ty.Long: return new V($"(~{x.Code})" , typeof(long));
                }
                string generic1 = $"S.OpUnary({code} , {cbox(x)})";
                return result is Ty.Bool ? new V($"((bool){generic1})" , typeof(bool)) : new V(generic1 , typeof(object));
            }
            V a = operand_cs(o[0]), b = operand_cs(o[1]);
            Ty ta = norm(type_of(o[0])), tb = norm(type_of(o[1]));
            string p = a.Code, q = b.Code;
            if (ta is Ty.Long && tb is Ty.Long)
            {
                switch (op.Symbol)
                {
                    case "+" or "-" or "*" or "&" or "|" or "^": return new V($"unchecked({p} {op.Symbol} {q})" , typeof(long));
                    case "/": return new V($"S.OpDivLL({p} , {q})" , typeof(long));
                    case "%": return new V($"S.OpModLL({p} , {q})" , typeof(long));
                    case "<<" or ">>": return new V($"unchecked({p} {op.Symbol} (int)({q} & 63L))" , typeof(long));
                    case "<" or ">" or "<=" or ">=" or "==" or "!=": return new V($"({p} {op.Symbol} {q})" , typeof(bool));
                }
            }
            else if (numeric(ta) && numeric(tb) && op.Symbol is "+" or "-" or "*" or "/" or "%")
            {
                string dp = ta is Ty.Double ? p : $"(double){p}";
                string dq = tb is Ty.Double ? q : $"(double){q}";
                if (tb is Ty.Long && op.Symbol is "/") return new V($"S.OpDivDL({dp} , {q})" , typeof(double));
                if (tb is Ty.Long && op.Symbol is "%") return new V($"S.OpModDL({dp} , {q})" , typeof(double));
                return new V($"({dp} {op.Symbol} {dq})" , typeof(double));
            }
            else if (ta is Ty.String && tb is Ty.String && op.Symbol is "+")
                return new V($"string.Concat({p} , {q})" , typeof(string));
            else if (ta is Ty.Bool && tb is Ty.Bool)
            {
                switch (op.Symbol)
                {
                    case "&" or "|" or "==" or "!=": return new V($"({p} {op.Symbol} {q})" , typeof(bool));
                    case "^": return new V($"({p} != {q})" , typeof(bool));
                }
            }
            string generic = $"S.OpBinary({code} , {cbox(a)} , {cbox(b)})";
            return result is Ty.Bool ? new V($"((bool){generic})" , typeof(bool)) : new V(generic , typeof(object));
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
