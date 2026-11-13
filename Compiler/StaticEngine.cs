using Jyuno.Analysis;
using Jyuno.Compiler;
using Jyuno.Language;

namespace Jyuno;

/// <summary>
/// [프로토타입] 제한 모드로 미리 해석하고 호스트와 연결(link)까지 마친, 바로 실행할수 있는 프로그램
/// </summary>
public sealed class JyunoProgram
{
    internal JyunoProgram(Func<object?> run) { this.run = run; }
    readonly Func<object?> run;
    bool running;
    /// <summary>[실험] 실제로 쓰인 실행 방식 (IL 컴파일을 못했다면 이유)</summary>
    public string Engine { get; internal init; } = "Closure";
    /// <summary>처음부터 끝까지 실행합니다. (Interpreter.Run과 같은 값을 돌려줌)</summary>
    public object? Run()
    {
        if (running)
            throw new JyunoException("이미 실행중입니다.");
        running = true;
        try
        {
            return run();
        }
        finally
        {
            running = false;
        }
    }
}

public partial class Interpreter
{
    internal static bool SupportsIntrinsic(Intrinsic op , int arity) => supports(op , arity);

    //실행 중인 영역 하나 (스크립트 최상위, 함수 호출 한번, for 반복 한번)
    internal sealed class Frame
    {
        public Frame(int size , Frame? parent , object?[]? args)
        {
            Slots = size is 0 ? Array.Empty<object?>() : new object?[size];
            Parent = parent;
            Args = args;
        }
        //이름이 가리키는 것 (JyunoVariable, 상수, 함수). null이면 아직 없음
        public readonly object?[] Slots;
        public readonly Frame? Parent;
        //이 영역이 들어있는 함수 호출의 인자
        public readonly object?[]? Args;
    }

    //문장이 끝난 이유 (null이면 다음 문장으로)
    internal abstract class Signal { }
    sealed class ReturnSignal : Signal
    {
        public ReturnSignal(object? value) { Value = value; }
        public readonly object? Value;
    }
    sealed class BreakSignal : Signal
    {
        public BreakSignal(IrStmt loop) { Loop = loop; }
        public readonly IrStmt Loop;
    }
    sealed class JumpSignal : Signal
    {
        public JumpSignal(IrTarget target) { Target = target; }
        public readonly IrTarget Target;
    }

    delegate object? Eval(Frame frame);
    delegate Signal? Exec(Frame frame);

    //IR → 클로저 (호스트와 연결된 가져오기 표를 사용)
    internal sealed class StaticEngine
    {
        public StaticEngine(IrProgram program , object?[] imports)
        {
            this.program = program;
            this.imports = imports;
        }
        readonly IrProgram program;
        readonly object?[] imports;
        int depth;
        //goto로 이동해 오는 문장이 있는 블록
        readonly HashSet<IrBlock> targeted = new();
        void find_targets(IrBlock block)
        {
            foreach (IrStmt s in block.Statements)
            {
                foreach (IrTarget t in s.Targets)
                    if (t.Statement is not null)
                        targeted.Add(t.Statement.Parent);
                if (s.Body is not null) find_targets(s.Body);
                if (s.Else is not null) find_targets(s.Else);
            }
        }

        public Func<object?> Compile()
        {
            find_targets(program.Root);
            Exec root = block(program.Root);
            int size = program.TopFrameSize;
            return () => {
                depth = 0;
                Frame top = new(size , null , null);
                try
                {
                    Signal? signal = root(top);
                    return signal is ReturnSignal r ? r.Value : null;
                }
                catch (GrammarErrorException e)
                {
                    return e.Error;
                }
            };
        }

        static Frame up(Frame frame , int hops)
        {
            for (; hops > 0 ; hops--)
                frame = frame.Parent!;
            return frame;
        }

        //이름이 가리키는 것 찾기 (안쪽 영역 → 바깥 영역 → 전역)
        Func<Frame , object?> resolver(IrName name)
        {
            var candidates = name.Candidates;
            object? global = name.Import >= 0 ? imports[name.Import] : null;
            bool has_global = name.Import >= 0;
            if (candidates.Length is 0)
                return _ => global;
            if (candidates.Length is 1 && !has_global)
            {
                var (hops , slot) = candidates[0];
                if (hops is 0)
                    return f => f.Slots[slot];
                if (hops is 1)
                    return f => f.Parent!.Slots[slot];
                return f => up(f , hops).Slots[slot];
            }
            return f => {
                foreach (var (hops , slot) in candidates)
                {
                    object? holder = up(f , hops).Slots[slot];
                    if (holder is not null)
                        return holder;
                }
                return global;
            };
        }

