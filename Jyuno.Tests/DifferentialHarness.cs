using Jyuno.Language;
using System.Collections;
using System.Globalization;
using System.Text;

namespace Jyuno.Tests;

//차등 테스트용 실행기: 스크립트를 탈출 분석/인라이닝 설정대로 실행하고, 출력/반환값/오류/마지막 상태를 문자열 하나로 돌려줌
//(설정만 다르게 실행한 결과가 글자 하나까지 같아야 함)
//
//파일 맨 앞의 지시문 (#!로 시작하는 줄, 스크립트에서는 제외):
//  #!dump a b c   실행이 끝난 뒤 ExecuteLine()으로 읽어서 비교할 이름
//  #!opt blocksub noremove   BlockSubstitute = true, EnableRemoveVariable = false
//  #!chunk        대화형 프롬프트처럼 여기까지를 실행한 뒤, 다음 줄들을 scripts에 추가해서 Run(시작 줄)
//  #!again        끝난 뒤 Run()을 한번 더 실행
//
//스크립트가 쓸수 있는 C# 함수/변수:
//  host.keep v / host.get [i]   받은 값을 보관했다가 돌려줌 (C#으로 빠져나간 값)
//  host.eval "줄" / host.evalline n   실행 도중에 ExecuteLine()을 호출
//  host.var                     값을 그대로 보관하는 C# 변수
//  host.settrap "줄" / host.trap / trapvar   읽을 때마다 미리 정한 줄을 실행하는 C# 변수 (변수 읽기의 부수 효과)
//  host.alist / host.olist      C#이 만든 컬렉션 (ArrayList는 구조체를 복사할 때 복사되지 않음)
//  host.show a b ...            값들을 필드까지 풀어서 문자열로
//  host.poke v "필드"           받은 구조체 값의 리스트 필드를 C#에서 바꿈
//  host.opt "escape"|"inline" 0|1   실행 도중에 최적화를 끄고 켬 (이 실행에서 켠 것만)
//  host.cur, host.nan, host.throw
internal static class DifferentialHarness
{
    public static string Format(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case string text: return text;
            case StructValue structure:
            {
                StringBuilder builder = new(structure.TypeName + "{");
                foreach (string field in structure.FieldNames)
                {
                    structure.TryGetField(field , out object? x);
                    builder.Append(field).Append('=').Append(Format(x)).Append(';');
                }
                return builder.Append('}').ToString();
            }
            case IList list:
            {
                StringBuilder builder = new(list is Array ? "A[" : list.GetType().IsGenericType ? "L[" : "H[");
                foreach (object? x in list)
                    builder.Append(Format(x)).Append(',');
                return builder.Append(']').ToString();
            }
            default: return Convert.ToString(value , CultureInfo.InvariantCulture) ?? "";
        }
    }

    public static string Run(string[] raw , bool escape , bool inline)
    {
        List<string> dump = new();
        bool blocksub = false, noremove = false, again = false;
        List<List<string>> chunks = new() { new() };
        foreach (string line in raw)
        {
            if (line.StartsWith("#!"))
            {
                string[] parts = line[2..].Split(' ' , StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length is 0)
                    continue;
                switch (parts[0])
                {
                    case "dump": dump.AddRange(parts[1..]); break;
                    case "opt": blocksub |= parts.Contains("blocksub"); noremove |= parts.Contains("noremove"); break;
                    case "chunk": chunks.Add(new()); break;
                    case "again": again = true; break;
                }
                continue;
            }
            chunks[^1].Add(line);
        }
        //콘솔 출력은 Console을 바꾸지 않고 직접 모음 (다른 테스트와 동시에 실행될수 있음)
        StringBuilder console = new();
        StringBuilder result = new();
        try
        {
            Runtime runtime = new(Runtime.AddJyunoCommandType.Essential | Runtime.AddJyunoCommandType.Math);
            runtime.AddFunction("console.write" , a => { console.Append(a.Length is 0 ? "" : a.Length is 1 ? Format(a[0]) : string.Format(CultureInfo.InvariantCulture , (string?)a[0] ?? "null" , a)); return null; });
            runtime.AddFunction("console.writeline" , a => { console.Append(a.Length is 0 ? "" : a.Length is 1 ? Format(a[0]) : string.Format(CultureInfo.InvariantCulture , (string)a[0]! , a)).Append('\n'); return null; });
            Queue<string> input = new(new[] { "7" , "3" , "5" , "2" , "9" , "4" });
            runtime.AddFunction("console.readline" , a => input.Count > 0 ? input.Dequeue() : null);
            List<object?> kept = new();
            object? host_var = null;
            string? trap_line = null;
            int trap_count = 0;
            runtime.AddFunction("host.keep" , a => { kept.Add(a.Length > 0 ? a[0] : null); return a.Length > 0 ? a[0] : null; });
            runtime.AddFunction("host.get" , a => kept.Count > 0 ? kept[a.Length > 0 && a[0] is long n ? (int)(((n % kept.Count) + kept.Count) % kept.Count) : ^1] : null);
            runtime.AddFunction("host.line" , a => null);
            using Interpreter interpreter = runtime.Create(chunks[0].ToArray());
            interpreter.EscapeAnalysis = escape;
            interpreter.Inlining = inline;
            interpreter.BlockSubstitute = blocksub;
            interpreter.EnableRemoveVariable = !noremove;
            runtime.AddFunction("host.cur" , a => (long)interpreter.CurrentExecuteLine);
            runtime.AddFunction("host.eval" , a => interpreter.ExecuteLine((string)a[0]!));
            runtime.AddFunction("host.evalline" , a => interpreter.ExecuteLine((int)(long)a[0]!));
            runtime.AddFunction("host.nan" , a => double.NaN);
            runtime.AddVariable("host.var" , () => host_var , v => host_var = v);
            runtime.AddFunction("host.settrap" , a => { trap_line = a.Length > 0 ? a[0] as string : null; return null; });
            runtime.AddVariable("host.trap" , () => { trap_count++; return trap_line is null ? (object?)(long)trap_count : interpreter.ExecuteLine(trap_line); } , v => { });
            //이름에 '.'이 없는 같은 변수 (파라미터 이름과 겹칠수 있음)
            runtime.AddVariable("trapvar" , () => { trap_count++; return trap_line is null ? (object?)(long)trap_count : interpreter.ExecuteLine(trap_line); } , v => { });
            runtime.AddFunction("host.alist" , a => new ArrayList(a));
            runtime.AddFunction("host.olist" , a => new List<object?>(a));
            runtime.AddFunction("host.show" , a => string.Join("|" , a.Select(Format)));
            runtime.AddFunction("host.poke" , a => {
                if (a.Length < 2 || a[0] is not StructValue structure || a[1] is not string field || !structure.TryGetField(field , out object? x) || x is not IList list)
                    return false;
                if (!list.IsFixedSize)
                    list.Add(777L);
                else if (list.Count > 0)
                    list[0] = 777L;
                else
                    return false;
                return true;
            });
            runtime.AddFunction("host.opt" , a => {
                bool on = a.Length > 1 && Compiler.Parser.IsTrue(a[1]);
                if (a[0] as string == "escape")
                    interpreter.EscapeAnalysis = escape && on;
                else
                    interpreter.Inlining = inline && on;
                return null;
            });
            runtime.AddFunction("host.throw" , a => throw new InvalidOperationException("host.throw " + (a.Length > 0 ? Format(a[0]) : "")));
            int start = 0;
            for (int c = 0 ; c < chunks.Count ; c++)
            {
                if (c > 0)
                {
                    start = interpreter.scripts.Count;
                    foreach (string line in chunks[c])
                        interpreter.scripts.Add(new CommandLine(line));
                }
                try
                {
                    result.Append("ok|" + Format(interpreter.Run(start)) + "\n");
                }
                catch (Exception e)
                {
                    result.Append("error|" + e.GetType().Name + "|" + e.Message + "\n");
                }
            }
            if (again)
            {
                try
                {
                    result.Append("again|" + Format(interpreter.Run()) + "\n");
                }
                catch (Exception e)
                {
                    result.Append("again-error|" + e.GetType().Name + "|" + e.Message + "\n");
                }
            }
            trap_line = null;
            foreach (string name in dump)
            {
                try
                {
                    result.Append(name + "=" + Format(interpreter.ExecuteLine(name)) + "\n");
                }
                catch (Exception e)
                {
                    result.Append(name + "!" + e.GetType().Name + "|" + e.Message + "\n");
                }
            }
            result.Append("kept=" + Format(kept) + " var=" + Format(host_var) + "\n");
        }
        catch (Exception e)
        {
            result.Append("fatal|" + e.GetType().Name + "|" + e.Message + "\n");
        }
        return result + "---\n" + console;
    }

    public static string[] ReadLines(string path) => File.ReadAllText(path).Replace("\r\n" , "\n").TrimEnd('\n').Split('\n');
}
