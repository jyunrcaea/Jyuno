# Jyuno.Benchmarks

인터프리터 성능을 비교할 때 쓰는 벤치마크입니다. 라이브러리 빌드와 NuGet 패키지에는 포함되지 않습니다.

```
dotnet build Jyuno.Benchmarks -c Release
dotnet Jyuno.Benchmarks/bin/Release/net8.0/Jyuno.Benchmarks.dll --iterations 40
dotnet Jyuno.Benchmarks/bin/Release/net8.0/Jyuno.Benchmarks.dll --iterations 40 --filter while_sum,fib
```

- 반복마다 새 `Runtime`/`Interpreter`를 만들어서(줄 컴파일 포함) 실행 시간을 잽니다.
- `첫 실행`은 프로세스를 띄우고 처음 실행한 시간(.NET JIT의 Tier0 코드로 실행되는 구간 포함), `안정 상태`는 11번째 반복부터의 중앙값입니다.
- `--json`은 반복마다의 시간을 JSON 한 줄로 출력합니다. `--filter`에 여러 이름을 쉼표로 적으면 적은 순서대로 실행합니다.
- 다른 .NET 버전에서 재려면 `dotnet build Jyuno.Benchmarks -c Release -p:BenchFramework=net10.0`처럼 지정해서 빌드합니다. (출력은 `bin/Release/net10.0`)

## .NET 런타임 설정 비교
Dynamic PGO와 티어드 컴파일은 라이브러리가 아니라 실행하는 프로그램(호스트)의 설정입니다. 환경 변수로 바꿔서 비교합니다.
```
DOTNET_TieredPGO=0 dotnet .../Jyuno.Benchmarks.dll
DOTNET_TieredCompilation=0 dotnet .../Jyuno.Benchmarks.dll
dotnet publish Jyuno.Benchmarks -c Release -r linux-x64 --self-contained false -p:PublishReadyToRun=true -o out/r2r
```
