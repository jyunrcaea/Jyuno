using Jyuno.Language;
using System.Diagnostics;

namespace Jyuno;

//시간 제한 블록 (1.7.0부터)
//ttl (초) ~ endttl 사이를 실행한 시간이 제한을 넘으면, 실행중이던 줄이 끝난 뒤 endttl 다음 줄로 빠져나감
//평소의 실행(Run, 함수 본문)은 줄마다 시간을 확인하지 않으며, ttl 블록에 들어온 동안에만 시간을 확인하는 다른 반복문으로 실행함
//(ttl 밖의 코드는 함수를 호출할 때 ttl이 진행중인지 한번 확인하는 것 외에는 달라지지 않음)
//ttl 안에서 호출한 함수의 본문도 시간을 확인하며, 제한이 지나면 함수 호출을 거슬러 올라가서 ttl 다음 줄로 빠져나감
//줄마다 정밀한 시계(Stopwatch, 약 30ns)를 읽으면 짧은 줄은 실행 시간이 약 1.6배로 늘어나므로,
//제한 시각에 가까워질 때까지는 값싼 시계(Environment.TickCount64, 약 7ns, 1~16ms 단위)만 읽고 마지막 GateMargin 동안만 정밀한 시계로 확인함
public partial class Interpreter
{
    //진행중인 ttl 블록 하나 (ttl 줄을 실행할때 만들고, 블록을 빠져나가면 사라짐)
    sealed class TtlState
    {
        public TtlState(int line , int end , long deadline , long gate , TtlState? outer , Scope scope)
        {
            Line = line;
            End = end;
            Deadline = deadline;
            Outer = outer;
            Scope = scope;
            //바깥 ttl의 제한이 먼저 지나면 이 블록도 함께 빠져나가므로, 둘 중 빠른 시각에 확인
            Effective = outer is null ? deadline : Math.Min(deadline , outer.Effective);
            Gate = outer is null ? gate : Math.Min(gate , outer.Gate);
        }
        //ttl 줄과 endttl 줄
        public readonly int Line, End;
        //이 블록의 제한 시각 (Stopwatch.GetTimestamp 기준)
        public readonly long Deadline;
        //이 블록과 바깥 블록들 중 가장 빠른 제한 시각
        public readonly long Effective;
        //Environment.TickCount64가 이 값 이상이 되면 정밀한 시계로 Effective를 확인함 (그 전에는 제한 시각까지 GateMargin 넘게 남음)
        public readonly long Gate;
        //줄마다 확인: 제한 시각이 지났는지
        public bool Expired => Environment.TickCount64 >= Gate && Stopwatch.GetTimestamp() >= Effective;
        //바깥에서 진행중인 ttl (호출한 함수의 ttl 포함)
        public readonly TtlState? Outer;
        //ttl 줄을 실행한 영역 (빠져나갈때 되돌릴 영역)
        public readonly Scope Scope;
    }
    //가장 안쪽에서 진행중인 ttl (없다면 null)
    TtlState? ttl;
    //값싼 시계(TickCount64)만 확인하다가 정밀한 시계로 바꾸는 시점: 제한 시각의 이만큼(ms) 전
    //값싼 시계의 단위(Windows 약 15.6ms, Linux 1~4ms)보다 커야 제한 시각을 놓치지 않음
    const long GateMargin = 32;

    //제한 시간이 지난 ttl을 빠져나가기 위해 그 ttl 줄을 실행하는 곳까지 거슬러 올라감
    //스크립트의 오류가 아니므로 JyunoException이 아니며, 오류를 잡는 곳(Run/ExecuteLine의 LastErrorLine 등)에서도 오류로 다루지 않음
    sealed class TtlExpiredException : Exception
    {
        public TtlExpiredException(TtlState state) : base("ttl의 제한 시간이 지났습니다.")
        {
            State = state;
        }
        public readonly TtlState State;
    }

