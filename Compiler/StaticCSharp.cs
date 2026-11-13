using System.Globalization;
using System.Text;
using Jyuno.Analysis;
using Jyuno.Compiler;
using Jyuno.Language;

namespace Jyuno;

//[프로토타입] 제한 모드 IR → 타입 추론(StaticJit과 같은 분석) → C# 소스
//
//IL을 실행 중에 만드는 대신 빌드할 때 C# 소스로 내보내서 일반 C# 컴파일러(Roslyn)와 ILC(Native AOT)로 빌드하는 실험
//  - 식 트리(IL) 버전과 같은 의미: 왼쪽부터 계산하도록 모든 중간 값을 임시 변수에 담음 (A-정규형)
//  - Jyuno 함수 → 영역 시작에 선언한 C# 로컬 함수 + 정의 여부 플래그 (func 문장은 플래그만 켬)
//  - break → goto, 함수 안의 return → 반환 레이블로 goto (호출 깊이를 정상 반환에서만 줄임)
//  - 호스트 객체 없이 가져오기 표만 보고 만들고, 실행할 때 같은 바이너리를 연결 검사(Bind)한 뒤 실행
public partial class Interpreter
{
    internal sealed partial class StaticJit
    {
        /// <summary>C# 소스를 만들수 없는 프로그램이라면 null (이유는 reason)</summary>
        /// <summary>facts: 검사기가 증명한 타입 (null이거나 쓸수 없으면 기본 추론, typed로 알려줌)</summary>
        public static string? TryEmitCSharp(IrProgram program , byte[] image , string ns , string class_name , out string? reason , CodegenFacts? facts = null) => TryEmitCSharp(program , image , ns , class_name , out reason , facts , out _);
        public static string? TryEmitCSharp(IrProgram program , byte[] image , string ns , string class_name , out string? reason , CodegenFacts? facts , out bool typed)
        {
            typed = false;
            try
            {
                StaticJit jit = new(program , new object?[program.Imports.Length]) { aot = true , facts = facts };
                reason = null;
                string code = jit.emit_csharp(image , ns , class_name);
                typed = jit.facts_mode;
                return code;
            }
            catch (Unsupported e)
            {
                reason = e.Message;
                return null;
            }
        }

        readonly record struct V(string Code , Type Type , bool Stable = false);

        readonly StringBuilder cs = new();
        int indent , ids;
        readonly Dictionary<IrStmt , string> break_names = new(ReferenceEqualityComparer.Instance);

        int next_id() => ++ids;
        void line(string text) => cs.Append(' ' , indent * 4).Append(text).Append('\n');
        void open(string text) { line(text); line("{"); indent++; }
        void close(string text = "}") { indent--; line(text); }

        static string tname(Type t) => t == typeof(long) ? "long" : t == typeof(double) ? "double" : t == typeof(bool) ? "bool" : t == typeof(string) ? "string"
            : Nullable.GetUnderlyingType(t) is Type u && u != t ? tname(u) + "?" : "object";

        static string literal(string text)
        {
            StringBuilder b = new("\"");
            foreach (char c in text)
            {
                if (c is '"' or '\\') b.Append('\\').Append(c);
                else if (c >= ' ' && c != 0x7F && !char.IsControl(c) && !char.IsSurrogate(c) && c is not '\u2028' and not '\u2029') b.Append(c);
                else b.Append("\\u").Append(((int)c).ToString("X4"));
            }
            return b.Append('"').ToString();
        }
        static string cfail(string message , Type type) => $"S.Fail<{tname(type)}>({literal(message)})";

        static readonly V Null = new("((object)null)" , typeof(object) , true);

        static string cconv(V v , Type to)
        {
            if (v.Type == to)
                return v.Code;
            if (to == typeof(object))
                return v.Type == typeof(bool) ? $"S.Box({v.Code})" : $"(object)({v.Code})";
            if (v.Type == typeof(object))
                return $"(({tname(to)})({v.Code}))";
            //사실 모드: long ↔ long? (null이 아니라고 증명한 자리), 검사기가 일어나지 않는다고 증명한 경로
            if (Nullable.GetUnderlyingType(to) == v.Type || Nullable.GetUnderlyingType(v.Type) == to)
                return $"(({tname(to)})({v.Code}))";
            if (facts_conv)
                return $"S.Impossible<{tname(to)}>({cbox(v)})";
            throw new Unsupported($"{v.Type} → {to}");
        }
        static string cbox(V v) => cconv(v , typeof(object));