        Eval[] list(IrExpr[] expressions) => Array.ConvertAll(expressions , expression);

        static Func<Frame , object?[]> arguments(Eval[] evals)
        {
            switch (evals.Length)
            {
                case 0: return _ => Array.Empty<object?>();
                case 1: { Eval a = evals[0]; return f => new[] { a(f) }; }
                case 2: { Eval a = evals[0], b = evals[1]; return f => { object? x = a(f); return new[] { x , b(f) }; }; }
                default:
                    return f => {
                        object?[] values = new object?[evals.Length];
                        for (int i = 0 ; i < values.Length ; i++)
                            values[i] = evals[i](f);
                        return values;
                    };
            }
        }

        Eval expression(IrExpr ir)
        {
            switch (ir)
            {
                case IrConst c:
                {
                    object? value = c.Value;
                    return _ => value;
                }
                case IrRead read:
                {
                    string name = read.Name.Name;
                    var find = resolver(read.Name);
                    return f => {
                        object? holder = find(f);
                        if (holder is JyunoVariable v)
                            return v.Get();
                        if (holder is null)
                            throw new JyunoException($"'{name}'은/는 알수없는 변수/함수 입니다.");
                        if (holder is VariableInterface vi)
                            return vi.Get();
                        throw new JyunoException($"'{name}'에서 값을 가져올수 없습니다.");
                    };
                }
                case IrIntrinsic intrinsic:
                {
                    NativeFunction callee = (NativeFunction)imports[intrinsic.Import]!;
                    Intrinsic op = callee.Intrinsic;
                    Eval[] args = list(intrinsic.Arguments);
                    Eval a = args[0];
                    switch (args.Length)
                    {
                        case 1:
                            return f => {
                                object? x = a(f);
                                object r = fast1(op , x);
                                return r != Miss ? r : callee.Execute(new[] { x });
                            };
                        case 2:
                        {
                            Eval b = args[1];
                            return f => {
                                object? x = a(f);
                                object? y = b(f);
                                object r = fast2(op , x , y);
                                return r != Miss ? r : callee.Execute(new[] { x , y });
                            };
                        }
                        default:
                        {
                            Eval b = args[1], c = args[2];
                            return f => {
                                object? x = a(f);
                                object? y = b(f);
                                object? z = c(f);
                                object r = fast3(op , x , y , z);
                                return r != Miss ? r : callee.Execute(new[] { x , y , z });
                            };
                        }
                    }
                }
                case IrCall call:
                    return call_expression(call);
                case IrAssign assign:
                {
                    string name = assign.Name.Name;
                    var find = resolver(assign.Name);
                    Eval value = expression(assign.Value);
                    int create = assign.Create;
                    return f => {
                        object? ret = value(f);
                        EarlyEnd? early = null;
                        if (ret is EarlyEnd e)
                        {
                            early = e;
                            ret = e.Value;
                        }
                        object? holder = find(f);
                        //대부분은 스크립트 변수: 가상 호출 없이 바로 씀 (상수는 JyunoConstantVariable이라 제외됨)
                        if (holder is not null && holder.GetType() == typeof(JyunoVariable))
                            ((JyunoVariable)holder).value = ret;
                        else if (holder is VariableInterface vi)
                            vi.Set(ret);
                        else if (holder is null && create >= 0)
                            f.Slots[create] = new JyunoVariable(ret);
                        else if (holder is null)
                            throw new JyunoException($"'{name}'은/는 알수없는 변수/함수 입니다.");
                        else
                            throw new JyunoException($"'{name}'은/는 변수가 아닙니다.");
                        return early is null ? null : new EarlyEnd(null);
                    };
                }
                case IrConstDef definition:
                {
                    int slot = definition.Slot;
                    object value = definition.Value;
                    return f => {
                        f.Slots[slot] = new JyunoConstantVariable(value);
                        return null;
                    };
                }
                case IrLabel label:
                {
                    //인터프리터와 같이 int 줄 번호
                    int slot = label.Slot, line = label.Line;
                    return f => {
                        f.Slots[slot] = new JyunoVariable(line);
                        return null;
                    };
                }
                case IrParen paren:
                {
                    Eval inner = expression(paren.Inner);
                    if (paren.Closed)
                        return f => {
                            object? value = inner(f);
                            if (value is EarlyEnd)
                                throw parenthesis_error();
                            return value;
                        };
                    return f => {
                        inner(f);
                        throw parenthesis_error();
                    };
                }
                case IrOperator op:
                    return operator_expression(op);
                default:
                    throw new InvalidOperationException($"알수 없는 식 {ir.Op}");
            }
        }

