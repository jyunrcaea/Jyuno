using System.Linq.Expressions;
using System.Reflection;
using Jyuno.Compiler;
using E = System.Linq.Expressions.Expression;

namespace Jyuno;

//IL 엔진의 1.6.0 연산자 (Compiler/OperatorCompiler.cs와 같은 계산)
//두 값의 타입을 알면 바로 계산하고, 모르면 인터프리터와 같은 도우미(StaticBinary/StaticUnary)를 부름
//구조체는 제한 모드에서 거부되므로 연산자를 다시 정의한 경우는 없음
public partial class Interpreter
{
    internal sealed partial class StaticJit
    {
        static readonly MethodInfo
            M_OpBinary = method(nameof(op_binary)),
            M_OpUnary = method(nameof(op_unary)),
            M_OpDivLL = method(nameof(op_div_ll)),
            M_OpModLL = method(nameof(op_mod_ll)),
            M_OpDivDL = method(nameof(op_div_dl)),
            M_OpModDL = method(nameof(op_mod_dl)),
            M_OpTruth = method(nameof(op_truth));

        static bool is_compare(string symbol) => symbol is "<" or ">" or "<=" or ">=" or "==" or "!=";

        //연산자 식의 타입 (모르는 값이 있으면 아직 모름)
        Ty operator_type(IrOperator op)
        {
            Ty[] ts = Array.ConvertAll(op.Operands , type_of);
            switch (op.Symbol)
            {
                case "&&" or "||" or "u!":
                    return Ty.Bool;
                case "??":
                    //null이 아닌 타입의 값이라면 왼쪽 그대로 (문자열 타입은 상수나 연결에서만 생기므로 null이 아님)
                    if (ts[0] is Ty.Long or Ty.Double or Ty.Bool or Ty.String)
                        return ts[0];
                    return ts[0] is Ty.Bottom || ts[1] is Ty.Bottom ? Ty.Bottom : Ty.Object;
                case "?:":
                    return join(ts[1] , ts[2]);
            }
            if (is_compare(op.Symbol))
                return Ty.Bool;
            if (ts.Contains(Ty.Bottom))
                return Ty.Bottom;
            if (ts.Length is 1)
            {
                Ty t = ts[0];
                return op.Symbol switch {
                    "u-" or "u+" when t is Ty.Long or Ty.Double => t,
                    "u~" when t is Ty.Long => Ty.Long,
                    _ => Ty.Object
                };
            }
            Ty a = ts[0], b = ts[1];
            switch (op.Symbol)
            {
                case "+" or "-" or "*" or "/" or "%":
                    if (a is Ty.Long && b is Ty.Long) return Ty.Long;
                    if (numeric(a) && numeric(b)) return Ty.Double;
                    if (op.Symbol is "+" && a is Ty.String && b is Ty.String) return Ty.String;
                    return Ty.Object;
                case "&" or "|" or "^":
                    if (a is Ty.Long && b is Ty.Long) return Ty.Long;
                    if (a is Ty.Bool && b is Ty.Bool) return Ty.Bool;
                    return Ty.Object;
                case "<<" or ">>":
                    return a is Ty.Long && b is Ty.Long ? Ty.Long : Ty.Object;
                default:
                    return Ty.Object;
            }
        }

        //피연산자: 괄호 안의 식이 ')' 전에 끝났다면(EarlyEnd) 오류
        E operand(IrExpr ir)
        {
            E e = expression(ir);
            return e.Type == typeof(object) ? E.Call(M_Paren , e) : e;
        }
        E operand_truth(IrExpr ir)
        {
            E e = operand(ir);
            return e.Type == typeof(bool) ? e : E.Call(M_OpTruth , boxed(e));
        }