        //계산 순서를 지키기 위해 값을 임시 변수에 고정
        V let(V v)
        {
            if (v.Stable)
                return v;
            string t = "t" + next_id();
            line($"{tname(v.Type)} {t} = {v.Code};");
            return new V(t , v.Type , true);
        }

        string emit_csharp(byte[] image , string ns , string class_name)
        {
            Frame root = analyze();
            //사실 모드: 검사기가 닿지 않는다고 본 호출이 있으면 "*" 인스턴스를 더해서 다시 만듦 (IL과 같음)
            for (int pass = 0 ; ; pass++)
            {
                needed_changed = false;
                cs.Clear();
                indent = ids = 0;
                break_names.Clear();
                facts_conv = facts_mode;
                try { emit_class(root , image , ns , class_name); }
                finally { facts_conv = false; }
                if (!needed_changed)
                    return cs.ToString();
                if (pass > 20) throw new Unsupported("함수 인스턴스가 정해지지 않음");
            }
        }

        void emit_class(Frame root , byte[] image , string ns , string class_name)
        {
            line("// <auto-generated> Jyuno 제한 모드 프로그램에서 만든 코드 </auto-generated>");
            line("#nullable disable");
            line("#pragma warning disable CS0162, CS0164, CS0168, CS0219, CS8321");
            line("using S = Jyuno.Interpreter.AotSupport;");
            line("");
            line($"namespace {ns};");
            line("");
            open($"public sealed class {class_name}");
            line($"public static readonly byte[] Image = System.Convert.FromBase64String(\"{Convert.ToBase64String(image)}\");");
            for (int i = 0 ; i < program.Imports.Length ; i++)
                line($"readonly object i{i}; // {program.Imports[i].Name} ({program.Imports[i].Kind}{(program.Imports[i].Intrinsic is Intrinsic.None ? "" : ", " + program.Imports[i].Intrinsic)})");
            line("int depth;");
            line("");
            line("/// <summary>바이너리를 검증하고 호스트와 연결합니다. (호스트가 필요한 전역을 같은 종류로 제공하지 않으면 JyunoLinkException)</summary>");
            open($"public {class_name}(Jyuno.Runtime host)");
            line("object[] I = S.Bind(Image , host);");
            for (int i = 0 ; i < program.Imports.Length ; i++)
                line($"i{i} = I[{i}];");
            close();
            line("");
            open("public object Run()");
            line("depth = 0;");
            open("try");
            cs_frame(root , program.Root , null);
            line("return null;");
            close();
            line("catch (System.Exception e) when (S.IsGrammar(e)) { return S.GrammarError(e); }");
            close();
            close();
        }

        //한 영역: 칸 변수를 정의되지 않은 상태로 선언, 이 영역의 함수는 로컬 함수로 미리 선언
        void cs_frame(Frame frame , IrBlock block , Action? prologue)
        {
            line("{");
            indent++;
            foreach (Slot slot in frame.Slots)
            {
                if (slot.IsFunc)
                {
                    slot.Id = next_id();
                    line($"bool fd{slot.Id} = false;");
                    //사실 모드: 인스턴스마다 로컬 함수 이름
                    if (facts_mode)
                    {
                        slot.FuncNames = new(StringComparer.Ordinal);
                        foreach (string key in needed[slot.Func!.Def])
                            slot.FuncNames[key] = $"f{slot.Id}_{slot.FuncNames.Count}";
                    }
                }
                else if (slot.IsVar)
                {
                    slot.Id = next_id();
                    line($"{tname(clr(slot.Type))} v{slot.Id} = default; bool d{slot.Id} = false;{(slot.HasConst ? $" bool c{slot.Id} = false;" : "")}");
                }
            }
            stack.Add(frame);
            prologue?.Invoke();
            foreach (IrStmt s in functions_in(block))
                cs_function(s);
            foreach (IrStmt s in block.Statements)
                cs_statement(s);
            stack.RemoveAt(stack.Count - 1);
            close();
        }

        //이 영역에 속한 func 문장 (for/func 안쪽은 다른 영역)
        static IEnumerable<IrStmt> functions_in(IrBlock block)
        {
            foreach (IrStmt s in block.Statements)
            {
                switch (s.Kind)
                {
                    case IrKind.Func:
                        yield return s;
                        break;
                    case IrKind.If:
                        foreach (IrStmt f in functions_in(s.Body!)) yield return f;
                        if (s.Else is not null) foreach (IrStmt f in functions_in(s.Else)) yield return f;
                        break;
                    case IrKind.While:
                    case IrKind.Repeat:
                        foreach (IrStmt f in functions_in(s.Body!)) yield return f;
                        break;
                }
            }
        }

