using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using Jyuno;
using Jyuno.Benchmarks;

//사용법: Jyuno.Benchmarks [--iterations N] [--filter name1,name2,...] [--json]
//--filter: 이름에 포함된 벤치마크만, 적은 순서대로 실행 (프로필 오염 실험용)
//각 반복마다 새 Runtime/Interpreter를 만들어서(컴파일 포함) 실행 시간을 잼
int iterations = 30;
string? filter = null;
bool json = false;
for (int i = 0 ; i < args.Length ; i++)
{
    switch (args[i])
    {
        case "--iterations": iterations = int.Parse(args[++i]); break;
        case "--filter": filter = args[++i]; break;
        case "--json": json = true; break;
    }
}

string config = string.Join(" " , Environment.GetEnvironmentVariables().Keys.Cast<string>()
    .Where(k => k.StartsWith("DOTNET_") && !k.StartsWith("DOTNET_CLI") && k != "DOTNET_NOLOGO" && k != "DOTNET_ROOT")
    .OrderBy(k => k).Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}"));
if (!json)
    Console.WriteLine($".NET {Environment.Version}, Jyuno {typeof(Runtime).Assembly.GetName().Version}, {(config.Length > 0 ? config : "기본 설정")}");

var selected = filter is null ? Benchmarks.All
    : filter.Split(',').SelectMany(f => Benchmarks.All.Where(b => b.Name.Contains(f))).ToArray();
foreach (var bench in selected)
{
    double[] times = new double[iterations];
    object? result = null;
    for (int it = 0 ; it < iterations ; it++)
    {
        var sw = Stopwatch.StartNew();
        Runtime runtime = new();
        using Interpreter interpreter = runtime.Create(bench.Lines);
        result = interpreter.Run();
        sw.Stop();
        times[it] = sw.Elapsed.TotalMilliseconds;
    }
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new {
            bench = bench.Name , runtime = Environment.Version.ToString() , config ,
            result = result?.ToString() , times
        }));
    }
    else
    {
        var steady = times.Skip(Math.Min(10 , iterations / 2)).OrderBy(t => t).ToArray();
        double median = steady[steady.Length / 2];
        Console.WriteLine($"{bench.Name,-22} 첫 실행 {times[0],8:F1} ms | 안정 상태 중앙값 {median,7:F2} ms | 결과 {result}");
    }
}
