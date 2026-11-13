using System.Linq.Expressions;
using System.Reflection;
using Jyuno.Analysis;
using Jyuno.Compiler;
using Jyuno.Language;
using E = System.Linq.Expressions.Expression;

namespace Jyuno;

//[프로토타입] 제한 모드 IR → 타입 추론 → .NET 식 트리(IL) 컴파일
//
//클로저 엔진(StaticEngine)이 Native JS보다 느린 두 가지 이유를 없애는 실험
//  1. 값이 모두 object라서 정수/실수 연산마다 박싱(힙 할당)이 생김
//     → 제한 모드에서는 이름이 사라지거나 내장 함수가 바뀌지 않으므로, 변수/매개변수/반환값의 타입을 미리 정할수 있음
//       (흐름을 보지 않는 추론: 변수에 대입되는 모든 값의 타입이 같다면 그 타입, 아니면 object)
//  2. 식 하나마다 델리게이트 호출 → 프로그램 전체를 IL로 만들어 RyuJIT이 레지스터, 인라인, 루프 최적화를 하게 함
//지원하지 않는 IR(goto, 레이블, const, local, 후보가 여러 개인 이름 등)이 있으면 null을 돌려주고 클로저 엔진을 사용
public partial class Interpreter
{
    internal sealed partial class StaticJit
    {
        enum Ty : byte { Bottom, Long, Double, Bool, String, Object }
        static Ty join(Ty a , Ty b) => a == Ty.Bottom ? b : b == Ty.Bottom ? a : a == b ? a : Ty.Object;
        static Ty norm(Ty t) => t == Ty.Bottom ? Ty.Object : t;
        static Type clr(Ty t) => norm(t) switch {
            Ty.Long => typeof(long),
            Ty.Double => typeof(double),
            Ty.Bool => typeof(bool),
            Ty.String => typeof(string),
            _ => typeof(object)
        };
        static bool numeric(Ty t) => t is Ty.Long or Ty.Double;

        sealed class Unsupported : Exception { public Unsupported(string message) : base(message) { } }

        sealed class Slot
        {
            public bool IsFunc, IsVar, HasConst;
            public int FuncDefs;
            public Function? Func;
            public Ty Type;
            public ParameterExpression? Var, Defined, Const; //Const: const로 정의되었는지 (실행 중)
            public int Id; //C# 생성: 변수 이름 번호
        }
        sealed class Frame
        {
            public Frame(int size , Function? function) { Slots = new Slot[size]; for (int i = 0 ; i < size ; i++) Slots[i] = new(); Function = function; }
            public readonly Slot[] Slots;
            public readonly Function? Function; //이 영역이 속한 함수 (최상위라면 null)
        }
        sealed class Function
        {
            public Function(IrStmt def) { Def = def; }
            public readonly IrStmt Def;
            public int Arity;
            public Ty[] Params = Array.Empty<Ty>();
            public Ty Ret;
            //codegen
            public ParameterExpression[] Parameters = Array.Empty<ParameterExpression>();
            public LabelTarget? Return;
            public Type? DelegateType;
            public int Id; //C# 생성
        }

        sealed class Counter { public int Depth; }
        //디버그용: 마지막으로 만든 식 트리
        internal static LambdaExpression? LastLambda;

        public StaticJit(IrProgram program , object?[] imports)
        {
            this.program = program;
            this.imports = imports;
        }
        readonly IrProgram program;
        readonly object?[] imports;
        readonly Dictionary<object , Frame> frames = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<IrStmt , Function> functions = new(ReferenceEqualityComparer.Instance);
        readonly List<Frame> stack = new();
        Function? current;
        bool changed;
        static readonly object RootKey = new();
        //C# 생성(AOT): 호스트 객체 없이 가져오기 표만 보고 만듦 (실행할 때 연결 검사가 같은 가정을 확인)
        bool aot;

        /// <summary>컴파일할수 없는 프로그램이라면 null (이유는 reason)</summary>
        public static Func<object?>? TryCompile(IrProgram program , object?[] imports , bool interpret , out string? reason)
        {
            try
            {
                StaticJit jit = new(program , imports);
                reason = null;
                return jit.compile(interpret);
            }
            catch (Unsupported e)
            {
                reason = e.Message;
                return null;
            }
        }

        Frame frame_of(object key , int size , Function? function)
        {
            if (!frames.TryGetValue(key , out Frame? frame))
                frames[key] = frame = new Frame(size , function);
            return frame;
        }
        Slot slot_at(int hops , int slot)
        {
            if (hops >= stack.Count)
                throw new Unsupported("영역 범위");
            return stack[stack.Count - 1 - hops].Slots[slot];
        }
        //이름이 가리키는 칸 후보 (안쪽 영역부터)
        Slot[] slots_of(IrName name) => Array.ConvertAll(name.Candidates , c => slot_at(c.Hops , c.Slot));

        //함수를 가리킬수 있는 이름은 후보가 하나이고 전역이 없어야 함 (직접 호출)
        Slot? function_slot(IrName name)
        {
            Slot[] slots = slots_of(name);
            if (!slots.Any(o => o.IsFunc))
                return null;
            if (slots.Length > 1 || name.Import >= 0)
                throw new Unsupported($"함수 '{name.Name}'의 후보가 여러 개");
            return slots[0];
        }

        //─────────── 1. 칸 분류 ───────────