        void cs_function(IrStmt s)
        {
            Slot slot = stack[^1].Slots[s.Slot];
            if (!facts_mode)
            {
                Function f = functions[s];
                f.Id = slot.Id;
                cs_function_body(s , f , $"f{f.Id}" , frames[s]);
                return;
            }
            //사실 모드: 필요한 인스턴스마다 로컬 함수
            foreach (string key in needed[s].ToList())
            {
                if (!slot.FuncNames!.TryGetValue(key , out string? name))
                    continue; //다음 번에 만듦 (needed_changed)
                Function f = inst_function(s , key);
                f.Id = next_id();
                string outer_key = ctx_key;
                ctx_key = key;
                cs_function_body(s , f , name , inst_frame(s , key));
                ctx_key = outer_key;
            }
        }

        void cs_function_body(IrStmt s , Function f , string name , Frame frame)
        {
            Function? outer = current;
            current = f;
            string rt = tname(clr(f.Ret));
            string ps = string.Join(" , " , Enumerable.Range(0 , f.Arity).Select(k => $"{tname(clr(f.Params[k]))} p{f.Id}_{k}"));
            line("");
            open($"{rt} {name}({ps})");
            line($"if (depth >= {MaxCallDepth}) S.Fail<object>({literal($"함수 호출이 너무 깊습니다. (최대 {MaxCallDepth}번)")});");
            line("depth++;");
            line($"{rt} rv{f.Id} = default;");
            cs_frame(frame , s.Body! , null);
            line($"rv{f.Id} = default;");
            line($"ret{f.Id}: depth--;");
            line($"return rv{f.Id};");
            close();
            line("");
            current = outer;
        }

        void return_with(string code)
        {
            if (current is null)
                line($"return {code};");
            else
                line($"{{ rv{current.Id} = {code}; goto ret{current.Id}; }}");
        }

        V cs_expression(IrExpr ir)
        {
            V core = expression_core_cs(ir);
            Type expected = clr(type_of(ir));
            return let(new V(cconv(core , expected) , expected , core.Stable && core.Type == expected));
        }

        V expression_core_cs(IrExpr ir)
        {
            switch (ir)
            {
                case IrConst c:
                    return c.Value switch {
                        null => Null,
                        long l => new V($"({l.ToString(CultureInfo.InvariantCulture)}L)" , typeof(long) , true),
                        double d => new V(double.IsNaN(d) ? "double.NaN" : double.IsPositiveInfinity(d) ? "double.PositiveInfinity" : double.IsNegativeInfinity(d) ? "double.NegativeInfinity"
                            : $"({d.ToString("R" , CultureInfo.InvariantCulture)}D)" , typeof(double) , true),
                        bool b => new V(b ? "true" : "false" , typeof(bool) , true),
                        string text => new V(literal(text) , typeof(string) , true),
                        _ => throw new Unsupported("상수 " + c.Value.GetType().Name)
                    };
                case IrRead read:
                    return read_name_cs(read.Name , clr(type_of(read)));
                case IrIntrinsic intrinsic:
                    return intrinsic_cs(intrinsic);
                case IrCall call:
                    return call_cs(call);
                case IrAssign assign:
                    return assign_cs(assign);
                case IrOperator op:
                    return operator_cs(op);
                case IrParen paren:
                {
                    V inner = cs_expression(paren.Inner);
                    if (!paren.Closed)
                        return new V("S.ThrowParen<object>()" , typeof(object));
                    return inner.Type == typeof(object) ? new V($"S.Paren({inner.Code})" , typeof(object)) : inner;
                }
                case IrConstDef definition:
                {
                    Slot slot = stack[^1].Slots[definition.Slot];
                    V value = expression_core_cs(new IrConst(definition.Value));
                    line($"v{slot.Id} = {cconv(value , clr(slot.Type))}; d{slot.Id} = true; c{slot.Id} = true;");
                    return Null;
                }
                case IrLabel label:
                {
                    Slot slot = stack[^1].Slots[label.Slot];
                    if (clr(slot.Type) != typeof(object)) throw new Unsupported("레이블 칸의 타입");
                    line($"v{slot.Id} = (object){label.Line}; d{slot.Id} = true;{(slot.HasConst ? $" c{slot.Id} = false;" : "")}");
                    return Null;
                }
                default:
                    throw new Unsupported(ir.Op.ToString());
            }
        }

