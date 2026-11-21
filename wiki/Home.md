Jyuno 라이브러리를 C# 프로그램에서 쓸 때 필요한 자세한 설명입니다. 소개와 설치, 기본 사용법은 [README](https://github.com/jyunrcaea/Jyuno#readme)를, 언어 문법은 [Jyuno Language](https://github.com/jyunrcaea/Jyuno/blob/master/Language/README.md)를 참고하세요.

## 문서
| 문서 | 내용 |
|---|---|
| [실행 중단과 오류](실행-중단과-오류) | 시간 제한(``CancellationToken``), 호출 깊이 제한(``CallDepthLimit``), 예외 종류(``JyunoException``, ``JyunoLimitException``, ``JyunoThrownException``)와 스크립트의 ``ttl``/``try`` (1.6.2부터) |
| [메모리 한도](메모리-한도) | 스크립트가 붙잡는 메모리 제한 (``DefaultMemoryLimit``, ``MemoryLimit``, 1.7.0부터) |
| [실행 방식과 성능](실행-방식과-성능) | 줄 단위 컴파일, 상수 미리 계산, 이름 캐싱 |
| [실험적 최적화](실험적-최적화) | ``EscapeAnalysis``, ``Inlining`` (1.5.2부터, 기본값은 꺼짐) |
| [코드 분석](코드-분석) | 실행하지 않고 스크립트 분석하기 (``Jyuno.Analysis``, 1.4.0부터)와 Native JS 제약 |
| [구조체 값](구조체-값) | C#에서 받은 구조체 값 다루기 (``StructValue``, 1.5.0부터) |

## English
| Page | Contents |
|---|---|
| [Stopping Execution and Errors](Stopping-Execution-and-Errors) | Time limits (``CancellationToken``), call depth limit (``CallDepthLimit``), exception types and the script's ``ttl``/``try`` (since 1.6.2) |
| [Memory Limit](Memory-Limit) | Limiting the memory a script holds (``DefaultMemoryLimit``, ``MemoryLimit``, since 1.7.0) |
| [Execution Model and Performance](Execution-Model-and-Performance) | Per-line compilation, constant folding, name caching |
| [Experimental Optimizations](Experimental-Optimizations) | ``EscapeAnalysis``, ``Inlining`` (since 1.5.2, off by default) |
| [Code Analysis](Code-Analysis) | Analyzing scripts without running them (``Jyuno.Analysis``, since 1.4.0) and Native JS restrictions |
| [Struct Values](Struct-Values) | Struct values received in C# (``StructValue``, since 1.5.0) |
