using Jyuno;
using Jyuno.Compiler;
using System.Diagnostics;

//사용법
//  build <소스.jyuno> <출력.jyc> [--strip]   바이너리 만들기
//  source <소스.jyuno>                        소스에서 실행 (단계별 시간을 stderr로)
//  image <파일.jyc>                           바이너리에서 실행 (단계별 시간을 stderr로)
//  noop                                       아무것도 하지 않음 (프로세스/런타임 시작 비용 측정)
//  warm <소스.jyuno> <횟수>                   같은 프로세스에서 반복 (JIT 이후의 순수 비용)
//  --prepare: 실행 전에 모든 줄을 컴파일 (컴파일 시간을 따로 보기 위함)
static class Program
{
    static long t0;
    static readonly List<(string, double)> phases = new();
    static void Mark(string name)
    {
        long now = Stopwatch.GetTimestamp();
        phases.Add((name , (now - t0) * 1000.0 / Stopwatch.Frequency));
        t0 = now;
    }
    static void Report()
    {
        //프로세스 시작부터 Main까지 (대략)
        double boot = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds - phases.Sum(p => p.Item2);
        Console.Error.WriteLine("PHASES " + string.Join(" " , phases.Select(p => $"{p.Item1}={p.Item2:F3}")));
    }

    static int Main(string[] args)
    {
        t0 = Stopwatch.GetTimestamp();
        bool prepare = args.Contains("--prepare");
        switch (args[0])
        {
            case "noop":
                return 0;
            case "build":
            {
                string[] src = File.ReadAllLines(args[1]);
                byte[] image = JyunoImage.Build(src , new JyunoImageBuildOptions { IncludeSource = !args.Contains("--strip") });
                File.WriteAllBytes(args[2] , image);
                Console.WriteLine($"{args[2]}: {image.Length} bytes ({src.Length} lines, source {new FileInfo(args[1]).Length} bytes)");
                return 0;
            }
            case "source":
            {
                string[] src = File.ReadAllLines(args[1]);
                Mark("read");
                Runtime runtime = new();
                Mark("runtime");
                Interpreter it = runtime.Create(src);
                it.Tokenize();
                Mark("load");
                if (prepare) { it.Prepare(); Mark("compile"); }
                object? ret = it.Run();
                Mark("run");
                Report();
                return ret is Jyuno.Language.GrammarError ? 1 : 0;
            }
            case "image":
            {
                byte[] bytes = File.ReadAllBytes(args[1]);
                Mark("read");
                Runtime runtime = new();
                Mark("runtime");
                Interpreter it = runtime.Load(bytes);
                Mark("load");
                if (prepare) { it.Prepare(); Mark("compile"); }
                object? ret = it.Run();
                Mark("run");
                Report();
                return ret is Jyuno.Language.GrammarError ? 1 : 0;
            }
            case "corpus":
            {
                //여러 스크립트를 같은 프로세스에서 반복해서 불러옴 (JIT 이후의 순수 비용, 플러그인을 많이 불러오는 호스트)
                var files = Directory.GetFiles(args[1] , "*.jyuno").OrderBy(f => f).Select(File.ReadAllLines).ToArray();
                var images = files.Select(f => JyunoImage.Build(f)).ToArray();
                int n = int.Parse(args[2]);
                int lines = files.Sum(f => f.Length);
                Runtime runtime = new();
                double tok = 0 , comp = 0 , load = 0 , comp2 = 0;
                for (int i = 0 ; i < n ; i++)
                {
                    GC.Collect();
                    long a = Stopwatch.GetTimestamp();
                    var its = files.Select(f => { var it = runtime.Create(f); it.Tokenize(); return it; }).ToArray();
                    long b = Stopwatch.GetTimestamp();
                    foreach (var it in its) it.Prepare();
                    long c = Stopwatch.GetTimestamp();
                    GC.Collect();
                    long d = Stopwatch.GetTimestamp();
                    var its2 = images.Select(img => runtime.Load(img)).ToArray();
                    long e = Stopwatch.GetTimestamp();
                    foreach (var it in its2) it.Prepare();
                    long f2 = Stopwatch.GetTimestamp();
                    if (i >= n / 2) { tok += b - a; comp += c - b; load += e - d; comp2 += f2 - e; }
                    foreach (var it in its) it.Dispose();
                    foreach (var it in its2) it.Dispose();
                }
                double k = (n - n / 2) * (double)Stopwatch.Frequency / 1e6;
                Console.WriteLine($"files={files.Length} lines={lines} bytes={files.Sum(f => f.Sum(l => l.Length + 1))} image-bytes={images.Sum(i => i.Length)}");
                Console.WriteLine($"per pass: tokenize={tok / k:F1}us compile={comp / k:F1}us | image-load(verify)={load / k:F1}us compile={comp2 / k:F1}us");
                Console.WriteLine($"per line: tokenize={tok / k / lines * 1000:F0}ns compile={comp / k / lines * 1000:F0}ns image-load={load / k / lines * 1000:F0}ns");
                return 0;
            }
            case "warm":
            {
                string[] src = File.ReadAllLines(args[1]);
                byte[] bytes = JyunoImage.Build(src);
                int n = int.Parse(args[2]);
                Runtime runtime = new();
                double tok = 0 , comp = 0 , load = 0 , build = 0;
                for (int i = 0 ; i < n ; i++)
                {
                    long a = Stopwatch.GetTimestamp();
                    Interpreter it = runtime.Create(src);
                    it.Tokenize();
                    long b = Stopwatch.GetTimestamp();
                    it.Prepare();
                    long c = Stopwatch.GetTimestamp();
                    Interpreter it2 = runtime.Load(bytes);
                    long d = Stopwatch.GetTimestamp();
                    JyunoImage.Build(src);
                    long e = Stopwatch.GetTimestamp();
                    if (i >= n / 2) { tok += b - a; comp += c - b; load += d - c; build += e - d; }
                    it.Dispose(); it2.Dispose();
                }
                double k = (n - n / 2) * (double)Stopwatch.Frequency / 1e6;
                Console.WriteLine($"lines={src.Length} tokenize={tok / k:F2}us compile={comp / k:F2}us image-load={load / k:F2}us build={build / k:F2}us");
                return 0;
            }
        }
        return 2;
    }
}