        V read_name_cs(IrName name , Type type)
        {
            Slot[] slots = slots_of(name);
            if (slots.Length is 1 && slots[0].IsFunc)
                return new V($"(!fd{slots[0].Id} ? {cfail($"'{name.Name}'은/는 알수없는 변수/함수 입니다." , typeof(object))} : {cfail($"'{name.Name}'에서 값을 가져올수 없습니다." , typeof(object))})" , typeof(object));
            string chain = global_read_cs(name , type);
            for (int k = slots.Length - 1 ; k >= 0 ; k--)
            {
                Slot slot = slots[k];
                if (!slot.IsVar)
                    continue;
                chain = $"(d{slot.Id} ? {cconv(new V($"v{slot.Id}" , clr(slot.Type)) , type)} : {chain})";
            }
            return new V(chain , type);
        }

        bool imports_variable(int import) => program.Imports[import].Kind is GlobalKind.Variable or GlobalKind.Constant;

        string global_read_cs(IrName name , Type type)
        {
            if (name.Import < 0)
                return cfail($"'{name.Name}'은/는 알수없는 변수/함수 입니다." , type);
            if (imports_variable(name.Import))
                return cconv(new V($"S.GetGlobal(i{name.Import})" , typeof(object)) , type);
            return cfail($"'{name.Name}'에서 값을 가져올수 없습니다." , type);
        }

        V intrinsic_cs(IrIntrinsic ir)
        {
            Intrinsic op = program.Imports[ir.Import].Intrinsic;
            V[] args = Array.ConvertAll(ir.Arguments , cs_expression);
            Ty[] ts = Array.ConvertAll(ir.Arguments , a => norm(type_of(a)));
            string callee = "i" + ir.Import;
            switch (args.Length)
            {
                case 1:
                    if (ts[0] is Ty.Long) return new V($"(~{args[0].Code})" , typeof(long));
                    if (ts[0] is Ty.Bool) return new V($"(!{args[0].Code})" , typeof(bool));
                    return new V($"S.Intrinsic1({callee} , {cbox(args[0])})" , typeof(object));
                case 2:
                {
                    V r = binary_cs(op , callee , args[0] , ts[0] , args[1] , ts[1]);
                    //null일수도 있는 값 (사실 모드): 값이 있으면 그 타입으로 빠르게, 아니면 도우미 (IL의 binary와 같음)
                    if (facts_mode && r.Code.StartsWith("S.Intrinsic2(") && (nullable(ts[0]) || nullable(ts[1])))
                    {
                        Ty na = non_null(ts[0]) , nb = non_null(ts[1]);
                        Type want = clr(type_of(ir));
                        if (na is Ty.String && nb is Ty.String && (op is Intrinsic.Equal || op is Intrinsic.Add && (ts[0] is Ty.String || ts[1] is Ty.String)))
                            return new V(cconv(binary_cs(op , callee , args[0] , na , args[1] , nb) , want) , want);
                        V g = guarded_cs(args[0] , ts[0] , args[1] , ts[1] , want , (x , y) => binary_cs(op , callee , x , na , y , nb) is V q && !q.Code.StartsWith("S.Intrinsic2(") ? q : null ,
                            (x , y) => new V($"S.Intrinsic2({callee} , {cbox(x)} , {cbox(y)})" , typeof(object)));
                        if (g.Code is not null)
                            return g;
                    }
                    return r;
                }
                default:
                    if (ts.All(numeric))
                    {
                        Ty t01 = binary_type(op , ts[0] , ts[1]);
                        V first = let(binary_cs(op , callee , args[0] , ts[0] , args[1] , ts[1]));
                        return binary_cs(op , callee , first , t01 , args[2] , ts[2]);
                    }
                    if (op is Intrinsic.Add && ts.All(t => t is Ty.String))
                        return new V($"string.Concat({args[0].Code} , {args[1].Code} , {args[2].Code})" , typeof(string));
                    return new V($"S.Intrinsic3({callee} , {cbox(args[0])} , {cbox(args[1])} , {cbox(args[2])})" , typeof(object));
            }
        }

        //null일수도 있는 값: 모두 값이 있으면 빠른 계산, 아니면 원래의 도우미 (결과는 검사기의 타입으로), 빠른 계산이 없으면 Code가 null
        V guarded_cs(V a , Ty ta , V b , Ty tb , Type want , Func<V , V , V?> fast , Func<V , V , V> slow)
        {
            a = let(a);
            b = let(b);
            V? quick = fast(unwrap_cs(a) , unwrap_cs(b));
            if (quick is not V q)
                return default;
            List<string> has = new();
            foreach (var (v , t) in new[] { (a , ta) , (b , tb) })
                if (nullable(t))
                    has.Add(Nullable.GetUnderlyingType(v.Type) is not null ? $"{v.Code}.HasValue" : $"{v.Code} != null");
            V generic = slow(a , b);
            return new V($"({string.Join(" && " , has)} ? {cconv(q , want)} : {cconv(generic , want)})" , want);
        }
        static V unwrap_cs(V v) => Nullable.GetUnderlyingType(v.Type) is Type u ? new V($"{v.Code}.Value" , u , true) : v;

