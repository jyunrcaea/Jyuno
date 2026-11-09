using Jyuno;
using Jyuno.Compiler;
using Jyuno.Language;
using JyunoWeb.Engine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

//[프로토타입] 제한 모드 프로그램 검증과 측정
//  fuzz <programs.jsonl>          무작위 프로그램을 인터프리터와 제한 모드로 실행해서 비교
//  judge <풀이 폴더>               JyunoWeb 채점 문제를 두 방식으로 채점
//  speed <폴더> [반복]             벤치마크 (컴파일 포함, 새 런타임마다)
//  build <소스> <출력>             바이너리 만들기 (JyunoWeb 채점 호스트 기준)
//  cold-source <소스> / cold-image <바이너리> / cold-static <소스>   새 프로세스 한번 실행 (단계별 시간을 stderr로)
//  phases-build <소스>   빌드 단계별 시간 (분석 / 제한 모드 검사 / 전체)
static class Program
{
    sealed class Sink : IConsoleSink
    {
        public readonly StringBuilder Buffer = new();
        public void Write(string text)
        {
            Buffer.Append(text);
            if (Buffer.Length > 2_000_000)
                throw new JyunoException("출력이 너무 많습니다.");
        }
        public void Echo(string text) { }
        public void Clear() => Buffer.Clear();
        public void Title(string title) { }
    }

    //JyunoWeb 채점과 같은 호스트 (수학 + 콘솔 + 예시 함수)
    static Runtime Host(string input , out Sink sink)
    {
        sink = new Sink();
        Runtime runtime = new(Runtime.AddJyunoCommandType.Math);
        JyunoHost.AddConsole(runtime , sink , new StringReader(input) , () => { });
        JyunoHost.AddHostFunctions(runtime);
        return runtime;
    }

    record Outcome(string Output , string Result , string? Error);

    static string Show(object? value) => value switch {
        null => "null",
        string s => "\"" + s + "\"",
        System.Collections.IList list => "[" + string.Join(",", list.Cast<object?>().Select(Show)) + "]",
        _ => value.GetType().Name + ":" + Convert.ToString(value , System.Globalization.CultureInfo.InvariantCulture)
    };

    static Outcome Execute(Func<object?> run , Sink sink)
    {
        try
        {
            object? result = run();
            return new(sink.Buffer.ToString() , Show(result) , null);
        }
        catch (Exception e)
        {
            return new(sink.Buffer.ToString() , "-" , e.GetType().Name + ": " + e.Message);
        }
    }

    static Outcome Interpret(string[] lines , string input)
    {
        Runtime runtime = Host(input , out Sink sink);
        Interpreter interpreter = runtime.Create(lines);
        return Execute(() => interpreter.Run() , sink);
    }

    //빌드는 별도 프로필 런타임으로, 실행은 새 호스트 런타임에 불러와서 (바이너리를 거침)
    //JYUNO_ENGINE=Il 이면 fuzz/judge의 제한 모드를 IL 컴파일로 실행 (못하면 클로저 엔진, 개수를 셈)
    static readonly JyunoEngine engine = Enum.Parse<JyunoEngine>(Environment.GetEnvironmentVariable("JYUNO_ENGINE") ?? "Closure");
    static int il_compiled , il_fallback;
    static readonly Dictionary<string , int> fallback_reasons = new();

    static Outcome Static(string[] lines , string input)
    {
        byte[] image = JyunoProgramImage.Build(lines , Host("" , out _));
        Runtime runtime = Host(input , out Sink sink);
        JyunoProgram program = JyunoProgramImage.Load(image , runtime , engine);
        if (engine is not JyunoEngine.Closure)
        {
            if (program.Engine == engine.ToString()) il_compiled++;
            else { il_fallback++; fallback_reasons[program.Engine] = fallback_reasons.GetValueOrDefault(program.Engine) + 1; }
        }
        return Execute(program.Run , sink);
    }