        //괄호 안의 식이 ')' 전에 끝났다면(EarlyEnd) 연산자를 계산하기 전에 오류 (Compiler/OperatorCompiler.cs의 operand)
        internal static object? operand(object? value) => value is EarlyEnd ? throw parenthesis_error() : value;

        //1.6.0 연산자 (Compiler/OperatorCompiler.cs와 같은 계산)
        Eval operator_expression(IrOperator ir)
        {
            Eval[] o = list(ir.Operands);
            Eval a = o[0];
            switch (ir.Symbol)
            {
                case "&&":
                {
                    Eval b = o[1];
                    return f => Parser.IsTrue(operand(a(f))) ? StaticBool(Parser.IsTrue(operand(b(f)))) : StaticBool(false);
                }
                case "||":
                {
                    Eval b = o[1];
                    return f => Parser.IsTrue(operand(a(f))) ? StaticBool(true) : StaticBool(Parser.IsTrue(operand(b(f))));
                }
                case "??":
                {
                    Eval b = o[1];
                    return f => operand(a(f)) ?? operand(b(f));
                }
                case "?:":
                {
                    Eval t = o[1], e = o[2];
                    return f => Parser.IsTrue(operand(a(f))) ? operand(t(f)) : operand(e(f));
                }
            }
            Op code = OperatorOf(ir.Symbol);
            if (o.Length is 1)
                return f => StaticUnary(code , operand(a(f)));
            Eval y = o[1];
            return f => {
                object? left = operand(a(f));
                return StaticBinary(code , left , operand(y(f)));
            };
        }

        Eval call_expression(IrCall call)
        {
            string name = call.Name.Name;
            var find = resolver(call.Name);
            Func<Frame , object?[]> args = arguments(list(call.Arguments));
            bool has_arguments = call.HasArguments, closed = call.Closed;
            string missing = call.Missing;
            //전역 함수만 가리키는 이름: 연결할 때 정해진 함수를 바로 호출
            if (call.Name.Candidates.Length is 0 && call.Name.Import >= 0 && imports[call.Name.Import] is FunctionInterface fixed_function)
                return f => fixed_function.Execute(args(f));
            return f => {
                object? holder = find(f);
                if (holder is null)
                    throw new JyunoException(missing);
                if (holder is FunctionInterface fi)
                    return fi.Execute(args(f));
                if (holder is VariableInterface vi)
                {
                    object? value = vi.Get();
                    if (has_arguments)
                    {
                        if (value is System.Collections.IList collection)
                        {
                            object?[] index = args(f);
                            switch (index.Length)
                            {
                                case 0:
                                    return collection;
                                case 1:
                                    return Collections.Get(collection , index[0]);
                                case 2:
                                    Collections.Set(collection , index[0] , index[1]);
                                    return null;
                                default:
                                    throw new JyunoException("배열/리스트는 '(이름) (인덱스)' 또는 '(이름) (인덱스) (값)' 형식으로만 쓸수 있습니다.");
                            }
                        }
                        if (closed)
                            return new EarlyEnd(value);
                    }
                    return value;
                }
                throw new JyunoException($"'{name}'는 함수 또는 변수가 아닙니다.");
            };
        }

        //─────────── 문장 ───────────

        Exec block(IrBlock ir)
        {
            Exec[] statements = Array.ConvertAll(ir.Statements , statement);
            IrBlock self = ir;
            switch (statements.Length)
            {
                case 0: return _ => null;
                case 1 when !targeted.Contains(ir):
                {
                    Exec only = statements[0];
                    return only;
                }
            }
            return f => {
                for (int i = 0 ; i < statements.Length ; i++)
                {
                    Signal? signal = statements[i](f);
                    if (signal is null)
                        continue;
                    //이 블록의 문장으로 이동
                    if (signal is JumpSignal { Target: { Statement: IrStmt target } jump } && target.Parent == self && jump.Kind is GotoTargetKind.Before or GotoTargetKind.After)
                    {
                        i = (jump.Kind is GotoTargetKind.Before ? target.Index : target.Index + 1) - 1;
                        continue;
                    }
                    return signal;
                }
                return null;
            };
        }