        E operator_expression(IrOperator op)
        {
            IrExpr[] o = op.Operands;
            switch (op.Symbol)
            {
                case "&&":
                    return E.Condition(operand_truth(o[0]) , operand_truth(o[1]) , E.Constant(false));
                case "||":
                    return E.Condition(operand_truth(o[0]) , E.Constant(true) , operand_truth(o[1]));
                case "?:":
                {
                    Type t = clr(type_of(op));
                    return E.Condition(operand_truth(o[0]) , conv(operand(o[1]) , t) , conv(operand(o[2]) , t));
                }
                case "??":
                {
                    E left = operand(o[0]);
                    if (left.Type != typeof(object))
                        return left;
                    ParameterExpression v = E.Variable(typeof(object) , "v");
                    return E.Block(typeof(object) , new[] { v } ,
                        E.Assign(v , left) ,
                        E.Condition(E.NotEqual(v , E.Constant(null)) , v , boxed(operand(o[1]))));
                }
            }
            Op code = OperatorOf(op.Symbol);
            Ty result = type_of(op);
            if (o.Length is 1)
            {
                E x = operand(o[0]);
                Ty tx = norm(type_of(o[0]));
                switch (op.Symbol)
                {
                    case "u!" when tx is Ty.Bool: return E.Not(x);
                    case "u-" when tx is Ty.Long or Ty.Double: return E.Negate(x);
                    case "u+" when tx is Ty.Long or Ty.Double: return x;
                    case "u~" when tx is Ty.Long: return E.OnesComplement(x);
                }
                E generic1 = E.Call(M_OpUnary , E.Constant(code) , boxed(x));
                return result is Ty.Bool ? E.Convert(generic1 , typeof(bool)) : generic1;
            }
            E a = operand(o[0]), b = operand(o[1]);
            Ty ta = norm(type_of(o[0])), tb = norm(type_of(o[1]));
            if (ta is Ty.Long && tb is Ty.Long)
            {
                switch (op.Symbol)
                {
                    case "+": return E.Add(a , b);
                    case "-": return E.Subtract(a , b);
                    case "*": return E.Multiply(a , b);
                    case "/": return E.Call(M_OpDivLL , a , b);
                    case "%": return E.Call(M_OpModLL , a , b);
                    case "&": return E.And(a , b);
                    case "|": return E.Or(a , b);
                    case "^": return E.ExclusiveOr(a , b);
                    case "<<": return E.LeftShift(a , E.Convert(E.And(b , E.Constant(63L)) , typeof(int)));
                    case ">>": return E.RightShift(a , E.Convert(E.And(b , E.Constant(63L)) , typeof(int)));
                    case "<": return E.LessThan(a , b);
                    case ">": return E.GreaterThan(a , b);
                    case "<=": return E.LessThanOrEqual(a , b);
                    case ">=": return E.GreaterThanOrEqual(a , b);
                    case "==": return E.Equal(a , b);
                    case "!=": return E.NotEqual(a , b);
                }
            }
            else if (numeric(ta) && numeric(tb) && op.Symbol is "+" or "-" or "*" or "/" or "%")
            {
                E da = ta is Ty.Double ? a : E.Convert(a , typeof(double));
                E db = tb is Ty.Double ? b : E.Convert(b , typeof(double));
                switch (op.Symbol)
                {
                    case "+": return E.Add(da , db);
                    case "-": return E.Subtract(da , db);
                    case "*": return E.Multiply(da , db);
                    case "/": return tb is Ty.Long ? E.Call(M_OpDivDL , da , b) : E.Divide(da , db);
                    case "%": return tb is Ty.Long ? E.Call(M_OpModDL , da , b) : E.Modulo(da , db);
                }
            }
            else if (ta is Ty.String && tb is Ty.String && op.Symbol is "+")
                return E.Call(M_Concat2 , a , b);
            else if (ta is Ty.Bool && tb is Ty.Bool)
            {
                switch (op.Symbol)
                {
                    case "&": return E.And(a , b);
                    case "|": return E.Or(a , b);
                    case "^": return E.NotEqual(a , b);
                    case "==": return E.Equal(a , b);
                    case "!=": return E.NotEqual(a , b);
                }
            }
            E generic = E.Call(M_OpBinary , E.Constant(code) , boxed(a) , boxed(b));
            return result is Ty.Bool ? E.Convert(generic , typeof(bool)) : generic;
        }

        //─────────── 실행 중 도우미 ───────────

        internal static object? op_binary(Op op , object? x , object? y) => StaticBinary(op , x , y);
        internal static object? op_unary(Op op , object? x) => StaticUnary(op , x);
        internal static bool op_truth(object? value) => value is bool b ? b : Parser.IsTrue(value);
        //0으로 나누기, long.MinValue / -1은 내장 함수와 같은 오류
        internal static long op_div_ll(long a , long b) => b != 0 && !(a == long.MinValue && b == -1) ? a / b : (long)StaticBinary(Op.Div , a , b)!;
        internal static long op_mod_ll(long a , long b) => b != 0 && !(a == long.MinValue && b == -1) ? a % b : (long)StaticBinary(Op.Mod , a , b)!;
        internal static double op_div_dl(double a , long b) => b != 0 ? a / b : (double)StaticBinary(Op.Div , a , b)!;
        internal static double op_mod_dl(double a , long b) => b != 0 ? a % b : (double)StaticBinary(Op.Mod , a , b)!;
    }
}