    static int Main(string[] args)
    {
        long t0 = Stopwatch.GetTimestamp();
        List<string> phases = new();
        void mark(string name)
        {
            long now = Stopwatch.GetTimestamp();
            phases.Add($"{name}={(now - t0) * 1000.0 / Stopwatch.Frequency:F3}");
            t0 = now;
        }
        switch (args[0])
        {
            case "fuzz":
            {
                int compared = 0 , skipped = 0 , mismatched = 0;
                Dictionary<string , int> reasons = new();
                foreach (string json in File.ReadLines(args[1]))
                {
                    string code = JsonSerializer.Deserialize<string>(json)!;
                    string[] lines = code.Split('\n');
                    Outcome expected = Interpret(lines , "");
                    Outcome actual;
                    try
                    {
                        actual = Static(lines , "");
                    }
                    catch (JyunoBuildException e)
                    {
                        skipped++;
                        string reason = e.Errors[0].Message;
                        reason = reason.Contains("삭제") ? "삭제" : reason.Contains("내장 함수/상수") ? "내장 함수 재정의" : reason.Contains("goto") ? "goto" : reason;
                        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                        continue;
                    }
                    compared++;
                    if (expected != actual)
                    {
                        mismatched++;
                        if (mismatched <= 5)
                        {
                            Console.WriteLine("──────── 불일치");
                            Console.WriteLine(string.Join("\n" , lines.Select((l , i) => $"{i,3}| {l}")));
                            Console.WriteLine("인터프리터: " + expected);
                            Console.WriteLine("제한 모드:  " + actual);
                        }
                    }
                }
                Console.WriteLine($"비교 {compared}, 일치 {compared - mismatched}, 불일치 {mismatched}, 제한 모드로 빌드 거부 {skipped}");
                if (engine is not JyunoEngine.Closure)
                {
                    Console.WriteLine($"  {engine}: 컴파일 {il_compiled}, 클로저 엔진으로 대신 실행 {il_fallback}");
                    foreach (var (reason , n) in fallback_reasons.OrderByDescending(o => o.Value).Take(8))
                        Console.WriteLine($"    {reason} {n}");
                }
                foreach (var (reason , n) in reasons.OrderByDescending(o => o.Value))
                    Console.WriteLine($"  거부 이유: {reason} {n}");
                return mismatched > 0 ? 1 : 0;
            }
            case "judge":
            {
                int problems = 0 , ac_interp = 0 , ac_static = 0 , cases = 0 , same = 0;
                foreach (Problem problem in Problems.All)
                {
                    string path = Path.Combine(args[1] , $"{problem.Id}.jyuno");
                    if (!File.Exists(path))
                        continue;
                    problems++;
                    string[] lines = File.ReadAllText(path).Replace("\r\n" , "\n").Split('\n');
                    bool ok_i = true , ok_s = true;
                    foreach (TestCase test in problem.AllCases)
                    {
                        cases++;
                        Outcome a = Interpret(lines , test.Input);
                        Outcome b;
                        try { b = Static(lines , test.Input); }
                        catch (JyunoBuildException e) { b = new("" , "-" , "빌드 거부: " + e.Message); }
                        if (a == b) same++;
                        else if (same + 3 > cases) Console.WriteLine($"{problem.Id}: {a} / {b}");
                        ok_i &= a.Error is null && a.Output.TrimEnd() == test.Output.TrimEnd();
                        ok_s &= b.Error is null && b.Output.TrimEnd() == test.Output.TrimEnd();
                    }
                    if (ok_i) ac_interp++;
                    if (ok_s) ac_static++;
                }
                Console.WriteLine($"문제 {problems}개, 케이스 {cases}개: 인터프리터 AC {ac_interp}, 제한 모드 AC {ac_static}, 두 방식의 결과가 같은 케이스 {same}");
                if (engine is not JyunoEngine.Closure)
                {
                    Console.WriteLine($"  {engine}: 컴파일 {il_compiled}, 클로저 엔진으로 대신 실행 {il_fallback}");
                    foreach (var (reason , n) in fallback_reasons.OrderByDescending(o => o.Value).Take(8))
                        Console.WriteLine($"    {reason} {n}");
                }
                return 0;
            }
            case "speed":
            {
                int n = args.Length > 2 ? int.Parse(args[2]) : 20;
                //JYUNO_ENGINE=ExpressionInterpreter: JIT가 없는 런타임(AOT, WASM)에서 식 트리가 해석 실행되는 경우
                JyunoEngine fast = engine is JyunoEngine.Closure ? JyunoEngine.Il : engine;
                foreach (string file in Directory.GetFiles(args[1] , "*.jyuno").OrderBy(o => o))
                {
                    string[] lines = File.ReadAllText(file).Replace("\r\n" , "\n").TrimEnd('\n').Split('\n');
                    byte[] image = JyunoProgramImage.Build(lines , new Runtime(Runtime.AddJyunoCommandType.Math));
                    List<double> ti = new() , ts = new() , tj = new() , tjr = new();
                    object? ri = null , rs = null , rj = null;
                    string used = "";
                    JyunoProgram warm = JyunoProgramImage.Load(image , new Runtime(Runtime.AddJyunoCommandType.Math) , fast);
                    for (int i = 0 ; i < n ; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        ri = new Runtime(Runtime.AddJyunoCommandType.Math).Create(lines).Run();
                        ti.Add(sw.Elapsed.TotalMilliseconds);
                        sw.Restart();
                        rs = JyunoProgramImage.Load(image , new Runtime(Runtime.AddJyunoCommandType.Math)).Run();
                        ts.Add(sw.Elapsed.TotalMilliseconds);
                        sw.Restart();
                        JyunoProgram jit = JyunoProgramImage.Load(image , new Runtime(Runtime.AddJyunoCommandType.Math) , fast);
                        rj = jit.Run();
                        tj.Add(sw.Elapsed.TotalMilliseconds);
                        used = jit.Engine;
                        sw.Restart();
                        warm.Run();
                        tjr.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    double med(List<double> x) => x.Skip(n / 2).OrderBy(o => o).ElementAt((n - n / 2) / 2);
                    bool same = Show(ri) == Show(rs) && Show(ri) == Show(rj);
                    Console.WriteLine($"{Path.GetFileNameWithoutExtension(file),-22} 인터프리터 {med(ti),8:F3}  클로저 {med(ts),8:F3}  IL(컴파일 포함) {med(tj),8:F3}  IL(실행만) {med(tjr),8:F3} ms  [{used}] {(same ? "같음" : $"다름 {Show(ri)} {Show(rs)} {Show(rj)}")}");
                }
                return 0;
            }
            case "corpus":
            {
                //채점 문제 풀이를 첫 예제 입력으로, 같은 프로세스에서 반복 (JIT 이후의 비용)
                var jobs = new List<(string[] Lines, string Input, byte[] Image)>();
                foreach (Problem problem in Problems.All)
                {
                    string path = Path.Combine(args[1] , $"{problem.Id}.jyuno");
                    if (!File.Exists(path)) continue;
                    string[] lines = File.ReadAllText(path).Replace("\r\n" , "\n").Split('\n');
                    jobs.Add((lines , problem.AllCases[0].Input , JyunoProgramImage.Build(lines , Host("" , out _))));
                }
                int n = int.Parse(args[2]);
                double ti = 0 , ts = 0 , tl = 0 , tc = 0 , tj = 0;
                for (int i = 0 ; i < n ; i++)
                {
                    GC.Collect();
                    long a = Stopwatch.GetTimestamp();
                    foreach (var job in jobs) { var rt = Host(job.Input , out _); rt.Create(job.Lines).Run(); }
                    long b = Stopwatch.GetTimestamp();
                    foreach (var job in jobs) { var rt = Host(job.Input , out _); JyunoProgramImage.Load(job.Image , rt).Run(); }
                    long c = Stopwatch.GetTimestamp();
                    foreach (var job in jobs) { var rt = Host(job.Input , out _); JyunoProgramImage.Load(job.Image , rt); }
                    long d = Stopwatch.GetTimestamp();
                    foreach (var job in jobs) { var rt = Host(job.Input , out _); JyunoProgramImage.Compile(job.Lines , rt); }
                    long e = Stopwatch.GetTimestamp();
                    long h0 = Stopwatch.GetTimestamp();
                    foreach (var job in jobs) Host(job.Input , out _);
                    long h1 = Stopwatch.GetTimestamp();
                    long j0 = Stopwatch.GetTimestamp();
                    foreach (var job in jobs) { var rt = Host(job.Input , out _); JyunoProgramImage.Load(job.Image , rt , JyunoEngine.Il).Run(); }
                    long j1 = Stopwatch.GetTimestamp();
                    if (i >= n / 2) { ti += b - a - (h1 - h0); ts += c - b - (h1 - h0); tl += d - c - (h1 - h0); tc += e - d - (h1 - h0); tj += j1 - j0 - (h1 - h0); }
                }
                double k = (n - n / 2) * (double)Stopwatch.Frequency / 1e3;
                int total_lines = jobs.Sum(j => j.Lines.Length);
                Console.WriteLine($"{jobs.Count}개 풀이, {total_lines}줄, 바이너리 {jobs.Sum(j => j.Image.Length)} B (소스 {jobs.Sum(j => j.Lines.Sum(l => l.Length + 1))} B)");
                Console.WriteLine($"한 바퀴: 인터프리터 생성+실행 {ti / k:F3} ms | 제한 모드 불러오기+실행 {ts / k:F3} ms | 불러오기(검증+연결+클로저)만 {tl / k:F3} ms | 소스에서 빌드+연결 {tc / k:F3} ms | IL 불러오기(컴파일)+실행 {tj / k:F3} ms");
                return 0;
            }
            case "build":
            {
                string[] lines = File.ReadAllText(args[1]).Replace("\r\n" , "\n").Split('\n');
                byte[] image = JyunoProgramImage.Build(lines , Host("" , out _));
                File.WriteAllBytes(args[2] , image);
                Console.WriteLine($"{args[2]}: {image.Length} bytes");
                return 0;
            }
            case "cold-source":
            {
                string[] lines = File.ReadAllText(args[1]).Split('\n');
                mark("read");
                Runtime runtime = Host(Console.In.ReadToEnd() , out Sink sink);
                mark("runtime");
                Interpreter interpreter = runtime.Create(lines);
                interpreter.Run();
                mark("run");
                Console.Write(sink.Buffer);
                break;
            }
            case "cold-static":
            {
                string[] lines = File.ReadAllText(args[1]).Split('\n');
                mark("read");
                Runtime runtime = Host(Console.In.ReadToEnd() , out Sink sink);
                mark("runtime");
                JyunoProgram program = JyunoProgramImage.Compile(lines , runtime);
                mark("load");
                program.Run();
                mark("run");
                Console.Write(sink.Buffer);
                break;
            }
            case "cold-image":
            {
                byte[] bytes = File.ReadAllBytes(args[1]);
                mark("read");
                Runtime runtime = Host(Console.In.ReadToEnd() , out Sink sink);
                mark("runtime");
                JyunoProgram program = JyunoProgramImage.Load(bytes , runtime);
                mark("load");
                program.Run();
                mark("run");
                Console.Write(sink.Buffer);
                break;
            }
            case "noop":
                return 0;
            case "jitdump":
            {
                //[실험] IL 백엔드가 만든 식 트리 출력
                string[] lines = File.ReadAllText(args[1]).Replace("\r\n" , "\n").TrimEnd('\n').Split('\n');
                byte[] image = JyunoProgramImage.Build(lines , new Runtime(Runtime.AddJyunoCommandType.Math));
                JyunoProgram p = JyunoProgramImage.Load(image , new Runtime(Runtime.AddJyunoCommandType.Math) , JyunoEngine.Il);
                Console.WriteLine(p.Engine);
                var jit = typeof(Interpreter).GetNestedType("StaticJit" , System.Reflection.BindingFlags.NonPublic)!;
                var lambda = (System.Linq.Expressions.LambdaExpression?)jit.GetField("LastLambda" , System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null);
                Console.WriteLine(typeof(System.Linq.Expressions.Expression).GetProperty("DebugView" , System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(lambda));
                return 0;
            }
            case "phases-build":
            {
                //빌드 단계별 시간 (분석 / 제한 모드 검사 / IR 빌드 전체), 반복 측정
                string[] lines = File.ReadAllText(args[1]).Split('\n');
                Runtime runtime = Host("" , out _);
                var globals = runtime.GetGlobals();
                for (int round = 0 ; round < 12 ; round++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var analysis = Jyuno.Analysis.ScriptAnalysis.Analyze(lines);
                    double a = sw.Elapsed.TotalMilliseconds;
                    analysis.CheckNativeJs(globals);
                    double c = sw.Elapsed.TotalMilliseconds - a;
                    sw.Restart();
                    JyunoProgramImage.Build(lines , runtime);
                    double b = sw.Elapsed.TotalMilliseconds;
                    Console.WriteLine($"analyze={a:F2} checkNativeJs={c:F2} build(all)={b:F2}");
                }
                return 0;
            }
        }
        Console.Error.WriteLine("PHASES " + string.Join(" " , phases));
        return 0;
    }
}