        static V binary_cs(Intrinsic op , string callee , V a , Ty ta , V b , Ty tb)
        {
            Ty result = binary_type(op , ta , tb);
            string x = a.Code , y = b.Code;
            if (ta is Ty.Long && tb is Ty.Long)
            {
                switch (op)
                {
                    case Intrinsic.Add: return new V($"({x} + {y})" , typeof(long));
                    case Intrinsic.Sub: return new V($"({x} - {y})" , typeof(long));
                    case Intrinsic.Mul: return new V($"({x} * {y})" , typeof(long));
                    case Intrinsic.Div: return new V($"S.DivLL({callee} , {x} , {y})" , typeof(long));
                    case Intrinsic.Mod: return new V($"S.ModLL({callee} , {x} , {y})" , typeof(long));
                    case Intrinsic.And: return new V($"({x} & {y})" , typeof(long));
                    case Intrinsic.Or: return new V($"({x} | {y})" , typeof(long));
                    case Intrinsic.Equal: return new V($"({x} == {y})" , typeof(bool));
                    case Intrinsic.Less: return new V($"({x} < {y})" , typeof(bool));
                    case Intrinsic.Greater: return new V($"({x} > {y})" , typeof(bool));
                }
            }
            else if (numeric(ta) && numeric(tb))
            {
                string dx = ta is Ty.Double ? x : $"(double){x}";
                string dy = tb is Ty.Double ? y : $"(double){y}";
                switch (op)
                {
                    case Intrinsic.Add: return new V($"({dx} + {dy})" , typeof(double));
                    case Intrinsic.Sub: return new V($"({dx} - {dy})" , typeof(double));
                    case Intrinsic.Mul: return new V($"({dx} * {dy})" , typeof(double));
                    case Intrinsic.Div: return tb is Ty.Long ? new V($"S.DivDL({callee} , {dx} , {y})" , typeof(double)) : new V($"({dx} / {dy})" , typeof(double));
                    case Intrinsic.Mod: return tb is Ty.Long ? new V($"S.ModDL({callee} , {dx} , {y})" , typeof(double)) : new V($"({dx} % {dy})" , typeof(double));
                    case Intrinsic.Less when ta is Ty.Double && tb is Ty.Double: return new V($"({x} < {y})" , typeof(bool));
                    case Intrinsic.Greater when ta is Ty.Double && tb is Ty.Double: return new V($"({x} > {y})" , typeof(bool));
                }
            }
            else if (ta is Ty.String && tb is Ty.String)
            {
                if (op is Intrinsic.Add) return new V($"string.Concat({x} , {y})" , typeof(string));
                if (op is Intrinsic.Equal) return new V($"string.Equals({x} , {y})" , typeof(bool));
            }
            else if (ta is Ty.Bool && tb is Ty.Bool)
            {
                if (op is Intrinsic.Equal) return new V($"({x} == {y})" , typeof(bool));
                if (op is Intrinsic.And) return new V($"({x} & {y})" , typeof(bool));
                if (op is Intrinsic.Or) return new V($"({x} | {y})" , typeof(bool));
            }
            string generic = $"S.Intrinsic2({callee} , {cbox(a)} , {cbox(b)})";
            return result is Ty.Bool ? new V($"((bool){generic})" , typeof(bool)) : new V(generic , typeof(object));
        }

        string args_array_cs(IrExpr[] arguments)
        {
            V[] values = Array.ConvertAll(arguments , cs_expression);
            return values.Length is 0 ? "System.Array.Empty<object>()" : "new object[] { " + string.Join(" , " , values.Select(cbox)) + " }";
        }

