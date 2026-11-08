# Jyuno.Benchmarks

[실험] Dynamic PGO 실험에 사용한 벤치마크입니다. 릴리즈에 포함되지 않습니다.

```
dotnet build Jyuno.Benchmarks -c Release
dotnet Jyuno.Benchmarks/bin/Release/net8.0/Jyuno.Benchmarks.dll --iterations 40
dotnet Jyuno.Benchmarks/bin/Release/net10.0/Jyuno.Benchmarks.dll --iterations 40 --filter while_sum,fib
```

- 반복마다 새 `Runtime`/`Interpreter`를 만들어서(줄 컴파일 포함) 실행 시간을 잽니다.
- `첫 실행`은 프로세스를 띄우고 처음 실행한 시간(JIT의 Tier0 코드로 실행되는 구간 포함), `안정 상태`는 11번째 반복부터의 중앙값입니다.
- `--json`은 반복마다의 시간을 JSON 한 줄로 출력합니다. `--filter`에 여러 이름을 쉼표로 적으면 적은 순서대로 실행합니다.

## .NET 런타임 설정 비교
런타임의 Dynamic PGO와 티어드 컴파일은 라이브러리가 아니라 실행하는 프로그램(호스트)의 설정입니다. 환경 변수로 바꿔서 비교합니다.
```
DOTNET_TieredPGO=0 dotnet .../Jyuno.Benchmarks.dll
DOTNET_TieredCompilation=0 dotnet .../Jyuno.Benchmarks.dll
DOTNET_TC_CallCountingDelayMs=0 dotnet .../Jyuno.Benchmarks.dll
dotnet publish Jyuno.Benchmarks -c Release -f net8.0 -r linux-x64 --self-contained false -p:PublishReadyToRun=true -o out/r2r
```

## 인터프리터 수준 특화 비교
`JYUNO_SPECIALIZE` 환경 변수로 호출 자리 특화를 바꿔서 비교합니다. (실험용 스위치)

| 값 | 동작 |
|---|---|
| `off` | 1.3.2와 같음 |
| `cond` | `if`/`while`의 조건이 하나라면 인자 배열 없이 판단 |
| `static` | `cond` + 컴파일할때 내장 함수(add, less 등)를 가리키는 호출 자리를 미리 정한 빠른 경로로 실행 |
| `pgo` (기본값) | `cond` + 호출 자리마다 실행 중에 함수와 인자 타입을 기록(8번)한 다음 특화, 가정이 틀리면 탈최적화 (2번 틀리면 특화 포기) |
