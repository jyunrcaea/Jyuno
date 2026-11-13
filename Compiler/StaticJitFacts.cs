using System.Linq.Expressions;
using Jyuno.Compiler;
using E = System.Linq.Expressions.Expression;

namespace Jyuno;

//[실험] 정적 타입 검사기가 증명한 타입으로 코드 만들기 (사실 모드)
//
//기본 추론(StaticJit.cs)은 흐름을 보지 않고 함수마다 시그니처가 하나라서, 타입이 섞이거나 여러 타입으로 부르는 함수는 object가 됨
//검사기(Compiler/StaticTypes.cs)는 흐름을 따르고 함수를 인자 타입마다 따로 검사(단형화)하므로, 그 결과를 쓰면
//  - 함수는 검사기의 인스턴스마다 따로 만듦 (input(int) -> string, input(string) -> string)
//  - 칸의 타입은 그 인스턴스에서 들어간 모든 값의 합 (null이 섞이지 않은 int/double/bool/string만 그 타입, 나머지는 object)
//  - 유니온 중 일부에서만 되는 연산(관대 단계의 "실행 중 확인")은 object 값으로 도우미를 불러 그 자리에서 확인
//검사기가 실패한 프로그램, 함수 안에 함수를 정의한 프로그램은 기본 추론을 씀
//검사기가 호출 대상을 모르는 자리(검사기가 닿지 않는다고 본 코드)는 모든 타입이 object인 인스턴스("*")를 부름
public partial class Interpreter
{
    internal sealed partial class StaticJit
    {
        CodegenFacts? facts;
        bool facts_mode;
        //지금 만드는 코드가 속한 검사기 인스턴스 ("" = 최상위)
        string ctx_key = "";
        bool nested_functions;
        bool needed_changed;
        readonly Dictionary<(IrStmt, string) , Frame> inst_frames = new();
        readonly Dictionary<(IrStmt, string) , Function> inst_functions = new();
        //함수마다 만들 인스턴스
        readonly Dictionary<IrStmt , List<string>> needed = new(ReferenceEqualityComparer.Instance);

        /// <summary>[실험] 사실 모드로 컴파일되었는지 (검사기의 타입을 썼는지)</summary>
        internal bool UsedFacts => facts_mode;

        const string Generic = "*";

        bool has_labels;

        static Ty ty_of(AType? t)
        {
            if (t is null) return Ty.Object;
            return t.Kinds switch {
                TK.Int => Ty.Long,
                TK.Real => Ty.Double,
                TK.Bool => Ty.Bool,
                TK.Str => Ty.String,
                TK.Int | TK.Null => Ty.LongN,
                TK.Real | TK.Null => Ty.DoubleN,
                TK.Bool | TK.Null => Ty.BoolN,
                TK.Str | TK.Null => Ty.StringN,
                _ => Ty.Object
            };
        }

        AType? expr_fact(IrExpr e) => ctx_key != Generic && facts!.Instances.TryGetValue(ctx_key , out CodegenFacts.Instance? inst) && inst.Exprs.TryGetValue(e , out AType? t) ? t : null;

        //분류가 끝난 뒤: 사실 모드를 쓸수 있으면 최상위 칸의 타입과 함수마다 만들 인스턴스를 정함
        bool setup_facts(Frame root)
        {
            if (facts is null || nested_functions || has_labels)
                return false;
            facts_mode = true;
            apply_types(root , CodegenFacts.Root , "");
            foreach (IrStmt def in functions.Keys)
                needed[def] = facts.Instances.Where(o => ReferenceEquals(o.Value.Func , def)).Select(o => o.Key).OrderBy(o => o , StringComparer.Ordinal).ToList();
            return true;
        }

        void apply_types(Frame frame , object key , string instance)
        {
            for (int i = 0 ; i < frame.Slots.Length ; i++)
            {
                Slot slot = frame.Slots[i];
                if (!slot.IsVar)
                    continue;
                var (summary , shared) = facts!.Slots.TryGetValue((key , i) , out var info) ? info : (AType.Bottom , false);
                AType? t = shared ? summary
                    : instance == Generic ? null
                    : facts.Instances.TryGetValue(instance , out CodegenFacts.Instance? inst) ? inst.Slots.GetValueOrDefault((key , i)) : null;
                slot.Type = t is null || t.IsBottom ? Ty.Object : ty_of(t);
            }
        }

        //인스턴스의 영역 (함수 본문, 또는 그 안/최상위의 for)
        Frame inst_frame(IrStmt s , string instance)
        {
            if (!inst_frames.TryGetValue((s , instance) , out Frame? frame))
            {
                Frame shape = frames[s];
                frame = new Frame(shape.Slots.Length , null);
                for (int i = 0 ; i < shape.Slots.Length ; i++)
                {
                    Slot a = shape.Slots[i] , b = frame.Slots[i];
                    b.IsFunc = a.IsFunc;
                    b.IsVar = a.IsVar;
                    b.HasConst = a.HasConst;
                    b.FuncDefs = a.FuncDefs;
                    b.Func = a.Func;
                    b.Type = a.Type;
                }
                apply_types(frame , s , instance);
                inst_frames[(s , instance)] = frame;
            }
            return frame;
        }