        V call_cs(IrCall call)
        {
            string name = call.Name.Name;
            Slot? fslot = function_slot(call.Name);
            if (fslot is not null)
                return direct_call_cs(fslot , call);
            Slot[] slots = slots_of(call.Name);
            string r = "t" + next_id();
            //사실 모드에서 인자 없이 쓴 변수 이름: 박싱하지 않고 검사기의 타입 그대로
            Type rt = facts_mode && !call.HasArguments ? clr(type_of(call)) : typeof(object);
            line($"{tname(rt)} {r};");
            bool first = true;
            foreach (Slot slot in slots)
            {
                if (!slot.IsVar)
                    continue;
                open($"{(first ? "" : "else ")}if (d{slot.Id})");
                V raw = new($"v{slot.Id}" , clr(slot.Type));
                line($"{r} = {(rt == typeof(object) ? value_call_cs(raw , call).Code : cconv(raw , rt))};");
                close();
                first = false;
            }
            if (!first)
                open("else");
            if (call.Name.Import < 0)
                line($"{r} = {cfail(call.Missing , rt)};");
            else if (imports_variable(call.Name.Import))
            {
                V x = value_call_cs(let(new V($"S.GetGlobal(i{call.Name.Import})" , typeof(object))) , call);
                line($"{r} = {cconv(x , rt)};");
            }
            else
                line($"{r} = {cconv(new V($"S.CallGlobal(i{call.Name.Import} , {literal(name)} , {args_array_cs(call.Arguments)})" , typeof(object)) , rt)};");
            if (!first)
                close();
            return new V(r , rt , true);
        }

        V direct_call_cs(Slot slot , IrCall call)
        {
            Function f = slot.Func!;
            string name = $"f{slot.Id}";
            if (facts_mode)
            {
                string key = call_target(call , f.Def);
                f = inst_function(f.Def , key);
                //이번에 처음 필요해진 인스턴스: 이번 결과는 버리고 다시 만듦
                name = slot.FuncNames!.GetValueOrDefault(key) ?? "missing";
            }
            line($"if (!fd{slot.Id}) {cfail(call.Missing , typeof(object))};");
            List<string> args = new();
            for (int k = 0 ; k < Math.Max(f.Arity , call.Arguments.Length) ; k++)
            {
                if (k < call.Arguments.Length)
                {
                    V a = cs_expression(call.Arguments[k]);
                    if (k < f.Arity) args.Add(cconv(a , clr(f.Params[k])));
                }
                else
                    args.Add(cconv(Null , clr(f.Params[k])));
            }
            return new V($"{name}({string.Join(" , " , args)})" , clr(f.Ret));
        }

        //변수 이름을 함수처럼 쓴 경우 (배열/리스트 읽기/쓰기, 그 외는 값)
        V value_call_cs(V value , IrCall call)
        {
            if (!call.HasArguments)
                return let(new V(cbox(value) , typeof(object)));
            string v = "t" + next_id() , r = "t" + next_id();
            line($"object {v} = {cbox(value)};");
            line($"object {r};");
            open($"if ({v} is System.Collections.IList)");
            Ty index = call.Arguments.Length is 1 or 2 ? norm(type_of(call.Arguments[0])) : Ty.Object;
            if (index is Ty.Long && call.Arguments.Length is 1)
            {
                V i = cs_expression(call.Arguments[0]);
                line($"{r} = S.GetAt({v} , {i.Code});");
            }
            else if (index is Ty.Long && call.Arguments.Length is 2)
            {
                V i = cs_expression(call.Arguments[0]);
                V x = cs_expression(call.Arguments[1]);
                line($"S.SetAt({v} , {i.Code} , {cbox(x)});");
                line($"{r} = null;");
            }
            else
                line($"{r} = S.ListCall({v} , {args_array_cs(call.Arguments)});");
            close();
            line($"else {r} = {(call.Closed ? $"S.EarlyOf({v})" : v)};");
            return new V(r , typeof(object) , true);
        }

        void write_cs(Slot slot , V raw)
        {
            string assign = $"v{slot.Id} = {cconv(raw , clr(slot.Type))};";
            if (slot.HasConst)
                line($"if (c{slot.Id}) S.Fail<object>({literal("상수를 변경할수 없습니다.")}); else {assign}");
            else
                line(assign);
        }

        V assign_cs(IrAssign assign)
        {
            V value = cs_expression(assign.Value);
            Slot[] slots = slots_of(assign.Name);
            void store(V raw)
            {
                for (int k = 0 ; k < slots.Length ; k++)
                {
                    open($"{(k is 0 ? "" : "else ")}if (d{slots[k].Id})");
                    write_cs(slots[k] , raw);
                    close();
                }
                if (slots.Length > 0)
                    open("else");
                if (assign.Name.Import >= 0)
                    line($"S.SetGlobal(i{assign.Name.Import} , {literal(assign.Name.Name)} , {cbox(raw)});");
                else if (assign.Create < 0)
                    line($"S.Fail<object>({literal($"'{assign.Name.Name}'은/는 알수없는 변수/함수 입니다.")});");
                else
                {
                    Slot create = slot_at(0 , assign.Create);
                    write_cs(create , raw);
                    line($"d{create.Id} = true;");
                }
                if (slots.Length > 0)
                    close();
            }
            if (value.Type != typeof(object))
            {
                store(value);
                return Null;
            }
            string r = "t" + next_id() , raw_value = "t" + next_id();
            line($"object {r};");
            open($"if (S.IsEarly({value.Code}))");
            line($"object {raw_value} = S.EarlyValue({value.Code});");
            store(new V(raw_value , typeof(object) , true));
            line($"{r} = S.NewEarly();");
            close();
            open("else");
            store(value);
            line($"{r} = null;");
            close();
            return new V(r , typeof(object) , true);
        }

