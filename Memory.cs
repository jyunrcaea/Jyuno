using Jyuno.Language;
using System.Collections;

namespace Jyuno;

//메모리 한도와 한 줄 안의 실행 중단 (1.7.0부터)
//
//메모리 한도: 스크립트가 붙잡고 있는 값(변수, 반복문 상태, 호출 인자, 구조체 값)의 크기를 가끔 재서, 한도를 넘으면 JyunoException
//- 언제 잴지는 .NET이 스레드마다 세는 할당량(GC.GetAllocatedBytesForCurrentThread, 호출 한번에 약 1.5ns)으로 정함
//  반복문의 다음 반복, 함수 호출, 큰 값을 만드는 내장 함수에서 할당량을 읽고, 지난번에 잰 뒤로 일정량(step) 이상 할당했을 때만 잼
//- 할당량은 버려진 값(가비지)도 세므로 그것만으로는 판단하지 않고, 붙잡고 있는 값을 따라가서 크기를 더함 (공유된 값은 한번만)
//  잰 크기만큼 다시 할당해야 다음에 재므로, 재는 비용은 할당량에 비례하는 정도 (GC와 같은 방식)
//- 1MB보다 작은 할당은 일단 실행하고 다음에 잴 때 확인하며, 한도에 가까울수록 자주 재므로 한도를 약 1MB까지 넘을수 있음
//- 1MB보다 큰 값을 만드는 내장 함수(array.new, 문자열 반복 등)는 만들기 전에 크기를 계산해서 남은 한도와 비교함 (Reserve)
//- 변수에 넣기 전의 큰 값(같은 줄의 다른 인자, 함수가 돌려준 값)은 그 줄이 끝날 때까지 roots에 붙잡아서 함께 셈 (Hold)
//크기는 64비트 .NET 기준의 어림값이며, 프로그램이 제공한 값(AddVariable, C# 함수가 돌려준 객체)의 내부는 세지 않음
//
//한 줄 안의 실행 중단: 줄 사이에서만 CancellationToken을 확인하면 한 줄이 끝나지 않는 계산(공유된 리스트를 담은 구조체 비교, 인라인된 재귀 등)을
//멈출수 없으므로, 함수 호출과 오래 걸리는 내장 계산의 반복 안에서도 확인함 (Tick)
public partial class Interpreter
{
    /// <summary>
    /// 이 인터프리터의 스크립트가 붙잡고 있을수 있는 메모리의 대략적인 최대 크기 (바이트, 1.7.0부터). 0이면 제한하지 않습니다.
    /// 따로 정하지 않았다면 <see cref="Runtime.MemoryLimit"/>를 따릅니다.
    /// 넘으면 <see cref="JyunoException"/>이 발생합니다. (붙잡고 있던 값은 인터프리터를 버려야 GC가 회수합니다)
    /// </summary>
    public long MemoryLimit
    {
        get => memory_limit ?? runtime.MemoryLimit;
        set => memory_limit = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value) , "메모리 한도는 0 이상이어야 합니다. (0은 제한 없음)");
    }
    long? memory_limit;
    /// <summary>마지막으로 잰, 스크립트가 붙잡고 있던 값의 대략적인 크기 (바이트, 1.7.0부터). 메모리 한도가 없다면 재지 않으므로 0입니다.</summary>
    public long MemoryUsage { get; private set; }

    //지금 이 스레드에서 실행중인 인터프리터 (내장 함수처럼 인터프리터를 모르는 코드에서 한도와 취소를 확인하기 위함)
    [ThreadStatic]
    static Interpreter? active;
    //마지막으로 잰 시점의 스레드 할당량, 다음에 잴 할당량
    long memory_baseline, memory_next;
    //재는 도구는 재사용함 (따라간 값을 기록하는 집합이 커지면 큰 객체 힙에 할당되어 GC를 자주 일으키므로)
    MemoryMeter? meter;

    //Run/ExecuteLine/C#에서의 함수 호출이 시작될 때: 이 스레드의 실행중인 인터프리터로 등록 (끝나면 leave로 되돌림)
    Interpreter? enter()
    {
        Interpreter? outer = active;
        if (outer != this)
        {
            active = this;
            //할당량은 스레드마다 따로 세므로, 이 스레드에서 처음 시작할 때 기준을 다시 잡음
            memory_baseline = GC.GetAllocatedBytesForCurrentThread();
            memory_next = memory_baseline + memory_step(MemoryLimit , MemoryUsage);
        }
        return outer;
    }
    static void leave(Interpreter? outer) => active = outer;
    bool memory_limited => memory_limit is long limit ? limit != 0 : runtime.MemoryLimit != 0;

    //C# 스택에만 있어서 영역/호출 정보로는 닿을수 없는 값: 호출한 쪽의 영역과 인자, 지금 줄을 계산하는 도중에 만든 큰 값
    //(호출 한번 또는 줄 하나가 끝나면 그 동안 추가한 것을 지움)
    readonly List<object?> roots = new();
    //이 위치부터는 지금 실행중인 줄을 계산하는 도중에 추가된 것 (다음 줄을 실행할 때 지움)
    int statement_floor;
    //호출이 끝난 뒤: 돌려준 값은 호출한 줄이 끝날 때까지 붙잡음 (함수 안의 지역 변수였다면 영역과 함께 사라졌으므로)
    void hold_result(object? result)
    {
        if (result is string or System.Collections.IList or StructInstance && memory_limited)
            roots.Add(result);
    }
    //줄 하나를 실행하기 전에: 이전 줄을 계산하는 도중에 붙잡은 값을 놓음
    void next_statement()
    {
        if (roots.Count > statement_floor)
            roots.RemoveRange(statement_floor , roots.Count - statement_floor);
    }
    //호출/실행 한번을 시작할 때 (끝나면 release_roots(mark, floor))
    int hold_roots(out int floor)
    {
        floor = statement_floor;
        return roots.Count;
    }
    void release_roots(int mark , int floor)
    {
        if (roots.Count > mark)
            roots.RemoveRange(mark , roots.Count - mark);
        statement_floor = floor;
    }

    //다음에 잴 때까지 할당할수 있는 양: 지난번에 잰 크기의 4배(재는 비용이 할당량에 비례하도록, GC와 같은 방식), 단 한도까지 남은 양을 넘지 않게
    //최소 1MB이므로, 붙잡고 있는 값은 한도를 약 1MB(+ 미리 확인하지 않는 1MB 미만의 값 하나)까지 넘을수 있음
    const long MinStep = 1L << 20;
    const int StepRatio = 4;
    static long memory_step(long limit , long live) => Math.Max(MinStep , Math.Min(live * StepRatio , limit - live));

    //반복문의 다음 반복, 함수 호출 등에서 호출: 지난번에 잰 뒤로 많이 할당했다면 다시 잼 (그렇지 않다면 할당량을 한번 읽는 비용뿐)
    void poll_memory()
    {
        if (memory_limited && GC.GetAllocatedBytesForCurrentThread() >= memory_next)
            measure_memory(0);
    }

    //bytes만큼 새로 만들기 전에 한도를 넘을지 확인 (넘을것 같다면 다시 재서 확인)
    void reserve_memory(long bytes)
    {
        long limit = MemoryLimit;
        if (limit is 0)
            return;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - memory_baseline;
        if (MemoryUsage + allocated + bytes > limit || allocated >= memory_next - memory_baseline)
            measure_memory(bytes);
    }

    void measure_memory(long extra)
    {
        long limit = MemoryLimit;
        long live = (meter ??= new(this)).Measure();
        MemoryUsage = live;
        memory_baseline = GC.GetAllocatedBytesForCurrentThread();
        memory_next = memory_baseline + memory_step(limit , live);
        if (limit > 0 && live + extra > limit)
            throw new JyunoException(extra > 0
                ? $"메모리 한도를 넘었습니다. (한도 {mib(limit)}, 사용중 약 {mib(live)}, 새로 만들 값 약 {mib(extra)})"
                : $"메모리 한도를 넘었습니다. (한도 {mib(limit)}, 사용중 약 {mib(live)})");
    }
    static string mib(long bytes) => (bytes / 1048576.0).ToString("0.#" , System.Globalization.CultureInfo.InvariantCulture) + "MB";

    //─── 내장 함수에서 사용 (지금 이 스레드에서 실행중인 인터프리터의 한도와 취소를 확인) ───

    //이보다 작은 값(문자열이라면 약 50만 자 미만)은 만들기 전에 확인하지 않고 일단 만듦 (반복문과 함수 호출에서의 확인으로 충분)
    const long ReserveThreshold = 1L << 20;
    //이보다 큰 값은 줄이 끝날 때까지 크기를 잴 때 포함 (Hold)
    const long HoldThreshold = 64 * 1024;
    /// <summary>bytes 크기의 값을 만들기 전에 메모리 한도를 넘지 않는지 확인 (작은 값은 확인하지 않음)</summary>
    internal static void Reserve(long bytes)
    {
        if (bytes >= ReserveThreshold && active is Interpreter interpreter)
            interpreter.reserve_memory(bytes);
    }
    /// <summary>Reserve(bytes)한 뒤 만든 값: 지금 줄이 끝날 때까지 크기를 잴 때 포함 (변수에 넣기 전에도 메모리를 차지하므로)</summary>
    internal static T Hold<T>(T value , long bytes)
    {
        if (bytes >= HoldThreshold && active is Interpreter interpreter && interpreter.memory_limited)
            interpreter.roots.Add(value);
        return value;
    }
    //문자열 두개/세개 이어붙이기 (+ 연산자, add): 긴 문자열이라면 만들기 전에 한도를 확인
    internal static string Concat(string a , string b)
    {
        long length = (long)a.Length + b.Length;
        if (length < HoldThreshold / 2)
            return string.Concat(a , b);
        ReserveString(length);
        return Hold(string.Concat(a , b) , length * 2);
    }
    internal static string Concat(string a , string b , string c)
    {
        long length = (long)a.Length + b.Length + c.Length;
        if (length < HoldThreshold / 2)
            return string.Concat(a , b , c);
        ReserveString(length);
        return Hold(string.Concat(a , b , c) , length * 2);
    }
    internal static string Concat(string[] texts)
    {
        long length = 0;
        foreach (string text in texts)
            length += text.Length;
        if (length < HoldThreshold / 2)
            return string.Concat(texts);
        ReserveString(length);
        return Hold(string.Concat(texts) , length * 2);
    }
    //문자열 length글자를 만들기 전에 (만들수 있는 최대 길이를 넘으면 오류)
    internal static void ReserveString(long length)
    {
        if (length > Strings.MaxLength)
            throw new JyunoException($"문자열이 너무 깁니다. ({length.ToString("N0" , System.Globalization.CultureInfo.InvariantCulture)}자, 최대 {Strings.MaxLength.ToString("N0" , System.Globalization.CultureInfo.InvariantCulture)}자)");
        Reserve(length * 2 + 24);
    }
    //리스트에 count개를 추가하기 전에: 용량이 늘어난다면 새 내부 배열의 크기만큼 확인
    internal static void ReserveGrowth(IList list , int count)
    {
        (int capacity, int size) = list switch {
            List<object?> l => (l.Capacity, 8),
            List<long> l => (l.Capacity, 8),
            List<double> l => (l.Capacity, 8),
            List<string> l => (l.Capacity, 8),
            List<bool> l => (l.Capacity, 1),
            _ => (int.MaxValue, 8)
        };
        long need = (long)list.Count + count;
        if (need > capacity)
            Reserve(Math.Max(need , capacity * 2L) * size);
    }
    //원소 count개의 배열/리스트를 만들기 전에 (boxed: 원소마다 박싱된 값이 하나씩 생기는지)
    internal static void ReserveItems(long count , bool boxed = false) => Reserve(count * (boxed ? 32 : 8) + 32);

    //오래 걸릴수 있는 내장 계산(비교, 문자열 변환, 복사, 정렬, 중복 제거 등)의 반복마다 호출: 몇천 번에 한번 취소와 메모리를 확인
    [ThreadStatic]
    static int ticks;
    internal static void Tick()
    {
        if ((++ticks & 0xFFF) == 0 && active is Interpreter interpreter)
            interpreter.interrupt();
    }
    void interrupt()
    {
        check_cancel();
        poll_memory();
    }
    void check_cancel()
    {
        if (CancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("실행이 취소되었습니다." , CancellationToken);
    }

    //─── 붙잡고 있는 값의 크기 재기 ───
    //스크립트가 닿을수 있는 값(영역, 반복문 상태, 호출 인자)에서 시작해서, 영역의 변수, 구조체 값, 배열/리스트의 원소를 따라감
    //재귀 대신 스택을 써서 깊게 중첩된 값도 스택 오버플로가 나지 않음
    sealed class MemoryMeter
    {
        public MemoryMeter(Interpreter interpreter)
        {
            this.interpreter = interpreter;
        }
        readonly Interpreter interpreter;
        //다 재면 비우지만 내부 배열은 남겨서 다음에 다시 씀
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        readonly Stack<object> pending = new();
        long total;

        //64비트 .NET 기준의 어림값
        const long ObjectHeader = 24, ScopeSize = 200, EntrySize = 56, BoxSize = 24;

        //가끔만 실행되므로 단계별 컴파일(처음에는 최적화하지 않음)을 거치지 않고 바로 최적화
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public long Measure()
        {
            total = 0;
            try
            {
                walk();
                return total;
            }
            finally
            {
                //다 쟀다면 값을 붙잡고 있지 않도록 비움 (아주 많았다면 내부 배열도 버림)
                if (seen.Count > 1 << 16)
                    seen = new(ReferenceEqualityComparer.Instance);
                else
                    seen.Clear();
                pending.Clear();
            }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        void walk()
        {
            Interpreter it = interpreter;
            visit(it.top);
            visit(it.current);
            foreach (var state in it.top_for_states.Values)
                visit_for(state);
            foreach (CallFrame frame in it.frames)
            {
                visit_values(frame.Args);
                foreach (var state in frame.ForStates.Values)
                    visit_for(state);
            }
            visit_values(it.inline_args);
            foreach (object? root in it.roots)
            {
                if (root is object?[] args)
                    visit_values(args);
                else
                    visit(root);
            }
            int steps = 0;
            while (pending.TryPop(out object? item))
            {
                if ((++steps & 0xFFFF) == 0)
                    it.check_cancel();
                expand(item);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        void visit_for(ForState state)
        {
            visit(state.Outer);
            total += ObjectHeader + 8L * state.Items.Length;
            visit_values(state.Items);
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        void visit_values(object?[] values)
        {
            foreach (object? value in values)
                visit(value);
        }

        //값 하나를 셈 (안에 다른 값이 있다면 나중에 따라가도록 쌓아둠)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        void visit(object? value)
        {
            switch (value)
            {
                case null:
                    return;
                case string text:
                    //긴 문자열은 같은 문자열을 여러 곳에서 담고 있을수 있으므로 한번만 셈 (짧은 문자열은 세는 비용을 줄이기 위해 매번)
                    if (text.Length >= 256 && !seen.Add(text))
                        return;
                    total += 22 + 2L * text.Length;
                    return;
                case long or double or bool or int:
                    total += BoxSize;
                    return;
                //구조체 값은 영역과 하나씩 짝지어 있으므로 영역으로 셈 (영역을 셀 때 구조체 값의 크기도 더함)
                case StructInstance instance:
                    visit(instance.Scope);
                    return;
                case Scope or IList or JyunoFunction or JyunoStructType:
                    if (seen.Add(value))
                        pending.Push(value);
                    return;
                default:
                    //프로그램이 제공한 객체는 안을 따라가지 않음
                    total += ObjectHeader;
                    return;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        void expand(object item)
        {
            switch (item)
            {
                case Scope scope:
                    total += ScopeSize + EntrySize * (long)scope.Variables.Count;
                    if (scope.Parent is Scope parent)
                        visit(parent);
                    if (scope.Instance is StructInstance owner)
                        total += 64 + 8L * owner.Members.Length;
                    foreach (object? holder in scope.Variables.Values)
                    {
                        switch (holder)
                        {
                            //프로그램이 제공하는 변수(AddVariable)는 읽으면 C# 코드가 실행되므로 읽지 않음
                            case JyunoVariable variable:
                                total += ObjectHeader;
                                visit(variable.Get());
                                break;
                            case JyunoFunction or JyunoStructType:
                                visit(holder);
                                break;
                        }
                    }
                    break;
                case JyunoFunction function:
                    total += 48;
                    visit(function.Scope);
                    break;
                case JyunoStructType struct_type:
                    total += 64;
                    visit(struct_type.Scope);
                    break;
                case Array array:
                    Type element = array.GetType().GetElementType()!;
                    total += ObjectHeader + 8 + element_size(element) * (long)array.Length;
                    if (array is object?[] values)
                    {
                        //string[]도 object?[]로 읽을수 있음 (배열 공변성)
                        foreach (object? value in values)
                            visit(value);
                    }
                    break;
                case IList list:
                    (long capacity, Type? type) = list switch {
                        List<object?> l => (l.Capacity, typeof(object)),
                        List<long> l => (l.Capacity, typeof(long)),
                        List<double> l => (l.Capacity, typeof(double)),
                        List<string> l => (l.Capacity, typeof(string)),
                        List<bool> l => (l.Capacity, typeof(bool)),
                        _ => ((long)list.Count, (Type?)null)
                    };
                    total += 32 + ObjectHeader + 8 + (type is null ? 8 : element_size(type)) * capacity;
                    if (type is null || type == typeof(object) || type == typeof(string))
                    {
                        for (int i = 0 ; i < list.Count ; i++)
                            visit(list[i]);
                    }
                    break;
            }
        }

        static long element_size(Type element) => element == typeof(bool) || element == typeof(byte) ? 1
            : element == typeof(int) || element == typeof(float) ? 4
            : element == typeof(char) || element == typeof(short) ? 2
            : 8;
    }
}