        Function inst_function(IrStmt def , string instance)
        {
            if (!inst_functions.TryGetValue((def , instance) , out Function? f))
            {
                Function shape = functions[def];
                f = new Function(def) { Arity = shape.Arity , Key = instance , Params = new Ty[shape.Arity] };
                if (instance == Generic || !facts!.Instances.TryGetValue(instance , out CodegenFacts.Instance? inst))
                {
                    Array.Fill(f.Params , Ty.Object);
                    f.Ret = Ty.Object;
                }
                else
                {
                    for (int i = 0 ; i < f.Arity ; i++)
                        f.Params[i] = i < inst.Args.Length ? (inst.Args[i].IsBottom ? Ty.Object : ty_of(inst.Args[i])) : Ty.Object;
                    f.Ret = inst.Ret.IsBottom ? Ty.Object : ty_of(inst.Ret);
                }
                inst_functions[(def , instance)] = f;
            }
            return f;
        }

        //이 호출이 부를 인스턴스: 검사기가 본 인스턴스 중 인자 타입이 나머지를 모두 담는 것 (반복문에서 넓어진 마지막 것), 없으면 "*"
        string call_target(IrCall call , IrStmt def)
        {
            string target = Generic;
            if (facts!.Calls.TryGetValue((ctx_key , call) , out HashSet<string>? keys))
            {
                if (keys.Count is 1)
                    target = keys.First();
                else
                {
                    foreach (string k in keys.OrderBy(o => o , StringComparer.Ordinal))
                    {
                        AType[] mine = facts.Instances[k].Args;
                        if (keys.All(other => covers(mine , facts.Instances[other].Args)))
                        {
                            target = k;
                            break;
                        }
                    }
                }
            }
            if (!needed[def].Contains(target))
            {
                needed[def].Add(target);
                needed_changed = true;
            }
            return target;
        }
        static bool covers(AType[] wide , AType[] narrow)
        {
            if (wide.Length != narrow.Length) return false;
            for (int i = 0 ; i < wide.Length ; i++)
                if (!narrow[i].SubsetOf(wide[i])) return false;
            return true;
        }

        //사실 모드의 함수 칸: 정의되었는지(Defined)와 인스턴스마다 대리자 변수
        void declare_function_slot(Slot slot , List<ParameterExpression> variables , List<E> body)
        {
            slot.Defined = E.Variable(typeof(bool) , "fd");
            variables.Add(slot.Defined);
            body.Add(E.Assign(slot.Defined , E.Constant(false)));
            slot.FuncVars = new(StringComparer.Ordinal);
            foreach (string key in needed[slot.Func!.Def])
            {
                ParameterExpression v = E.Variable(delegate_type(inst_function(slot.Func.Def , key)) , "f");
                slot.FuncVars[key] = v;
                variables.Add(v);
            }
        }

        E facts_direct_call(Slot slot , IrCall call)
        {
            string key = call_target(call , slot.Func!.Def);
            Function f = inst_function(slot.Func.Def , key);
            //이번에 처음 필요해진 인스턴스: 이번 결과는 버리고 다시 만듦 (변수만 임시로)
            if (!slot.FuncVars!.TryGetValue(key , out ParameterExpression? var))
                var = E.Variable(delegate_type(f) , "missing");
            List<E> args = new();
            List<E> extra = new();
            for (int k = 0 ; k < Math.Max(f.Arity , call.Arguments.Length) ; k++)
            {
                if (k < call.Arguments.Length)
                {
                    E a = expression(call.Arguments[k]);
                    if (k < f.Arity) args.Add(conv(a , clr(f.Params[k])));
                    else extra.Add(a);
                }
                else
                    args.Add(conv(E.Constant(null , typeof(object)) , clr(f.Params[k])));
            }
            E invoke;
            if (extra.Count is 0)
                invoke = E.Invoke(var , args);
            else
            {
                List<ParameterExpression> temps = args.Select(a => E.Variable(a.Type)).ToList();
                List<E> steps = new();
                for (int k = 0 ; k < temps.Count ; k++) steps.Add(E.Assign(temps[k] , args[k]));
                foreach (E x in extra) steps.Add(x);
                steps.Add(E.Invoke(var , temps));
                invoke = E.Block(temps , steps);
            }
            return E.Condition(slot.Defined! , invoke , fail(call.Missing , invoke.Type));
        }

        //func 문장: 필요한 인스턴스마다 람다를 만들어 대리자 변수에 넣고, 정의됨으로 표시
        E facts_function(IrStmt s)
        {
            Slot slot = stack[^1].Slots[s.Slot];
            List<E> steps = new();
            foreach (string key in needed[s].ToList())
            {
                if (!slot.FuncVars!.TryGetValue(key , out ParameterExpression? var))
                    continue; //다음 번에 만듦 (needed_changed)
                Function f = inst_function(s , key);
                string outer_key = ctx_key;
                ctx_key = key;
                steps.Add(E.Assign(var , function_lambda(s , f , inst_frame(s , key))));
                ctx_key = outer_key;
            }
            steps.Add(E.Assign(slot.Defined! , E.Constant(true)));
            return E.Block(typeof(void) , steps);
        }
    }
}
