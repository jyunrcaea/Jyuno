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
    static Outcome Static(string[] lines , string input)
    {
        byte[] image = JyunoProgramImage.Build(lines , Host("" , out _));
        Runtime runtime = Host(input , out Sink sink);
        JyunoProgram program = JyunoProgramImage.Load(image , runtime);
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
                return 0;
            }
            case "speed":
            {
                int n = args.Length > 2 ? int.Parse(args[2]) : 20;
                foreach (string file in Directory.GetFiles(args[1] , "*.jyuno").OrderBy(o => o))
                {
                    string[] lines = File.ReadAllText(file).Replace("\r\n" , "\n").TrimEnd('\n').Split('\n');
                    byte[] image = JyunoProgramImage.Build(lines , new Runtime(Runtime.AddJyunoCommandType.Math));
                    List<double> ti = new() , ts = new() , tl = new();
                    object? ri = null , rs = null;
                    for (int i = 0 ; i < n ; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        ri = new Runtime(Runtime.AddJyunoCommandType.Math).Create(lines).Run();
                        ti.Add(sw.Elapsed.TotalMilliseconds);
                        sw.Restart();
                        rs = JyunoProgramImage.Load(image , new Runtime(Runtime.AddJyunoCommandType.Math)).Run();
                        ts.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    double med(List<double> x) => x.Skip(n / 2).OrderBy(o => o).ElementAt((n - n / 2) / 2);
                    Console.WriteLine($"{Path.GetFileNameWithoutExtension(file),-22} 인터프리터 {med(ti),8:F2} ms   제한 모드 {med(ts),8:F2} ms   {med(ti) / med(ts),5:F2}x   결과 {(Show(ri) == Show(rs) ? "같음" : $"다름 {Show(ri)} {Show(rs)}")}");
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
                double ti = 0 , ts = 0 , tl = 0 , tc = 0;
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
                    if (i >= n / 2) { ti += b - a - (h1 - h0); ts += c - b - (h1 - h0); tl += d - c - (h1 - h0); tc += e - d - (h1 - h0); }
                }
                double k = (n - n / 2) * (double)Stopwatch.Frequency / 1e3;
                int total_lines = jobs.Sum(j => j.Lines.Length);
                Console.WriteLine($"{jobs.Count}개 풀이, {total_lines}줄, 바이너리 {jobs.Sum(j => j.Image.Length)} B (소스 {jobs.Sum(j => j.Lines.Sum(l => l.Length + 1))} B)");
                Console.WriteLine($"한 바퀴: 인터프리터 생성+실행 {ti / k:F3} ms | 제한 모드 불러오기+실행 {ts / k:F3} ms | 불러오기(검증+연결+클로저)만 {tl / k:F3} ms | 소스에서 빌드+연결 {tc / k:F3} ms");
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
        }
        Console.Error.WriteLine("PHASES " + string.Join(" " , phases));
        return 0;
    }
}