    //ttl (초): 제한 시간을 정하고 ttl과 endttl 사이를 시간을 확인하며 실행
    object? run_ttl(CompiledLine compiled , int line , BlockMap map)
    {
        int end = map.Pair[line];
        object?[] args = compiled.Arguments();
        if (args.Length is 0)
            throw new JyunoException("ttl 다음에는 제한 시간(초)이 와야 합니다.");
        if (args.Length > 1)
            throw new JyunoException("ttl 다음에는 제한 시간(초) 하나만 와야 합니다.");
        if (!Values.TryGetReal(args[0] , out double seconds) || double.IsNaN(seconds))
            throw new JyunoException($"ttl의 제한 시간은 숫자(초)여야 합니다. ('{Values.Show(args[0])}')");
        long now = Stopwatch.GetTimestamp();
        long coarse = Environment.TickCount64;
        long deadline, gate;
        if (seconds <= 0)
        {
            deadline = now;
            gate = coarse;
        }
        else
        {
            //너무 큰 값(무한대 포함)은 제한이 없는 것과 같음
            double ticks = seconds * Stopwatch.Frequency;
            deadline = ticks >= (double)(long.MaxValue - now) ? long.MaxValue : now + (long)ticks;
            double ms = seconds * 1000 - GateMargin;
            gate = ms <= 0 ? coarse : ms >= (double)(long.MaxValue - coarse) ? long.MaxValue : coarse + (long)ms;
        }
        TtlState state = new(line , end , deadline , gate , ttl , current);
        ttl = state;
        try
        {
            for (CurrentExecuteLine = line + 1 ; ; CurrentExecuteLine++)
            {
                int next = CurrentExecuteLine;
                //endttl에 도달함: endttl 다음 줄부터 이어서 실행 (Run/함수의 반복문에서 1을 더함)
                if (next == end)
                    return null;
                //goto/break로 블록을 빠져나감: 그 줄부터 이어서 실행
                if (next <= line || next > end)
                {
                    CurrentExecuteLine = next - 1;
                    return null;
                }
                if (state.Expired)
                {
                    TtlState expired = expired_ttl();
                    if (expired != state)
                        throw new TtlExpiredException(expired);
                    leave_ttl(state);
                    return null;
                }
                object? ret = execute_line(next , true);
                if (ret is ReturnInfo or GrammarError)
                    return ret;
            }
        }
        catch (TtlExpiredException e) when (e.State == state)
        {
            //ttl 안에서 호출한 함수에서 제한 시간이 지남 (함수 호출은 이미 정리됨)
            leave_ttl(state);
            return null;
        }
        finally
        {
            ttl = state.Outer;
        }
    }

    //제한 시간이 지난 ttl 중 가장 바깥의 것 (그 ttl을 빠져나가면 안쪽의 ttl도 모두 빠져나감)
    TtlState expired_ttl()
    {
        long now = Stopwatch.GetTimestamp();
        TtlState target = ttl!;
        for (TtlState? state = ttl ; state is not null ; state = state.Outer)
        {
            if (now >= state.Deadline)
                target = state;
        }
        return target;
    }

    //ttl 안에서 호출한 함수의 줄, 인라인된 호출: 제한 시간이 지났다면 그 ttl까지 거슬러 올라감
    void check_ttl()
    {
        if (ttl is TtlState state && state.Expired)
            throw new TtlExpiredException(expired_ttl());
    }

    //시간 제한이 지나서 ttl 블록을 빠져나감: 블록 안에서 진행중이던 for/repeat를 정리하고 endttl 다음 줄로 이동
    void leave_ttl(TtlState state)
    {
        var fors = for_states;
        if (fors.Count > 0)
        {
            foreach (int for_line in fors.Keys.Where(o => o > state.Line && o < state.End).ToList())
                fors.Remove(for_line);
        }
        var repeats = repeat_counts;
        if (repeats.Count > 0)
        {
            foreach (int repeat_line in repeats.Keys.Where(o => o > state.Line && o < state.End).ToList())
                repeats.Remove(repeat_line);
        }
        current = state.Scope;
        CurrentExecuteLine = state.End;
    }

    //ttl 안에서 호출한 함수의 본문: call_function의 반복문과 같지만 줄을 실행하기 전마다 시간 제한을 확인 (ttl 밖에서는 이 반복문을 쓰지 않음)
    object? call_watched(int line , int end)
    {
        for (CurrentExecuteLine = line + 1 ; CurrentExecuteLine < end ; CurrentExecuteLine++)
        {
            check_ttl();
            var ret = execute_line(CurrentExecuteLine , true);
            if (ret is ReturnInfo ri)
                return ri.value;
            if (ret is GrammarError error)
                throw new GrammarErrorException(error);
        }
        return null;
    }

    //ttl 안에서 C# 함수가 다시 호출한 Run(): Run의 반복문과 같지만 줄을 실행하기 전마다 시간 제한을 확인
    object? run_top_watched(int start , int end)
    {
        for (CurrentExecuteLine = start ; CurrentExecuteLine < end ; CurrentExecuteLine++)
        {
            check_ttl();
            var ret = execute_line(CurrentExecuteLine , true);
            if (ret is ReturnInfo ri)
            {
                escape(ri.value);
                return ri.value;
            }
            if (ret is GrammarError)
                return ret;
        }
        return null;
    }
}