        void classify_block(IrBlock block)
        {
            foreach (IrStmt s in block.Statements)
                classify(s);
        }
        void classify(IrStmt s)
        {
            foreach (IrExpr e in s.Arguments)
                classify(e);
            switch (s.Kind)
            {
                case IrKind.Goto:
                    throw new Unsupported("goto");
                case IrKind.Local:
                    stack[^1].Slots[s.Slot].IsVar = true;
                    if (s.Expression is not null) classify(s.Expression);
                    break;
                case IrKind.Expression:
                    classify(s.Expression!);
                    break;
                case IrKind.If:
                    classify_block(s.Body!);
                    if (s.Else is not null) classify_block(s.Else);
                    break;
                case IrKind.While:
                case IrKind.Repeat:
                    classify_block(s.Body!);
                    break;
                case IrKind.For:
                {
                    Frame inner = frame_of(s , s.FrameSize , current);
                    stack.Add(inner);
                    inner.Slots[s.Slot].IsVar = true;
                    classify_block(s.Body!);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                }
                case IrKind.Func:
                {
                    Slot slot = stack[^1].Slots[s.Slot];
                    slot.IsFunc = true;
                    slot.FuncDefs++;
                    Function function = new(s);
                    functions[s] = function;
                    slot.Func = function;
                    Function? outer = current;
                    current = function;
                    stack.Add(frame_of(s , s.FrameSize , function));
                    classify_block(s.Body!);
                    stack.RemoveAt(stack.Count - 1);
                    current = outer;
                    break;
                }
                case IrKind.Param:
                    stack[^1].Slots[s.Slot].IsVar = true;
                    current!.Arity = Math.Max(current.Arity , s.ParameterIndex + 1);
                    break;
            }
        }
        void classify(IrExpr e)
        {
            switch (e)
            {
                case IrRead read:
                    slots_of(read.Name);
                    break;
                case IrCall call:
                    slots_of(call.Name);
                    foreach (IrExpr a in call.Arguments) classify(a);
                    break;
                case IrIntrinsic intrinsic:
                    foreach (IrExpr a in intrinsic.Arguments) classify(a);
                    break;
                case IrAssign assign:
                {
                    Slot[] slots = slots_of(assign.Name);
                    if (slots.Length is 0)
                    {
                        if (assign.Name.Import < 0) throw new Unsupported("만들 칸이 없는 대입");
                    }
                    else
                    {
                        if (!assign.Name.Candidates.Contains((0 , assign.Create))) throw new Unsupported("만들 칸이 후보에 없는 대입");
                        foreach (Slot slot in slots) slot.IsVar = true;
                    }
                    classify(assign.Value);
                    break;
                }
                case IrParen paren:
                    classify(paren.Inner);
                    break;
                case IrConstDef definition:
                    stack[^1].Slots[definition.Slot].IsVar = true;
                    stack[^1].Slots[definition.Slot].HasConst = true;
                    break;
                case IrLabel label:
                    stack[^1].Slots[label.Slot].IsVar = true;
                    break;
            }
        }

        //─────────── 2. 타입 추론 (고정점까지 반복) ───────────

        void widen(ref Ty target , Ty value)
        {
            Ty joined = join(target , value);
            if (joined != target)
            {
                target = joined;
                changed = true;
            }
        }

        static bool falls_through(IrBlock block)
        {
            if (block.Statements.Length is 0)
                return true;
            IrStmt last = block.Statements[^1];
            if (last.Kind is IrKind.Return)
                return false;
            if (last.Kind is IrKind.If && last.Else is not null)
                return falls_through(last.Body!) || falls_through(last.Else);
            return true;
        }

        //문장 값이 ReturnInfo/GrammarError일수 있어서 확인해야 하는지 (Jyuno 내장 함수와 대입은 아님)
        bool needs_check(IrExpr e , bool inferring)
        {
            switch (e)
            {
                case IrAssign:
                case IrIntrinsic:
                case IrConstDef:
                case IrLabel:
                    return false;
                case IrCall call when !aot && call.Name.Candidates.Length is 0 && call.Name.Import >= 0 && imports[call.Name.Import] is NativeFunction { IsJyunoBuiltin: true }:
                    return false;
                case IrParen paren:
                    return paren.Closed && needs_check(paren.Inner , inferring);
            }
            Ty t = type_of(e);
            return inferring ? t is Ty.Object : norm(t) is Ty.Object;
        }

        void infer_block(IrBlock block)
        {
            foreach (IrStmt s in block.Statements)
                infer(s);
        }
        void infer(IrStmt s)
        {
            Ty first = Ty.Bottom;
            for (int i = 0 ; i < s.Arguments.Length ; i++)
            {
                Ty t = type_of(s.Arguments[i]);
                if (i is 0) first = t;
            }
            switch (s.Kind)
            {
                case IrKind.Expression:
                    type_of(s.Expression!);
                    if (current is not null && needs_check(s.Expression! , true))
                        widen(ref current.Ret , Ty.Object);
                    break;
                case IrKind.If:
                    infer_block(s.Body!);
                    if (s.Else is not null) infer_block(s.Else);
                    break;
                case IrKind.While:
                case IrKind.Repeat:
                    infer_block(s.Body!);
                    break;
                case IrKind.For:
                {
                    Frame inner = frames[s];
                    if (!(s.Arguments.Length is 1 && first is Ty.Bottom))
                        widen(ref inner.Slots[s.Slot].Type , s.Arguments.Length is 1 && first is Ty.String ? Ty.String : Ty.Object);
                    stack.Add(inner);
                    infer_block(s.Body!);
                    stack.RemoveAt(stack.Count - 1);
                    break;
                }
                case IrKind.Func:
                {
                    Function function = functions[s];
                    Function? outer = current;
                    current = function;
                    stack.Add(frames[s]);
                    infer_block(s.Body!);
                    stack.RemoveAt(stack.Count - 1);
                    current = outer;
                    if (falls_through(s.Body!))
                        widen(ref function.Ret , Ty.Object);
                    break;
                }
                case IrKind.Local:
                    widen(ref stack[^1].Slots[s.Slot].Type , s.Expression is null ? Ty.Object : type_of(s.Expression));
                    break;
                case IrKind.Return:
                    if (current is not null)
                        widen(ref current.Ret , s.Arguments.Length is 0 ? Ty.Object : first);
                    break;
                case IrKind.Param:
                {
                    if (current!.Params.Length < current.Arity)
                    {
                        Ty[] grown = new Ty[current.Arity];
                        current.Params.CopyTo(grown , 0);
                        current.Params = grown;
                    }
                    widen(ref stack[^1].Slots[s.Slot].Type , current.Params[s.ParameterIndex]);
                    break;
                }
            }
        }

