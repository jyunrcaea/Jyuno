using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jyuno;
using JyunoWeb.Engine;

//생성된 C# 프로그램 실행/측정
//  bench [반복]        각 프로그램: 연결(Bind)+실행, 실행만 (중앙값 ms)
//  fuzz <출력.jsonl>   JyunoWeb 호스트로 실행한 결과 (expected.jsonl과 비교)
//  cold <이름>         한번 연결+실행하고 결과 출력 (프로세스 시작 시간 측정용)
static class Program
{
    sealed class Sink : IConsoleSink
    {
        public readonly StringBuilder Buffer = new();
        public void Write(string text) { Buffer.Append(text); if (Buffer.Length > 2_000_000) throw new JyunoException("출력이 너무 많습니다."); }
        public void Echo(string text) { }
        public void Clear() => Buffer.Clear();
        public void Title(string title) { }
    }
    static Runtime Host(string input , out Sink sink)
    {
        sink = new Sink();
        Runtime runtime = new(Runtime.AddJyunoCommandType.Math);
        JyunoHost.AddConsole(runtime , sink , new StringReader(input) , () => { });
        JyunoHost.AddHostFunctions(runtime);
        return runtime;
    }
    static string Show(object value) => value switch {
        null => "null",
        string s => "\"" + s + "\"",
        System.Collections.IList list => "[" + string.Join("," , list.Cast<object>().Select(Show)) + "]",
        _ => value.GetType().Name + ":" + Convert.ToString(value , System.Globalization.CultureInfo.InvariantCulture)
    };

    static int Main(string[] args)
    {
        switch (args[0])
        {
            case "bench":
            {
                int n = args.Length > 1 ? int.Parse(args[1]) : 20;
                foreach (var (name , make) in Registry.All)
                {
                    List<double> tl = new() , tr = new();
                    object r = null;
                    Func<object> warm = make(new Runtime(Runtime.AddJyunoCommandType.Math));
                    for (int i = 0 ; i < n ; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        r = make(new Runtime(Runtime.AddJyunoCommandType.Math))();
                        tl.Add(sw.Elapsed.TotalMilliseconds);
                        sw.Restart();
                        warm();
                        tr.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    double med(List<double> x) => x.Skip(n / 2).OrderBy(o => o).ElementAt((n - n / 2) / 2);
                    Console.WriteLine($"{name,-24} 연결+실행 {med(tl),8:F3}  실행만 {med(tr),8:F3} ms  {Show(r)}");
                }
                return 0;
            }
            case "fuzz":
            {
                using StreamWriter output = new(args[1]);
                foreach (var (name , make) in Registry.All)
                {
                    Runtime runtime = Host("" , out Sink sink);
                    string result , error = "";
                    try { result = Show(make(runtime)()); }
                    catch (Exception e) { result = "-"; error = e.GetType().Name + ": " + e.Message; }
                    output.WriteLine(JsonSerializer.Serialize(new[] { name , sink.Buffer.ToString() , result , error }));
                }
                return 0;
            }
            case "cold":
            {
                var (name , make) = Registry.All.First(o => o.Name == args[1]);
                var sw = Stopwatch.StartNew();
                object r = make(new Runtime(Runtime.AddJyunoCommandType.Math))();
                Console.Error.WriteLine($"main→결과 {sw.Elapsed.TotalMilliseconds:F3} ms, 프로세스 시작→결과 {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:F1} ms");
                Console.WriteLine(Show(r));
                return 0;
            }
        }
        return 2;
    }
}