        static bool truth(object? value) => value is bool b ? b : Parser.IsTrue(value);

        static bool condition(Eval[] args , Frame f)
        {
            if (args.Length is 1)
                return truth(args[0](f));
            object? first = null;
            for (int i = 0 ; i < args.Length ; i++)
            {
                object? value = args[i](f);
                if (i is 0)
                    first = value;
            }
            return args.Length > 0 && truth(first);
        }

        //반복문 본문이 돌려준 신호 처리: null이면 다음 반복, Stop이면 반복문을 끝냄, 그 외는 바깥으로
        static readonly Signal Stop = new BreakSignal(null!);
        static Signal? loop_signal(Signal signal , IrStmt self)
        {
            if (signal is BreakSignal b && b.Loop == self)
                return Stop;
            if (signal is JumpSignal { Target: { Kind: GotoTargetKind.Continue } jump } && jump.Statement == self)
                return null;
            return signal;
        }

        Exec statement(IrStmt ir)
        {
            Eval[] args = list(ir.Arguments);
            int line = ir.Line;
            switch (ir.Kind)
            {
                case IrKind.Empty:
                    return _ => null;
                case IrKind.Expression:
                {
                    Eval e = expression(ir.Expression!);
                    return f => {
                        object? value = e(f);
                        //호스트 함수가 ReturnInfo/GrammarError를 돌려준 경우 (인터프리터와 같게)
                        if (value is ReturnInfo ri)
                            return new ReturnSignal(ri.value);
                        if (value is GrammarError error)
                            throw new GrammarErrorException(error);
                        return null;
                    };
                }
                case IrKind.If:
                {
                    Exec then = block(ir.Body!);
                    Exec? otherwise = ir.Else is null ? null : block(ir.Else);
                    if (args.Length is 1)
                    {
                        Eval c = args[0];
                        if (otherwise is null)
                            return f => truth(c(f)) ? then(f) : null;
                        return f => truth(c(f)) ? then(f) : otherwise(f);
                    }
                    return f => condition(args , f) ? then(f) : otherwise?.Invoke(f);
                }
                case IrKind.While:
                {
                    Exec body = block(ir.Body!);
                    if (args.Length is 1)
                    {
                        Eval c = args[0];
                        return f => {
                            while (truth(c(f)))
                            {
                                Signal? signal = body(f);
                                if (signal is not null && (signal = loop_signal(signal , ir)) is not null)
                                    return signal == Stop ? null : signal;
                            }
                            return null;
                        };
                    }
                    return f => {
                        while (condition(args , f))
                        {
                            Signal? signal = body(f);
                            if (signal is not null && (signal = loop_signal(signal , ir)) is not null)
                                return signal == Stop ? null : signal;
                        }
                        return null;
                    };
                }
                case IrKind.Repeat:
                {
                    Exec body = block(ir.Body!);
                    Func<Frame , object?[]> count = arguments(args);
                    return f => {
                        object?[] values = count(f);
                        if (values.Length is 0 || !Values.TryGetInteger(values[0] , out long n) || n <= 0)
                            return null;
                        for (; n > 0 ; n--)
                        {
                            Signal? signal = body(f);
                            if (signal is not null && (signal = loop_signal(signal , ir)) is not null)
                                return signal == Stop ? null : signal;
                        }
                        return null;
                    };
                }
                case IrKind.For:
                {
                    Exec body = block(ir.Body!);
                    Func<Frame , object?[]> source = arguments(args);
                    int size = ir.FrameSize, slot = ir.Slot;
                    return f => {
                        object?[] values = source(f);
                        if (values.Length is not 1)
                            throw new JyunoException("for 다음에는 변수 이름과 순회할 값 하나만 와야 합니다.");
                        object?[] items = values[0] switch {
                            string text => text.Select(c => (object?)c.ToString()).ToArray(),
                            System.Collections.IList collection => collection.Cast<object?>().ToArray(),
                            _ => throw new JyunoException($"'{values[0] ?? "null"}'은/는 순회할수 없습니다. (배열/리스트/문자열만 가능)")
                        };
                        foreach (object? item in items)
                        {
                            Frame inner = new(size , f , f.Args);
                            inner.Slots[slot] = new JyunoVariable(item);
                            Signal? signal = body(inner);
                            if (signal is not null && (signal = loop_signal(signal , ir)) is not null)
                                return signal == Stop ? null : signal;
                        }
                        return null;
                    };
                }
                case IrKind.Func:
                {
                    Exec body = block(ir.Body!);
                    int slot = ir.Slot, size = ir.FrameSize;
                    return f => {
                        f.Slots[slot] = new StaticFunction(this , body , size , f);
                        return null;
                    };
                }
                case IrKind.Return:
                    return f => {
                        object? first = null;
                        for (int i = 0 ; i < args.Length ; i++)
                        {
                            object? value = args[i](f);
                            if (i is 0)
                                first = value;
                        }
                        return new ReturnSignal(first);
                    };
                case IrKind.Break:
                {
                    BreakSignal signal = new(ir.Loop!);
                    return _ => signal;
                }
                case IrKind.Param:
                {
                    int slot = ir.Slot, index = ir.ParameterIndex;
                    return f => {
                        object?[] call_args = f.Args!;
                        f.Slots[slot] = new JyunoVariable(index < call_args.Length ? call_args[index] : null);
                        return null;
                    };
                }
                case IrKind.Local:
                {
                    int slot = ir.Slot;
                    string local_name = ir.Name!;
                    Eval? value = ir.Expression is null ? null : expression(ir.Expression);
                    return f => {
                        object? v = value?.Invoke(f);
                        object? existing = f.Slots[slot];
                        if (existing is null)
                            f.Slots[slot] = new JyunoVariable(v);
                        else if (existing is VariableInterface vi)
                            vi.Set(v);
                        else
                            throw new JyunoException($"'{local_name}'은/는 변수가 아닙니다.");
                        return null;
                    };
                }
                case IrKind.Goto:
                {
                    IrTarget[] targets = ir.Targets;
                    JumpSignal[] signals = Array.ConvertAll(targets , t => new JumpSignal(t));
                    Func<Frame , object?[]> target_of = arguments(args);
                    int owner = program.LineOwner[line];
                    int count = program.LineCount;
                    int[] owners = program.LineOwner;
                    return f => {
                        object?[] target = target_of(f);
                        if (target.Length is 0)
                            throw new JyunoException("이동할 값을 넣지 않았습니다.");
                        if (!Values.TryGetInteger(target[0] , out long goto_line))
                            throw new JyunoException("실행 위치는 음이 아닌 정수여야 합니다.");
                        if (goto_line < 0)
                            throw new JyunoException("실행 위치를 음수로 이동할수 없습니다.");
                        if (goto_line > count)
                            throw new JyunoException("실행 위치가 스크립트의 범위를 벗어났습니다.");
                        if ((goto_line < count ? owners[goto_line] : -1) != owner)
                            throw new JyunoException("goto로 함수 안팎을 넘나들수 없습니다.");
                        for (int i = 0 ; i < targets.Length ; i++)
                        {
                            if (targets[i].Line == goto_line)
                                return signals[i];
                        }
                        throw new JyunoException($"제한 모드: goto로 {goto_line}번 줄로 이동할수 없습니다.");
                    };
                }
                default:
                    throw new InvalidOperationException($"알수 없는 문장 {ir.Kind}");
            }
        }

        //스크립트에서 func로 만든 함수
        sealed class StaticFunction : FunctionInterface
        {
            public StaticFunction(StaticEngine engine , Exec body , int size , Frame scope)
            {
                this.engine = engine;
                this.body = body;
                this.size = size;
                this.scope = scope;
            }
            readonly StaticEngine engine;
            readonly Exec body;
            readonly int size;
            readonly Frame scope;
            public object? Execute(params object?[] args)
            {
                if (engine.depth >= MaxCallDepth)
                    throw new JyunoException($"함수 호출이 너무 깊습니다. (최대 {MaxCallDepth}번)");
                engine.depth++;
                try
                {
                    Signal? signal = body(new Frame(size , scope , args));
                    return signal is ReturnSignal r ? r.Value : null;
                }
                finally
                {
                    engine.depth--;
                }
            }
        }
    }
}
