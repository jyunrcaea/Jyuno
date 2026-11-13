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
//  emitcs <출력 폴더> bench <폴더> | fuzz <jsonl> <개수>   C# 소스로 내보내기 (AotGen 프로젝트에서 빌드)
//  interp-dump <jsonl> <개수> <출력>   인터프리터 결과만 기록 (AOT 빌드와 비교용)
//  phases-build <소스>   빌드 단계별 시간 (분석 / 제한 모드 검사 / 전체)
//  typecheck judge <풀이 폴더> | files <폴더> | fuzz <jsonl> <개수>   정적 타입 검사 통과율과 건전성 (통과한 프로그램이 타입 오류로 멈추는지)
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

    //실행 중 오류의 분류: 타입 검사가 막아야 하는 오류(타입/null/정의)와 값에 따른 오류(0으로 나누기, 인덱스 범위 등)
    static string? RuntimeClass(string? error)
    {
        if (error is null) return null;
        string[] null_like = { "null" , "NullReference" };
        string[] type_like = { "RuntimeBinder" , "InvalidCast" , "정의되지 않" , "알수없는 변수" , "존재하지 않는" , "뺄셈" , "크기를 비교" , "반복할 횟수" , "인덱스는 정수" , "순회할수 없습니다" , "넣을수 없습니다" , "숫자가 아닙니다" , "길이를 구할수 없습니다" , "함수 또는 변수가 아닙니다" , "괄호" , "인자" , "누락" , "더할수" , "곱할" , "나눌수" , "형식 문자열은" , "변환" };
        string[] value_like = { "0으로 나눌수" , "범위" , "호출 깊이" , "너무 많습니다" , "형식" , "goto" };
        if (value_like.Any(error.Contains)) return "값";
        if (null_like.Any(error.Contains)) return "null";
        if (type_like.Any(error.Contains)) return "타입";
        return "값";
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
                JyunoProgram program = JyunoProgramImage.Load(bytes , runtime , engine);
                mark("load");
                program.Run();
                mark("run");
                Console.Write(sink.Buffer);
                break;
            }
            case "emitcs":
            {
                //emitcs <출력 폴더> bench <폴더>  |  emitcs <출력 폴더> fuzz <programs.jsonl> <개수>
                //제한 모드 프로그램을 C# 소스로 내보내고 (Gen/*.cs + Registry.cs), fuzz는 인터프리터 결과도 expected.jsonl로
                string dir = args[1];
                Directory.CreateDirectory(Path.Combine(dir , "Gen"));
                foreach (string old in Directory.GetFiles(Path.Combine(dir , "Gen") , "*.cs")) File.Delete(old);
                List<(string Name, string[] Lines)> sources = new();
                bool fuzz = args[2] == "fuzz";
                if (fuzz)
                {
                    int count = int.Parse(args[4]) , index = 0;
                    foreach (string json in File.ReadLines(args[3]))
                    {
                        if (sources.Count >= count) break;
                        sources.Add(($"F{index++:D5}" , JsonSerializer.Deserialize<string>(json)!.Split('\n')));
                    }
                }
                else
                    foreach (string file in Directory.GetFiles(args[3] , "*.jyuno").OrderBy(o => o))
                        sources.Add(("B_" + Path.GetFileNameWithoutExtension(file) , File.ReadAllText(file).Replace("\r\n" , "\n").TrimEnd('\n').Split('\n')));
                List<string> registry = new();
                using StreamWriter expected = new(Path.Combine(dir , "expected.jsonl"));
                Dictionary<string , int> reasons = new();
                long bytes = 0;
                var sw = Stopwatch.StartNew();
                foreach (var (name , lines) in sources)
                {
                    string? code , reason;
                    try
                    {
                        code = JyunoProgramImage.EmitCSharp(lines , fuzz ? Host("" , out _) : new Runtime(Runtime.AddJyunoCommandType.Math) , "JyunoGen" , name , out reason);
                    }
                    catch (JyunoBuildException)
                    {
                        code = null;
                        reason = "빌드 거부";
                    }
                    if (code is null)
                    {
                        reasons[reason!] = reasons.GetValueOrDefault(reason!) + 1;
                        continue;
                    }
                    bytes += code.Length;
                    File.WriteAllText(Path.Combine(dir , "Gen" , name + ".cs") , code);
                    registry.Add($"        (\"{name}\" , h => new JyunoGen.{name}(h).Run),");
                    if (fuzz)
                    {
                        Outcome o = Interpret(lines , "");
                        expected.WriteLine(JsonSerializer.Serialize(new[] { name , o.Output , o.Result , o.Error ?? "" }));
                    }
                }
                File.WriteAllText(Path.Combine(dir , "Gen" , "Registry.cs") ,
                    "static class Registry\n{\n    public static readonly (string Name, System.Func<Jyuno.Runtime, System.Func<object>> Make)[] All =\n    {\n" + string.Join("\n" , registry) + "\n    };\n}\n");
                Console.WriteLine($"C# {registry.Count}개 ({bytes / 1024.0:F0} KB, {sw.Elapsed.TotalMilliseconds:F0} ms), 못 만듦 {sources.Count - registry.Count}");
                foreach (var (reason , n) in reasons.OrderByDescending(o => o.Value).Take(8))
                    Console.WriteLine($"  {reason} {n}");
                return 0;
            }
            case "interp-dump":
            {
                //interp-dump <programs.jsonl> <개수> <출력.jsonl>: 인터프리터 결과만 (AOT 빌드의 결과와 비교용)
                int count = int.Parse(args[2]) , index = 0;
                using StreamWriter output = new(args[3]);
                foreach (string json in File.ReadLines(args[1]).Take(count))
                {
                    Outcome o = Interpret(JsonSerializer.Deserialize<string>(json)!.Split('\n') , "");
                    output.WriteLine(JsonSerializer.Serialize(new[] { $"F{index++:D5}" , o.Output , o.Result , o.Error ?? "" }));
                }
                return 0;
            }
            case "typecheck":
            {
                //typecheck judge <풀이 폴더> | files <폴더> | fuzz <jsonl> <개수>
                var programs = new List<(string Name, string[] Lines, string[] Inputs)>();
                if (args[1] is "judge")
                {
                    foreach (Problem problem in Problems.All)
                    {
                        string path = Path.Combine(args[2] , $"{problem.Id}.jyuno");
                        if (File.Exists(path))
                            programs.Add((problem.Id.ToString() , File.ReadAllText(path).Replace("\r\n" , "\n").Split('\n') , problem.AllCases.Select(o => o.Input).ToArray()));
                    }
                }
                else if (args[1] is "files")
                {
                    foreach (string path in Directory.GetFiles(args[2] , "*.jyuno").OrderBy(o => o))
                        programs.Add((Path.GetFileNameWithoutExtension(path) , File.ReadAllText(path).Replace("\r\n" , "\n").Split('\n') , new[] { "" }));
                }
                else
                {
                    int index = 0;
                    foreach (string json in File.ReadLines(args[2]).Take(int.Parse(args[3])))
                        programs.Add(($"F{index++:D5}" , JsonSerializer.Deserialize<string>(json)!.Split('\n') , new[] { "" }));
                }
                bool verbose = Environment.GetEnvironmentVariable("TYPECHECK_VERBOSE") is "1";
                foreach (TypeCheckLevel level in new[] { TypeCheckLevel.Strict , TypeCheckLevel.Pragmatic })
                {
                    int accepted = 0 , rejected = 0 , restricted = 0 , runs = 0;
                    var categories = new Dictionary<string , int>();   //거부된 프로그램의 첫 오류 분류
                    var messages = new Dictionary<string , int>();
                    var unsound = new Dictionary<string , int>();      //통과했는데 실행 중 타입/null 오류
                    var rejected_runtime = new Dictionary<string , int>(); //거부된 프로그램이 실제로 실행하면
                    var false_alarm = new Dictionary<string , int>(); //거부했는데 실행하면 타입/null 오류가 없던 프로그램의 첫 오류
                    var warn = 0;
                    double total_ms = 0 , max_ms = 0;
                    var examples = new List<string>();
                    foreach (var (name , lines , inputs) in programs)
                    {
                        TypeCheckResult result;
                        long t = Stopwatch.GetTimestamp();
                        result = JyunoTypeChecker.Check(lines , Host("" , out _) , level , JyunoTypeChecker.JyunoWebHost);
                        if (result.Restricted)
                        {
                            restricted++;
                            continue;
                        }
                        double ms = (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;
                        total_ms += ms;
                        max_ms = Math.Max(max_ms , ms);
                        if (result.Warnings.Count > 0) warn++;
                        string? worst = null;
                        foreach (string input in inputs)
                        {
                            Outcome o = Interpret(lines , input);
                            runs++;
                            string? c = RuntimeClass(o.Error);
                            if (c is "타입" or "null") { worst = c + ": " + o.Error; break; }
                        }
                        if (result.Success)
                        {
                            accepted++;
                            if (worst is not null)
                            {
                                string k = worst.Split(':')[0];
                                unsound[k] = unsound.GetValueOrDefault(k) + 1;
                                if (unsound.Values.Sum() <= 6)
                                    examples.Add($"── 통과했는데 실행 오류 ({level}) {name}\n{string.Join("\n" , lines.Select((l , i) => $"{i + 1,3}| {l}"))}\n  => {worst}");
                            }
                        }
                        else
                        {
                            rejected++;
                            TypeDiagnostic first = result.Errors[0];
                            categories[first.Category] = categories.GetValueOrDefault(first.Category) + 1;
                            string m = System.Text.RegularExpressions.Regex.Replace(first.Message , "'[^']*'" , "'_'");
                            messages[m] = messages.GetValueOrDefault(m) + 1;
                            string k = worst?.Split(':')[0] ?? "오류 없음";
                            rejected_runtime[k] = rejected_runtime.GetValueOrDefault(k) + 1;
                            if (worst is null) false_alarm[$"[{first.Category}] {m}"] = false_alarm.GetValueOrDefault($"[{first.Category}] {m}") + 1;
                            if (verbose) Console.WriteLine($"  {name} [{level}] {string.Join(" / " , result.Errors.Take(3))}");
                        }
                    }
                    int checkedn = accepted + rejected;
                    Console.WriteLine($"[{level}] 프로그램 {programs.Count}개: 제한 모드 거부 {restricted}, 검사 {checkedn} → 통과 {accepted} ({100.0 * accepted / Math.Max(1 , checkedn):F1}%), 거부 {rejected}, 경고 있음 {warn}");
                    Console.WriteLine($"  검사 시간 평균 {total_ms / Math.Max(1 , checkedn):F2} ms, 최대 {max_ms:F1} ms");
                    Console.WriteLine($"  통과했는데 실행 중 오류: {(unsound.Count is 0 ? "없음" : string.Join(", " , unsound.Select(o => $"{o.Key} {o.Value}")))}");
                    Console.WriteLine($"  거부된 프로그램을 실제로 실행하면: {string.Join(", " , rejected_runtime.OrderByDescending(o => o.Value).Select(o => $"{o.Key} {o.Value}"))}");
                    Console.WriteLine($"  거부 분류: {string.Join(", " , categories.OrderByDescending(o => o.Value).Select(o => $"{o.Key} {o.Value}"))}");
                    foreach (var (m , n) in messages.OrderByDescending(o => o.Value).Take(12))
                        Console.WriteLine($"    {n,5}  {m}");
                    Console.WriteLine("  실행하면 문제 없던 거부 (첫 오류):");
                    foreach (var (m , n) in false_alarm.OrderByDescending(o => o.Value).Take(10))
                        Console.WriteLine($"    {n,5}  {m}");
                    foreach (string e in examples.Take(int.Parse(Environment.GetEnvironmentVariable("TYPECHECK_EXAMPLES") ?? "2"))) Console.WriteLine(e);
                }
                return 0;
            }
            case "typeshow":
            {
                //typeshow <파일> [strict|pragmatic]: 한 파일의 검사 결과와 추론한 타입
                string[] lines = File.ReadAllText(args[1]).Replace("\r\n" , "\n").Split('\n');
                TypeCheckLevel level = args.Length > 2 && args[2] is "pragmatic" ? TypeCheckLevel.Pragmatic : TypeCheckLevel.Strict;
                TypeCheckResult r = JyunoTypeChecker.Check(lines , Host("" , out _) , level , JyunoTypeChecker.JyunoWebHost);
                foreach (var d in r.Errors) Console.WriteLine("오류 " + d);
                foreach (var d in r.Warnings) Console.WriteLine("경고 " + d);
                foreach (string t in r.Types) Console.WriteLine("  " + t);
                Console.WriteLine($"인스턴스 {r.Instances}, 반복 {r.Rounds}");
                Console.WriteLine("실행: " + Interpret(lines , args.Length > 3 ? File.ReadAllText(args[3]) : ""));
                return r.Success ? 0 : 1;
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
