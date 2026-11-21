using Jyuno.Compiler;
using Jyuno.Language;
using System.Runtime.ExceptionServices;

namespace Jyuno;

//try ~ catch (이름) ~ tried (1.7.0부터)
//try 본문에서 난 오류(JyunoException)나 throw로 던진 값을 catch 본문에서 처리함
//ttl처럼 try 블록에 들어온 동안만 블록 전용 반복문으로 실행하므로, try가 없는 코드의 실행은 달라지지 않음
//잡지 않는 것: 프로그램이 정한 한도(JyunoLimitException: 메모리 한도, 호출 깊이, 스택), 블록 구조의 문법 오류, 취소(OperationCanceledException),
//ttl의 제한 시간, 프로그램이 추가한 함수가 던진 JyunoException이 아닌 예외
public partial class Interpreter
{
    //catch 본문에서 처리중인 오류 (값 없는 throw가 그대로 다시 던짐)
    Exception? handling;

    //try 줄: try 본문을 실행하고, 오류가 나면 catch 본문을 실행 (tried 다음 줄로 이어서 실행하도록 CurrentExecuteLine은 tried 줄)
    object? run_try(int line , BlockMap map)
    {
        int catch_line = map.Pair[line];
        int end = map.Pair[catch_line];
        //catch/tried 줄의 형식 오류는 try 블록을 시작하기 전에 보고 (오류가 나지 않아도 지나가는 줄이므로)
        if (map.Errors[catch_line] is string catch_error)
            return new GrammarError(catch_line , catch_error);
        if (map.Errors[end] is string end_error)
            return new GrammarError(end , end_error);
        //try 줄을 실행한 영역 (오류가 나면 되돌릴 영역)
        Scope scope = current;
        //지금 실행하는 본문: try 본문(try 줄 다음 ~ catch 줄 전), 오류가 났다면 catch 본문(catch 줄 다음 ~ tried 줄 전)
        int start = line + 1, stop = catch_line;
        bool caught = false;
        Exception? outer = handling;
        try
        {
            for (CurrentExecuteLine = start ; ; CurrentExecuteLine++)
            {
                int next = CurrentExecuteLine;
                //본문이 끝남 (try 본문이 끝났다면 catch 본문은 건너뜀): tried 다음 줄부터 이어서 실행 (Run/함수의 반복문에서 1을 더함)
                if (next == stop)
                {
                    CurrentExecuteLine = end;
                    return null;
                }
                //goto/break로 블록을 빠져나감: 그 줄부터 이어서 실행 (본문 안쪽으로 들어가는 goto는 goto에서 막음)
                if (next < start || next > stop)
                {
                    CurrentExecuteLine = next - 1;
                    return null;
                }
                //ttl 안이라면 줄마다 시간 제한을 확인 (Ttl.cs의 반복문과 같음)
                if (ttl is not null)
                    check_ttl();
                next_statement();
                object? ret;
                try
                {
                    ret = execute_line(next , true);
                }
                catch (JyunoException e) when (!caught && catchable(e))
                {
                    caught = true;
                    //try 본문에서 진행중이던 for/repeat를 정리하고 try 줄의 영역으로 되돌림 (호출한 함수의 상태는 이미 정리됨)
                    leave_block(line , catch_line , scope);
                    handling = e;
                    start = catch_line + 1;
                    stop = end;
                    CurrentExecuteLine = catch_line;
                    bind_catch(catch_line , e is JyunoThrownException thrown ? thrown.Value : e.Message);
                    continue;
                }
                if (ret is ReturnInfo or GrammarError)
                    return ret;
            }
        }
        finally
        {
            handling = outer;
        }
    }

    //catch로 잡을수 있는 오류인지 (예외 필터는 안쪽의 finally보다 먼저 실행되므로, 오류가 난 가장 안쪽의 줄을 여기서 기록함)
    bool catchable(JyunoException e)
    {
        if (e is JyunoLimitException or GrammarErrorException)
            return false;
        if (e.Line < 0)
            e.Line = CurrentExecuteLine;
        return true;
    }

    //블록(first 줄과 last 줄 사이)에서 진행중이던 for/repeat를 정리하고 영역을 되돌림
    void leave_block(int first , int last , Scope scope)
    {
        var fors = for_states;
        if (fors.Count > 0)
        {
            foreach (int for_line in fors.Keys.Where(o => o > first && o < last).ToList())
                fors.Remove(for_line);
        }
        var repeats = repeat_counts;
        if (repeats.Count > 0)
        {
            foreach (int repeat_line in repeats.Keys.Where(o => o > first && o < last).ToList())
                repeats.Remove(repeat_line);
        }
        current = scope;
    }

    //catch (이름): 잡은 값을 local처럼 지금 영역의 변수에 넣음 (이름이 없다면 버림)
    void bind_catch(int catch_line , object? value)
    {
        Token[] tokens = scripts[catch_line].tokens;
        if (tokens.Length < 2)
            return;
        string name = (string)tokens[1].value;
        if (find(current , name , name.Contains('.') , out object? existing))
        {
            if (existing is VariableInterface variable)
                assign(variable , name , value);
            else
                throw new JyunoException($"'{name}'은/는 변수가 아닙니다.");
            return;
        }
        declare_variable(name , value);
    }

    //throw (값): 값을 던짐, throw: 처리중인 오류를 그대로 다시 던짐 (오류가 난 줄과 예외 타입이 그대로)
    object? throw_value(CompiledLine compiled , int line)
    {
        if (get_blocks().Errors[line] is string error)
            return new GrammarError(line , error);
        if (compiled.Tokens.Length is 1)
        {
            if (handling is null)
                throw new JyunoException("다시 던질 오류가 없습니다.");
            ExceptionDispatchInfo.Throw(handling);
        }
        object?[] values = compiled.Arguments();
        if (values.Length is not 1)
            throw new JyunoException("throw 다음에는 던질 값 하나만 와야 합니다.");
        throw new JyunoThrownException(values[0]);
    }
}