        //식의 타입 (대입과 함수 호출 인자의 타입을 변수/매개변수에 모음)
        Ty type_of(IrExpr e)
        {
            switch (e)
            {
                case IrConst c:
                    return c.Value switch {
                        long => Ty.Long,
                        double => Ty.Double,
                        bool => Ty.Bool,
                        string => Ty.String,
                        _ => Ty.Object
                    };
                case IrRead read:
                {
                    Slot[] slots = slots_of(read.Name);
                    if (slots.Length > 1 && slots.Any(o => o.IsFunc))
                        throw new Unsupported($"함수 '{read.Name.Name}'의 후보가 여러 개");
                    if (slots.Length is 0 || slots.Any(o => !o.IsVar))
                        return Ty.Object;
                    Ty t = read.Name.Import >= 0 ? Ty.Object : Ty.Bottom;
                    foreach (Slot slot in slots) t = join(t , slot.Type);
                    return t;
                }
                case IrIntrinsic intrinsic:
                {
                    Ty[] ts = Array.ConvertAll(intrinsic.Arguments , type_of);
                    //아직 타입을 모르는 값이 있으면 결과도 아직 모름 (고정점 반복에서 정해짐, 처음부터 object로 넓히지 않음)
                    if (ts.Contains(Ty.Bottom))
                        return Ty.Bottom;
                    return intrinsic_type(program.Imports[intrinsic.Import].Intrinsic , ts);
                }
                case IrCall call:
                {
                    Ty[] ts = Array.ConvertAll(call.Arguments , type_of);
                    Slot? slot = function_slot(call.Name);
                    if (slot is not null)
                    {
                        Function function = slot.Func!;
                        if (function.Params.Length < function.Arity)
                        {
                            Ty[] grown = new Ty[function.Arity];
                            function.Params.CopyTo(grown , 0);
                            function.Params = grown;
                        }
                        for (int k = 0 ; k < function.Arity ; k++)
                            widen(ref function.Params[k] , k < ts.Length ? ts[k] : Ty.Object);
                        return function.Ret;
                    }
                    return Ty.Object;
                }
                case IrAssign assign:
                {
                    Ty value = type_of(assign.Value);
                    foreach (Slot slot in slots_of(assign.Name))
                        widen(ref slot.Type , value);
                    return Ty.Object;
                }
                case IrParen paren:
                {
                    Ty inner = type_of(paren.Inner);
                    return paren.Closed ? inner : Ty.Object;
                }
                case IrConstDef definition:
                    widen(ref stack[^1].Slots[definition.Slot].Type , type_of(new IrConst(definition.Value)));
                    return Ty.Object;
                case IrLabel label:
                    //레이블의 값은 인터프리터와 같이 int (Jyuno의 정수 타입이 아니므로 object)
                    widen(ref stack[^1].Slots[label.Slot].Type , Ty.Object);
                    return Ty.Object;
                default:
                    throw new Unsupported(e.Op.ToString());
            }
        }

        static bool comparison(Intrinsic op) => op is Intrinsic.Equal or Intrinsic.Less or Intrinsic.Greater;

        static Ty binary_type(Intrinsic op , Ty a , Ty b)
        {
            if (comparison(op))
                return Ty.Bool;
            if (a is Ty.Long && b is Ty.Long)
                return op is Intrinsic.Add or Intrinsic.Sub or Intrinsic.Mul or Intrinsic.Div or Intrinsic.Mod or Intrinsic.And or Intrinsic.Or ? Ty.Long : Ty.Object;
            if (numeric(a) && numeric(b))
                return op is Intrinsic.Add or Intrinsic.Sub or Intrinsic.Mul or Intrinsic.Div or Intrinsic.Mod ? Ty.Double : Ty.Object;
            if (a is Ty.String && b is Ty.String && op is Intrinsic.Add)
                return Ty.String;
            if (a is Ty.Bool && b is Ty.Bool && op is Intrinsic.And or Intrinsic.Or)
                return Ty.Bool;
            return Ty.Object;
        }

        static Ty intrinsic_type(Intrinsic op , Ty[] ts)
        {
            switch (ts.Length)
            {
                case 1:
                    return ts[0] is Ty.Long ? Ty.Long : ts[0] is Ty.Bool ? Ty.Bool : Ty.Object;
                case 2:
                    return binary_type(op , ts[0] , ts[1]);
                default:
                    if (ts.All(numeric))
                        return binary_type(op , binary_type(op , ts[0] , ts[1]) , ts[2]);
                    if (op is Intrinsic.Add && ts.All(t => t is Ty.String))
                        return Ty.String;
                    return Ty.Object;
            }
        }

        //─────────── 3. 식 트리 만들기 ───────────

        static readonly MethodInfo
            M_Intrinsic1 = method(nameof(intrinsic1)),
            M_Intrinsic2 = method(nameof(intrinsic2)),
            M_Intrinsic3 = method(nameof(intrinsic3)),
            M_DivLL = method(nameof(div_ll)),
            M_ModLL = method(nameof(mod_ll)),
            M_DivDL = method(nameof(div_dl)),
            M_ModDL = method(nameof(mod_dl)),
            M_Truth = method(nameof(truth)),
            M_Box = method(nameof(box_bool)),
            M_ListCall = method(nameof(list_call)),
            M_GetAt = typeof(Collections).GetMethod(nameof(Collections.GetAt) , BindingFlags.NonPublic | BindingFlags.Static)!,
            M_SetAt = typeof(Collections).GetMethod(nameof(Collections.SetAt) , BindingFlags.NonPublic | BindingFlags.Static)!,
            M_Paren = method(nameof(paren_check)),
            M_IsEarly = method(nameof(is_early)),
            M_EarlyValue = method(nameof(early_value)),
            M_NewEarly = method(nameof(new_early)),
            M_EarlyOf = method(nameof(early_of)),
            M_Check = method(nameof(statement_check)),
            M_ReturnValue = method(nameof(return_value)),
            M_RepeatCount = method(nameof(repeat_count)),
            M_ForItems = method(nameof(for_items)),
            M_SetGlobal = method(nameof(set_global)),
            M_StringEquals = typeof(string).GetMethod(nameof(string.Equals) , new[] { typeof(string) , typeof(string) })!,
            M_Concat2 = typeof(string).GetMethod(nameof(string.Concat) , new[] { typeof(string) , typeof(string) })!,
            M_Concat3 = typeof(string).GetMethod(nameof(string.Concat) , new[] { typeof(string) , typeof(string) , typeof(string) })!,
            M_Execute = typeof(FunctionInterface).GetMethod(nameof(FunctionInterface.Execute))!,
            M_Get = typeof(VariableInterface).GetMethod(nameof(VariableInterface.Get))!;
        static readonly ConstructorInfo C_Exception = typeof(JyunoException).GetConstructor(new[] { typeof(string) })!;
        static readonly FieldInfo F_Depth = typeof(Counter).GetField(nameof(Counter.Depth))!;