        string truth_cs(IrExpr ir)
        {
            V e = cs_expression(ir);
            return e.Type == typeof(bool) ? e.Code : $"S.Truth({cbox(e)})";
        }

        string condition_cs(IrExpr[] args)
        {
            if (args.Length is 0)
                return "false";
            V first = let(new V(truth_cs(args[0]) , typeof(bool)));
            for (int i = 1 ; i < args.Length ; i++)
                cs_expression(args[i]);
            return first.Code;
        }

        void statements_cs(IrBlock block)
        {
            foreach (IrStmt s in block.Statements)
                cs_statement(s);
        }

        Type return_type_cs => current is null ? typeof(object) : clr(current.Ret);

        void cs_statement(IrStmt s)
        {
            switch (s.Kind)
            {
                case IrKind.Empty:
                    return;
                case IrKind.Expression:
                {
                    V e = cs_expression(s.Expression!);
                    if (!needs_check(s.Expression! , false))
                        return;
                    string r = "t" + next_id();
                    line($"object {r} = S.Check({cbox(e)});");
                    open($"if ({r} != null)");
                    return_with(cconv(new V($"S.ReturnValue({r})" , typeof(object)) , return_type_cs));
                    close();
                    return;
                }
                case IrKind.If:
                {
                    string c = condition_cs(s.Arguments);
                    open($"if ({c})");
                    statements_cs(s.Body!);
                    close();
                    if (s.Else is not null)
                    {
                        open("else");
                        statements_cs(s.Else);
                        close();
                    }
                    return;
                }
                case IrKind.While:
                {
                    string end = "brk" + next_id();
                    break_names[s] = end;
                    open("while (true)");
                    string c = condition_cs(s.Arguments);
                    line($"if (!{c}) break;");
                    statements_cs(s.Body!);
                    close();
                    line($"{end}: ;");
                    return;
                }
                case IrKind.Repeat:
                {
                    string end = "brk" + next_id();
                    break_names[s] = end;
                    string n = "n" + next_id();
                    if (s.Arguments.Length is 0)
                        line($"long {n} = 0;");
                    else
                    {
                        V first = cs_expression(s.Arguments[0]);
                        line($"long {n} = {(first.Type == typeof(long) ? first.Code : $"S.RepeatCount({cbox(first)})")};");
                        for (int i = 1 ; i < s.Arguments.Length ; i++)
                            cs_expression(s.Arguments[i]);
                    }
                    open($"while ({n} > 0)");
                    statements_cs(s.Body!);
                    line($"{n}--;");
                    close();
                    line($"{end}: ;");
                    return;
                }
                case IrKind.For:
                {
                    if (s.Arguments.Length is not 1)
                    {
                        foreach (IrExpr a in s.Arguments) cs_expression(a);
                        line(cfail("for 다음에는 변수 이름과 순회할 값 하나만 와야 합니다." , typeof(object)) + ";");
                        return;
                    }
                    string end = "brk" + next_id();
                    break_names[s] = end;
                    string items = "items" + next_id() , i = "i" + next_id();
                    V source = cs_expression(s.Arguments[0]);
                    line($"object[] {items} = S.ForItems({cbox(source)});");
                    Frame inner = facts_mode ? inst_frame(s , ctx_key) : frames[s];
                    Slot var = inner.Slots[s.Slot];
                    open($"for (int {i} = 0 ; {i} < {items}.Length ; {i}++)");
                    cs_frame(inner , s.Body! , () => line($"v{var.Id} = {cconv(new V($"{items}[{i}]" , typeof(object)) , clr(var.Type))}; d{var.Id} = true;"));
                    close();
                    line($"{end}: ;");
                    return;
                }
                case IrKind.Func:
                    line($"fd{stack[^1].Slots[s.Slot].Id} = true;");
                    return;
                case IrKind.Return:
                {
                    if (s.Arguments.Length is 0)
                    {
                        return_with(cconv(Null , return_type_cs));
                        return;
                    }
                    V first = let(new V(cconv(cs_expression(s.Arguments[0]) , return_type_cs) , return_type_cs));
                    for (int i = 1 ; i < s.Arguments.Length ; i++)
                        cs_expression(s.Arguments[i]);
                    return_with(first.Code);
                    return;
                }
                case IrKind.Break:
                    line($"goto {break_names[s.Loop!]};");
                    return;
                case IrKind.Local:
                {
                    Slot slot = stack[^1].Slots[s.Slot];
                    V value = s.Expression is null ? Null : cs_expression(s.Expression);
                    open($"if (d{slot.Id})");
                    write_cs(slot , value);
                    close();
                    line($"else v{slot.Id} = {cconv(value , clr(slot.Type))};");
                    line($"d{slot.Id} = true;");
                    return;
                }
                case IrKind.Param:
                {
                    Slot slot = stack[^1].Slots[s.Slot];
                    line($"v{slot.Id} = {cconv(new V($"p{current!.Id}_{s.ParameterIndex}" , clr(current.Params[s.ParameterIndex])) , clr(slot.Type))}; d{slot.Id} = true;{(slot.HasConst ? $" c{slot.Id} = false;" : "")}");
                    return;
                }
                default:
                    throw new Unsupported(s.Kind.ToString());
            }
        }
    }