        static MethodInfo method(string name) => typeof(StaticJit).GetMethod(name , BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException(name);

        readonly Counter counter = new();
        readonly Dictionary<IrStmt , LabelTarget> breaks = new(ReferenceEqualityComparer.Instance);
        LabelTarget top_return = null!;

        //분류와 타입 추론: IL 생성과 C# 생성이 같이 쓴다
        Frame analyze()
        {
            Frame root = frame_of(RootKey , program.TopFrameSize , null);
            stack.Add(root);
            classify_block(program.Root);
            foreach (Frame frame in frames.Values)
                foreach (Slot slot in frame.Slots)
                {
                    if (slot.IsFunc && slot.IsVar) throw new Unsupported("함수와 변수가 같은 이름");
                    if (slot.FuncDefs > 1) throw new Unsupported("함수를 여러번 정의");
                }
            foreach (Function f in functions.Values)
                f.Params = new Ty[f.Arity];
            int rounds = 0;
            do
            {
                changed = false;
                infer_block(program.Root);
                if (++rounds > 100) throw new Unsupported("타입 추론이 끝나지 않음");
            } while (changed);
            stack.Clear();
            return root;
        }

        Func<object?> compile(bool interpret)
        {
            Frame root = analyze();
            top_return = E.Label(typeof(object) , "return");
            ParameterExpression error = E.Parameter(typeof(GrammarErrorException) , "e");
            E body = E.Block(
                E.Assign(E.Field(E.Constant(counter) , F_Depth) , E.Constant(0)),
                E.TryCatch(
                    frame_block(root , program.Root , null , E.Label(top_return , E.Constant(null , typeof(object)))),
                    E.Catch(error , E.Convert(E.PropertyOrField(error , nameof(GrammarErrorException.Error)) , typeof(object)))));
            var lambda = E.Lambda<Func<object?>>(body , "jyuno_main" , Array.Empty<ParameterExpression>());
            LastLambda = lambda;
            return lambda.Compile(interpret);
        }

        //한 영역의 블록: 칸 변수를 만들고 (반복마다 새 영역이므로) 정의되지 않은 상태로 시작
        E frame_block(Frame frame , IrBlock block , Action<List<E>>? prologue , E? tail)
        {
            List<ParameterExpression> variables = new();
            List<E> body = new();
            foreach (Slot slot in frame.Slots)
            {
                if (slot.IsFunc)
                {
                    slot.Var = E.Variable(delegate_type(slot.Func!) , "f");
                    variables.Add(slot.Var);
                    body.Add(E.Assign(slot.Var , E.Constant(null , slot.Var.Type)));
                }
                else if (slot.IsVar)
                {
                    slot.Var = E.Variable(clr(slot.Type) , "v");
                    slot.Defined = E.Variable(typeof(bool) , "d");
                    variables.Add(slot.Var);
                    variables.Add(slot.Defined);
                    body.Add(E.Assign(slot.Defined , E.Constant(false)));
                    if (slot.HasConst)
                    {
                        slot.Const = E.Variable(typeof(bool) , "c");
                        variables.Add(slot.Const);
                        body.Add(E.Assign(slot.Const , E.Constant(false)));
                    }
                }
            }
            stack.Add(frame);
            prologue?.Invoke(body);
            foreach (IrStmt s in block.Statements)
                body.Add(statement(s));
            stack.RemoveAt(stack.Count - 1);
            if (tail is not null)
                body.Add(tail);
            else
                body.Add(E.Empty());
            return E.Block(tail?.Type ?? typeof(void) , variables , body);
        }

        Type delegate_type(Function f)
        {
            if (f.DelegateType is null)
            {
                Type[] types = new Type[f.Arity + 1];
                for (int i = 0 ; i < f.Arity ; i++)
                    types[i] = clr(f.Params[i]);
                types[^1] = clr(f.Ret);
                f.DelegateType = E.GetFuncType(types);
            }
            return f.DelegateType;
        }

        static E fail(string message , Type type) => E.Throw(E.New(C_Exception , E.Constant(message)) , type);

        //값의 타입 바꾸기 (추론 결과상 같은 타입이거나 object로 넓히는 경우만 생김)
        static E conv(E e , Type to)
        {
            if (e.Type == to)
                return e;
            if (to == typeof(object))
                return e.Type == typeof(bool) ? E.Call(M_Box , e) : E.Convert(e , typeof(object));
            if (e.Type == typeof(object))
                return E.Convert(e , to);
            if (e.Type == typeof(void))
                return E.Block(e , E.Default(to));
            throw new Unsupported($"{e.Type} → {to}");
        }
        static E boxed(E e) => conv(e , typeof(object));

        E expression(IrExpr ir)
        {
            E result = expression_core(ir);
            Type expected = clr(type_of(ir));
            return conv(result , expected);
        }

        E expression_core(IrExpr ir)
        {
            switch (ir)
            {
                case IrConst c:
                    return c.Value is null ? E.Constant(null , typeof(object)) : E.Constant(c.Value , c.Value.GetType());
                case IrRead read:
                    return read_name(read.Name , clr(type_of(read)));
                case IrIntrinsic intrinsic:
                    return intrinsic_call(intrinsic);
                case IrCall call:
                    return call_name(call);
                case IrAssign assign:
                    return assign_name(assign);
                case IrParen paren:
                {
                    E inner = expression(paren.Inner);
                    if (!paren.Closed)
                        return E.Block(inner , E.Throw(E.Call(typeof(Interpreter).GetMethod(nameof(parenthesis_error) , BindingFlags.NonPublic | BindingFlags.Static)!) , typeof(object)));
                    return inner.Type == typeof(object) ? E.Call(M_Paren , inner) : inner;
                }
                case IrConstDef definition:
                {
                    Slot slot = stack[^1].Slots[definition.Slot];
                    return E.Block(typeof(object) ,
                        E.Assign(slot.Var! , conv(E.Constant(definition.Value , definition.Value.GetType()) , slot.Var!.Type)) ,
                        E.Assign(slot.Defined! , E.Constant(true)) ,
                        E.Assign(slot.Const! , E.Constant(true)) ,
                        E.Constant(null , typeof(object)));
                }
                case IrLabel label:
                {
                    Slot slot = stack[^1].Slots[label.Slot];
                    return E.Block(typeof(object) ,
                        E.Assign(slot.Var! , conv(E.Constant(label.Line , typeof(int)) , slot.Var!.Type)) ,
                        E.Assign(slot.Defined! , E.Constant(true)) ,
                        slot.Const is null ? E.Empty() : E.Assign(slot.Const , E.Constant(false)) ,
                        E.Constant(null , typeof(object)));
                }
                default:
                    throw new Unsupported(ir.Op.ToString());
            }
        }

        //상수 칸에 쓰기 (const로 정의되었다면 인터프리터와 같은 오류)
        static E write(Slot slot , E raw)
        {
            E assign = E.Assign(slot.Var! , conv(raw , slot.Var!.Type));
            if (slot.Const is null)
                return assign;
            return E.IfThenElse(slot.Const , fail("상수를 변경할수 없습니다." , typeof(void)) , assign);
        }

        E read_name(IrName name , Type type)
        {
            Slot[] slots = slots_of(name);
            if (slots.Length is 1 && slots[0].IsFunc)
                return E.Condition(E.Equal(slots[0].Var! , E.Constant(null , slots[0].Var!.Type)) ,
                    fail($"'{name.Name}'은/는 알수없는 변수/함수 입니다." , typeof(object)) ,
                    fail($"'{name.Name}'에서 값을 가져올수 없습니다." , typeof(object)));
            //안쪽 영역부터 정의된 칸, 없으면 전역
            E chain = global_read(name , type);
            for (int k = slots.Length - 1 ; k >= 0 ; k--)
            {
                Slot slot = slots[k];
                if (!slot.IsVar)
                    continue;
                chain = E.Condition(slot.Defined! , conv(slot.Var! , type) , chain);
            }
            return chain;
        }

        E global_read(IrName name , Type type)
        {
            if (name.Import < 0)
                return fail($"'{name.Name}'은/는 알수없는 변수/함수 입니다." , type);
            object? holder = imports[name.Import];
            if (holder is VariableInterface vi)
                return conv(E.Call(E.Constant(vi , typeof(VariableInterface)) , M_Get) , type);
            return fail($"'{name.Name}'에서 값을 가져올수 없습니다." , type);
        }

        E assign_name(IrAssign assign)
        {
            E value = expression(assign.Value);
            Slot[] slots = slots_of(assign.Name);
            //값 → (EarlyEnd라면 안의 값) → 정의된 칸 또는 전역, 없으면 지금 영역에 만듦
            E store(E raw)
            {
                E chain;
                if (assign.Name.Import >= 0)
                    chain = E.Call(M_SetGlobal , E.Constant(imports[assign.Name.Import] , typeof(object)) , E.Constant(assign.Name.Name) , boxed(raw));
                else
                {
                    Slot create = slot_at(0 , assign.Create);
                    chain = E.Block(write(create , raw) , E.Assign(create.Defined! , E.Constant(true)));
                }
                for (int k = slots.Length - 1 ; k >= 0 ; k--)
                {
                    Slot slot = slots[k];
                    chain = E.IfThenElse(slot.Defined! , write(slot , raw) , chain);
                }
                return chain;
            }
            if (value.Type != typeof(object))
            {
                ParameterExpression v = E.Variable(value.Type , "t");
                return E.Block(typeof(object) , new[] { v } , E.Assign(v , value) , store(v) , E.Constant(null , typeof(object)));
            }
            ParameterExpression tmp = E.Variable(typeof(object) , "t");
            ParameterExpression raw_value = E.Variable(typeof(object) , "raw");
            return E.Block(typeof(object) , new[] { tmp , raw_value } ,
                E.Assign(tmp , value) ,
                E.Condition(E.Call(M_IsEarly , tmp) ,
                    E.Block(E.Assign(raw_value , E.Call(M_EarlyValue , tmp)) , store(raw_value) , E.Call(M_NewEarly)) ,
                    E.Block(store(tmp) , E.Constant(null , typeof(object)))));
        }

        E args_array(IrExpr[] arguments) => E.NewArrayInit(typeof(object) , arguments.Select(a => boxed(expression(a))));

        E call_name(IrCall call)
        {
            string name = call.Name.Name;
            Slot? fslot = function_slot(call.Name);
            if (fslot is not null)
                return direct_call(fslot , call);
            //안쪽 영역부터 정의된 변수 칸, 없으면 전역
            Slot[] slots = slots_of(call.Name);
            E chain;
            if (call.Name.Import < 0)
                chain = fail(call.Missing , typeof(object));
            else
            {
                object? holder = imports[call.Name.Import];
                if (holder is FunctionInterface fi)
                    chain = E.Call(E.Constant(fi , typeof(FunctionInterface)) , M_Execute , args_array(call.Arguments));
                else if (holder is VariableInterface vi)
                    chain = value_call(E.Call(E.Constant(vi , typeof(VariableInterface)) , M_Get) , call);
                else
                    chain = fail($"'{name}'는 함수 또는 변수가 아닙니다." , typeof(object));
            }
            for (int k = slots.Length - 1 ; k >= 0 ; k--)
            {
                Slot slot = slots[k];
                if (!slot.IsVar)
                    continue;
                chain = E.Condition(slot.Defined! , value_call(slot.Var! , call) , chain);
            }
            return chain;
        }

        E direct_call(Slot slot , IrCall call)
        {
            string name = call.Name.Name;
            {
                Function f = slot.Func!;
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
                    invoke = E.Invoke(slot.Var! , args);
                else
                {
                    //인자는 왼쪽부터 모두 계산하고, 넘치는 인자는 버림
                    List<ParameterExpression> temps = args.Select(a => E.Variable(a.Type)).ToList();
                    List<E> steps = new();
                    for (int k = 0 ; k < temps.Count ; k++) steps.Add(E.Assign(temps[k] , args[k]));
                    foreach (E x in extra) steps.Add(x);
                    steps.Add(E.Invoke(slot.Var! , temps));
                    invoke = E.Block(temps , steps);
                }
                return E.Condition(E.Equal(slot.Var! , E.Constant(null , slot.Var!.Type)) ,
                    fail(call.Missing , invoke.Type) , invoke);
            }
        }

        //변수 이름을 함수처럼 쓴 경우 (배열/리스트 읽기/쓰기, 그 외는 값)
        E value_call(E value , IrCall call)
        {
            if (!call.HasArguments)
                return boxed(value);
            ParameterExpression v = E.Variable(typeof(object) , "v");
            E list = E.Convert(v , typeof(System.Collections.IList));
            E access;
            Ty index = call.Arguments.Length is 1 or 2 ? norm(type_of(call.Arguments[0])) : Ty.Object;
            //정수 인덱스: 인자 배열과 박싱 없이 바로
            if (index is Ty.Long && call.Arguments.Length is 1)
                access = E.Call(M_GetAt , list , expression(call.Arguments[0]));
            else if (index is Ty.Long && call.Arguments.Length is 2)
                access = E.Block(E.Call(M_SetAt , list , expression(call.Arguments[0]) , boxed(expression(call.Arguments[1]))) , E.Constant(null , typeof(object)));
            else
                access = E.Call(M_ListCall , list , args_array(call.Arguments));
            return E.Block(typeof(object) , new[] { v } ,
                E.Assign(v , boxed(value)) ,
                E.Condition(E.TypeIs(v , typeof(System.Collections.IList)) , access , call.Closed ? E.Call(M_EarlyOf , v) : v));
        }

        E intrinsic_call(IrIntrinsic ir)
        {
            NativeFunction callee = (NativeFunction)imports[ir.Import]!;
            Intrinsic op = callee.Intrinsic;
            E[] args = Array.ConvertAll(ir.Arguments , expression);
            Ty[] ts = Array.ConvertAll(ir.Arguments , a => norm(type_of(a)));
            E callee_e = E.Constant(callee);
            switch (args.Length)
            {
                case 1:
                    if (ts[0] is Ty.Long) return E.OnesComplement(args[0]);
                    if (ts[0] is Ty.Bool) return E.Not(args[0]);
                    return E.Call(M_Intrinsic1 , callee_e , boxed(args[0]));
                case 2:
                    return binary(op , callee_e , args[0] , ts[0] , args[1] , ts[1]);
                default:
                    if (ts.All(numeric))
                    {
                        //왼쪽부터 차례대로 (첫 두 값은 먼저 임시 변수에)
                        Ty t01 = binary_type(op , ts[0] , ts[1]);
                        E first = binary(op , callee_e , args[0] , ts[0] , args[1] , ts[1]);
                        return binary(op , callee_e , first , t01 , args[2] , ts[2]);
                    }
                    if (op is Intrinsic.Add && ts.All(t => t is Ty.String))
                        return E.Call(M_Concat3 , args[0] , args[1] , args[2]);
                    return E.Call(M_Intrinsic3 , callee_e , boxed(args[0]) , boxed(args[1]) , boxed(args[2]));
            }
        }

        E binary(Intrinsic op , E callee , E a , Ty ta , E b , Ty tb)
        {
            Ty result = binary_type(op , ta , tb);
            if (ta is Ty.Long && tb is Ty.Long)
            {
                switch (op)
                {
                    case Intrinsic.Add: return E.Add(a , b);
                    case Intrinsic.Sub: return E.Subtract(a , b);
                    case Intrinsic.Mul: return E.Multiply(a , b);
                    case Intrinsic.Div: return E.Call(M_DivLL , callee , a , b);
                    case Intrinsic.Mod: return E.Call(M_ModLL , callee , a , b);
                    case Intrinsic.And: return E.And(a , b);
                    case Intrinsic.Or: return E.Or(a , b);
                    case Intrinsic.Equal: return E.Equal(a , b);
                    case Intrinsic.Less: return E.LessThan(a , b);
                    case Intrinsic.Greater: return E.GreaterThan(a , b);
                }
            }
            else if (numeric(ta) && numeric(tb))
            {
                //인자는 이미 왼쪽부터 계산된 상태로 넘어오므로, 순서를 지키기 위해 임시 변수 없이 바로 변환
                E da = ta is Ty.Double ? a : E.Convert(a , typeof(double));
                E db = tb is Ty.Double ? b : E.Convert(b , typeof(double));
                switch (op)
                {
                    case Intrinsic.Add: return E.Add(da , db);
                    case Intrinsic.Sub: return E.Subtract(da , db);
                    case Intrinsic.Mul: return E.Multiply(da , db);
                    case Intrinsic.Div: return tb is Ty.Long ? E.Call(M_DivDL , callee , da , b) : E.Divide(da , db);
                    case Intrinsic.Mod: return tb is Ty.Long ? E.Call(M_ModDL , callee , da , b) : E.Modulo(da , db);
                    case Intrinsic.Less when ta is Ty.Double && tb is Ty.Double: return E.LessThan(a , b);
                    case Intrinsic.Greater when ta is Ty.Double && tb is Ty.Double: return E.GreaterThan(a , b);
                }
            }
            else if (ta is Ty.String && tb is Ty.String)
            {
                if (op is Intrinsic.Add) return E.Call(M_Concat2 , a , b);
                if (op is Intrinsic.Equal) return E.Call(M_StringEquals , a , b);
            }
            else if (ta is Ty.Bool && tb is Ty.Bool)
            {
                if (op is Intrinsic.Equal) return E.Equal(a , b);
                if (op is Intrinsic.And) return E.And(a , b);
                if (op is Intrinsic.Or) return E.Or(a , b);
            }
            E generic = E.Call(M_Intrinsic2 , callee , boxed(a) , boxed(b));
            return result is Ty.Bool ? E.Convert(generic , typeof(bool)) : generic;
        }

        E truth_of(IrExpr ir)
        {
            E e = expression(ir);
            return e.Type == typeof(bool) ? e : E.Call(M_Truth , boxed(e));
        }

        //조건: 인자를 모두 계산하고 첫 값으로 판단 (인자가 없으면 거짓)
        E condition(IrExpr[] args)
        {
            if (args.Length is 0)
                return E.Constant(false);
            if (args.Length is 1)
                return truth_of(args[0]);
            ParameterExpression first = E.Variable(typeof(bool) , "c");
            List<E> steps = new() { E.Assign(first , truth_of(args[0])) };
            for (int i = 1 ; i < args.Length ; i++)
                steps.Add(expression(args[i]));
            steps.Add(first);
            return E.Block(typeof(bool) , new[] { first } , steps);
        }

        E statements(IrBlock block)
        {
            if (block.Statements.Length is 0)
                return E.Empty();
            return E.Block(typeof(void) , block.Statements.Select(statement));
        }

        LabelTarget return_label => current?.Return ?? top_return;
        Type return_type => current is null ? typeof(object) : clr(current.Ret);

        E statement(IrStmt s)
        {
            switch (s.Kind)
            {
                case IrKind.Empty:
                    return E.Empty();
                case IrKind.Expression:
                {
                    E e = expression(s.Expression!);
                    if (!needs_check(s.Expression! , false))
                        return E.Block(typeof(void) , e);
                    ParameterExpression r = E.Variable(typeof(ReturnInfo) , "r");
                    return E.Block(typeof(void) , new[] { r } ,
                        E.Assign(r , E.Call(M_Check , boxed(e))) ,
                        E.IfThen(E.NotEqual(r , E.Constant(null , typeof(ReturnInfo))) ,
                            E.Return(return_label , conv(E.Call(M_ReturnValue , r) , return_type))));
                }
                case IrKind.If:
                {
                    E c = condition(s.Arguments);
                    E then = statements(s.Body!);
                    return s.Else is null ? E.IfThen(c , then) : E.IfThenElse(c , then , statements(s.Else));
                }
                case IrKind.While:
                {
                    LabelTarget end = E.Label("end");
                    breaks[s] = end;
                    return E.Loop(E.IfThenElse(condition(s.Arguments) , statements(s.Body!) , E.Break(end)) , end);
                }
                case IrKind.Repeat:
                {
                    LabelTarget end = E.Label("end");
                    breaks[s] = end;
                    ParameterExpression n = E.Variable(typeof(long) , "n");
                    List<E> steps = new();
                    if (s.Arguments.Length is 0)
                        steps.Add(E.Assign(n , E.Constant(0L)));
                    else
                    {
                        E first = expression(s.Arguments[0]);
                        steps.Add(E.Assign(n , first.Type == typeof(long) ? first : E.Call(M_RepeatCount , boxed(first))));
                        for (int i = 1 ; i < s.Arguments.Length ; i++)
                            steps.Add(expression(s.Arguments[i]));
                    }
                    steps.Add(E.Loop(E.IfThenElse(E.GreaterThan(n , E.Constant(0L)) ,
                        E.Block(statements(s.Body!) , E.PreDecrementAssign(n)) , E.Break(end)) , end));
                    return E.Block(typeof(void) , new[] { n } , steps);
                }
                case IrKind.For:
                {
                    if (s.Arguments.Length is not 1)
                        return E.Block(typeof(void) , s.Arguments.Select(expression).Append(fail("for 다음에는 변수 이름과 순회할 값 하나만 와야 합니다." , typeof(void))));
                    LabelTarget end = E.Label("end");
                    breaks[s] = end;
                    ParameterExpression items = E.Variable(typeof(object[]) , "items");
                    ParameterExpression i = E.Variable(typeof(int) , "i");
                    Frame inner = frames[s];
                    Slot var = inner.Slots[s.Slot];
                    E body = frame_block(inner , s.Body! , steps => {
                        steps.Add(E.Assign(var.Var! , conv(E.ArrayIndex(items , i) , var.Var!.Type)));
                        steps.Add(E.Assign(var.Defined! , E.Constant(true)));
                    } , null);
                    return E.Block(typeof(void) , new[] { items , i } ,
                        E.Assign(items , E.Call(M_ForItems , boxed(expression(s.Arguments[0])))) ,
                        E.Assign(i , E.Constant(0)) ,
                        E.Loop(E.IfThenElse(E.LessThan(i , E.ArrayLength(items)) ,
                            E.Block(body , E.PreIncrementAssign(i)) , E.Break(end)) , end));
                }
                case IrKind.Func:
                {
                    Function f = functions[s];
                    Slot slot = stack[^1].Slots[s.Slot];
                    Function? outer = current;
                    current = f;
                    f.Parameters = new ParameterExpression[f.Arity];
                    for (int k = 0 ; k < f.Arity ; k++)
                        f.Parameters[k] = E.Parameter(clr(f.Params[k]) , "a" + k);
                    Type ret = clr(f.Ret);
                    f.Return = E.Label(ret , "return");
                    E depth = E.Field(E.Constant(counter) , F_Depth);
                    E body = frame_block(frames[s] , s.Body! , null , E.Label(f.Return , ret == typeof(object) ? E.Constant(null , typeof(object)) : E.Default(ret)));
                    //예외는 Jyuno 안에서 잡을수 없고 실행을 시작할 때 깊이를 0으로 되돌리므로, finally 없이 정상 반환에서만 줄임
                    ParameterExpression result = E.Variable(ret , "result");
                    E guarded = E.Block(ret , new[] { result } ,
                        E.IfThen(E.GreaterThanOrEqual(depth , E.Constant(MaxCallDepth)) , fail($"함수 호출이 너무 깊습니다. (최대 {MaxCallDepth}번)" , typeof(void))) ,
                        E.PreIncrementAssign(depth) ,
                        E.Assign(result , body) ,
                        E.PreDecrementAssign(depth) ,
                        result);
                    current = outer;
                    return E.Assign(slot.Var! , E.Lambda(delegate_type(f) , guarded , "jyuno_" + s.Line , f.Parameters));
                }
                case IrKind.Return:
                {
                    if (s.Arguments.Length is 0)
                        return E.Return(return_label , conv(E.Constant(null , typeof(object)) , return_type));
                    ParameterExpression first = E.Variable(return_type , "r");
                    List<E> steps = new() { E.Assign(first , conv(expression(s.Arguments[0]) , return_type)) };
                    for (int i = 1 ; i < s.Arguments.Length ; i++)
                        steps.Add(expression(s.Arguments[i]));
                    steps.Add(E.Return(return_label , first));
                    return E.Block(typeof(void) , new[] { first } , steps);
                }
                case IrKind.Break:
                    return E.Break(breaks[s.Loop!]);
                case IrKind.Local:
                {
                    //값은 그대로(EarlyEnd도) 넣음, 이미 있다면 그 변수에 씀 (상수라면 오류)
                    Slot slot = stack[^1].Slots[s.Slot];
                    E value = s.Expression is null ? E.Constant(null , typeof(object)) : expression(s.Expression);
                    ParameterExpression v = E.Variable(value.Type , "t");
                    return E.Block(typeof(void) , new[] { v } ,
                        E.Assign(v , value) ,
                        E.IfThenElse(slot.Defined! , write(slot , v) , E.Assign(slot.Var! , conv(v , slot.Var!.Type))) ,
                        E.Assign(slot.Defined! , E.Constant(true)));
                }
                case IrKind.Param:
                {
                    Slot slot = stack[^1].Slots[s.Slot];
                    //새 변수로 덮어씀 (const였더라도)
                    return E.Block(
                        E.Assign(slot.Var! , conv(current!.Parameters[s.ParameterIndex] , slot.Var!.Type)) ,
                        E.Assign(slot.Defined! , E.Constant(true)) ,
                        slot.Const is null ? E.Empty() : E.Assign(slot.Const , E.Constant(false)));
                }
                default:
                    throw new Unsupported(s.Kind.ToString());
            }
        }

        //─────────── 실행 중 도우미 (클로저 엔진과 같은 동작) ───────────

        internal static object? intrinsic1(NativeFunction callee , object? x)
        {
            object r = fast1(callee.Intrinsic , x);
            return r != Miss ? r : callee.Execute(new[] { x });
        }
        internal static object? intrinsic2(NativeFunction callee , object? x , object? y)
        {
            object r = fast2(callee.Intrinsic , x , y);
            return r != Miss ? r : callee.Execute(new[] { x , y });
        }
        internal static object? intrinsic3(NativeFunction callee , object? x , object? y , object? z)
        {
            object r = fast3(callee.Intrinsic , x , y , z);
            return r != Miss ? r : callee.Execute(new[] { x , y , z });
        }
        internal static long div_ll(NativeFunction callee , long a , long b) => b != 0 && !(a == long.MinValue && b == -1) ? a / b : (long)callee.Execute(new object?[] { a , b })!;
        internal static long mod_ll(NativeFunction callee , long a , long b) => b != 0 && !(a == long.MinValue && b == -1) ? a % b : (long)callee.Execute(new object?[] { a , b })!;
        internal static double div_dl(NativeFunction callee , double a , long b) => b != 0 ? a / b : (double)callee.Execute(new object?[] { a , b })!;
        internal static double mod_dl(NativeFunction callee , double a , long b) => b != 0 ? a % b : (double)callee.Execute(new object?[] { a , b })!;
        internal static bool truth(object? value) => value is bool b ? b : Parser.IsTrue(value);
        internal static object box_bool(bool value) => box(value);
        internal static object? list_call(System.Collections.IList collection , object?[] index)
        {
            switch (index.Length)
            {
                case 0: return collection;
                case 1: return Collections.Get(collection , index[0]);
                case 2: Collections.Set(collection , index[0] , index[1]); return null;
                default: throw new JyunoException("배열/리스트는 '(이름) (인덱스)' 또는 '(이름) (인덱스) (값)' 형식으로만 쓸수 있습니다.");
            }
        }
        internal static object? paren_check(object? value) => value is EarlyEnd ? throw parenthesis_error() : value;
        internal static bool is_early(object? value) => value is EarlyEnd;
        internal static object? early_value(object? value) => ((EarlyEnd)value!).Value;
        internal static object new_early() => new EarlyEnd(null);
        internal static object early_of(object? value) => new EarlyEnd(value);
        internal static ReturnInfo? statement_check(object? value)
        {
            if (value is ReturnInfo ri) return ri;
            if (value is GrammarError error) throw new GrammarErrorException(error);
            return null;
        }
        internal static object? return_value(ReturnInfo ri) => ri.value;
        internal static long repeat_count(object? value) => Values.TryGetInteger(value , out long n) && n > 0 ? n : 0;
        internal static object?[] for_items(object? value) => value switch {
            string text => text.Select(c => (object?)c.ToString()).ToArray(),
            System.Collections.IList collection => collection.Cast<object?>().ToArray(),
            _ => throw new JyunoException($"'{value ?? "null"}'은/는 순회할수 없습니다. (배열/리스트/문자열만 가능)")
        };
        internal static object? set_global(object? holder , string name , object? value)
        {
            EarlyEnd? early = null;
            if (value is EarlyEnd e)
            {
                early = e;
                value = e.Value;
            }
            if (holder is VariableInterface vi)
                vi.Set(value);
            else
                throw new JyunoException($"'{name}'은/는 변수가 아닙니다.");
            return early is null ? null : new EarlyEnd(null);
        }
    }
}