    /// <summary>[실험] C#으로 내보낸 제한 모드 프로그램이 쓰는 실행 도우미 (생성된 코드 전용)</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static partial class AotSupport
    {
        public static object?[] Bind(byte[] image , Runtime host) => JyunoProgramImage.BindImports(image , host);
        public static T Fail<T>(string message) => throw new JyunoException(message);
        public static T Impossible<T>(object? value) => throw new JyunoException("[IlTyped] 정적 타입 검사가 증명한 타입과 다른 값입니다.");
        public static T ThrowParen<T>() => throw parenthesis_error();
        public static object Box(bool value) => StaticJit.box_bool(value);
        public static bool Truth(object? value) => StaticJit.truth(value);
        public static object? Intrinsic1(object? callee , object? x) => StaticJit.intrinsic1((NativeFunction)callee! , x);
        public static object? Intrinsic2(object? callee , object? x , object? y) => StaticJit.intrinsic2((NativeFunction)callee! , x , y);
        public static object? Intrinsic3(object? callee , object? x , object? y , object? z) => StaticJit.intrinsic3((NativeFunction)callee! , x , y , z);
        public static long DivLL(object? callee , long a , long b) => b != 0 && !(a == long.MinValue && b == -1) ? a / b : StaticJit.div_ll((NativeFunction)callee! , a , b);
        public static long ModLL(object? callee , long a , long b) => b != 0 && !(a == long.MinValue && b == -1) ? a % b : StaticJit.mod_ll((NativeFunction)callee! , a , b);
        public static double DivDL(object? callee , double a , long b) => b != 0 ? a / b : StaticJit.div_dl((NativeFunction)callee! , a , b);
        public static double ModDL(object? callee , double a , long b) => b != 0 ? a % b : StaticJit.mod_dl((NativeFunction)callee! , a , b);
        public static object? ListCall(object? list , object?[] index) => StaticJit.list_call((System.Collections.IList)list! , index);
        public static object? GetAt(object? list , long index) => Collections.GetAt((System.Collections.IList)list! , index);
        public static void SetAt(object? list , long index , object? value) => Collections.SetAt((System.Collections.IList)list! , index , value);
        public static object? Paren(object? value) => StaticJit.paren_check(value);
        public static bool IsEarly(object? value) => value is EarlyEnd;
        public static object? EarlyValue(object? value) => StaticJit.early_value(value);
        public static object NewEarly() => StaticJit.new_early();
        public static object EarlyOf(object? value) => StaticJit.early_of(value);
        public static object? Check(object? value) => StaticJit.statement_check(value);
        public static object? ReturnValue(object? info) => StaticJit.return_value((ReturnInfo)info!);
        public static long RepeatCount(object? value) => StaticJit.repeat_count(value);
        public static object?[] ForItems(object? value) => StaticJit.for_items(value);
        public static object? GetGlobal(object? holder) => ((VariableInterface)holder!).Get();
        public static void SetGlobal(object? holder , string name , object? value) => StaticJit.set_global(holder , name , value);
        public static object? CallGlobal(object? holder , string name , object?[] args) =>
            holder is FunctionInterface fi ? fi.Execute(args) : throw new JyunoException($"'{name}'는 함수 또는 변수가 아닙니다.");
        public static bool IsGrammar(Exception e) => e is GrammarErrorException;
        public static object GrammarError(Exception e) => ((GrammarErrorException)e).Error;
    }
}
