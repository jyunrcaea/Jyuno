# Jyuno

작고 가벼운 언어 Jyuno 입니다.  
[소개 사이트 & 플레이그라운드](https://jyunrcaea.github.io/JyunoWeb)

## 목적
C# 프로그램에 플러그인 기능을 쉽게 추가할수 있도록 하기위해 만들었습니다.<br>
개발하고 있는 C# 프로그램에 이 라이브러리를 추가해서 함수 몇몇개만 추가해주면, 쉽게 플러그인 기능을 만들수 있습니다!

### 왜 새로운 언어인가?
ironpython을 사용한 python 모듈을 실행하거나, C#/F# 모듈을 직접 실행하는 방법도 있습니다.  
하지만 이런 방법의 문제점은 해당 스크립트가 파일을 임의로 조작하거나, 정보를 유출하는 등, 안전한 스크립트인지 확인할 방도가 없다는겁니다.  
그래서 Jyuno라는 언어에서는 이러한 보안 문제를 해결하기 위해, 임의의 명령어를 제한하는 기능을 만들었습니다!  
처음부터 파일을 조작할수 있는 명령어를 제공하지 않으면 스크립트는 없는 명령어로 인해 오류만 띄우고 끝나므로, 플러그인 기능을 안전하게 구현할수 있습니다!

## 장점
- C# 네이티브 함수/변수를 Jyuno에 추가하여 조작할수 있습니다.  
- 명령어 제한이나 변수 제거 금지 등의 기능을 통해 스크립트를 안전하게 실행할수 있습니다.

## 단점
- 느린 편입니다. (1.3.1부터 크게 빨라졌지만, 단순 반복문에서 아직 파이썬보다 약 2~4배 느립니다.)
- 클래스가 없습니다. (대신 구조체(1.5.0부터)와 배열/리스트는 지원합니다.)

## 언어
[Jyuno Language](/Language/README.md) 를 참고하세요.

## 설치
NuGet에서 설치할수 있습니다.
```
dotnet add package Jyuno
```

## 실행
해당 라이브러리를 포함하여 사용하면 됩니다.

```cs
Runtime runtime = new();
Interpreter interpreter = runtime.Create(File.ReadAllLines("code.txt"));
interpreter.Run();
```
그리고 Runtime 변수를 만든뒤, 다시 Interpreter 변수를 만들어서 실행하면 됩니다.

### 런타임(Runtime)
핵심이 되는 Jyuno의 실행 관리자입니다. 이 변수에서 전역 함수/변수를 추가할수 있고, 여기서 인터프리터를 생성합니다.<br>
코드를 하나만 실행한다면 별 필요성이 없어보일수 있지만, 만약 여러 코드를 실행할 경우 각 인터프리터마다 필요한 함수/변수를 추가하는 과정을 생략하기 위해<br>
런타임을 따로 만들어서 묶는겁니다. 이 안에서 함수/변수를 추가하면, 이 런타임에서 생성된 모든 인터프리터가 사용할수 있습니다.

### 인터프리터(Interpreter)
Jyuno의 코드를 직접 실행하는 실행기입니다. 인터프리터 내에서도 함수/변수를 추가할수 있으나, 전역이 아닌 지역(로컬) 함수/변수 입니다.<br>
(만약 전역이였다면, 이미 변수 이름이 충돌했겠죠....?)<br>

``Run()``은 스크립트를 실행하고, ``return``으로 반환된 값을 돌려줍니다. 문법 오류가 있는 줄을 만나면 실행을 멈추고 ``GrammarError``를 반환합니다.<br>
``ExecuteLine()``으로 한 줄씩 실행할수도 있지만, 실행 위치를 옮기는 키워드(``if``, ``while``, ``repeat``, ``goto`` 등)는 ``Run()``으로 실행할 때만 사용할수 있습니다.<br>
스크립트가 런타임의 전역 함수/변수를 삭제하더라도 해당 인터프리터에서만 삭제되며, 같은 런타임의 다른 인터프리터에는 영향을 주지 않습니다.

### 실행 방식과 성능
- 각 줄은 처음 실행될 때 한번만 클로저 트리로 컴파일되고, 그 다음부터는 토큰을 다시 해석하지 않고 컴파일된 트리를 실행합니다.
- ``(add 1 2)``처럼 인자가 모두 상수인 내장 함수 호출(``add``, ``sub``, ``mul``, ``div``, ``mod``, ``equal``, ``less``, ``greater``, ``and``, ``or``, ``not``, ``int``, ``double``, ``string``, ``bool``, ``length``, ``math.*``)은 컴파일할 때 미리 계산합니다.  
  스크립트에서 같은 이름을 다시 정의하거나(``func add``, ``local add``) 삭제(``add =``)했다면, 미리 계산한 값 대신 원래대로 호출합니다. 실행할 때 오류가 나는 계산(``div 1 0`` 등)과 배열/리스트를 만드는 계산은 미리 계산하지 않습니다.
- 이름(변수/함수)을 찾은 결과는 이름이 나오는 자리마다 캐싱되며, 변수가 새로 만들어지거나 삭제/교체되면 자동으로 다시 찾습니다.
- ``add i 1``처럼 내장 함수(``add``, ``sub``, ``mul``, ``div``, ``mod``, ``equal``, ``less``, ``greater``, ``and``, ``or``, ``not``)를 인자 1~3개로 부르는 자리는, 실행할 때 이름이 여전히 같은 내장 함수를 가리키는지만 확인하고 인자 배열 없이 바로 계산합니다. (1.3.4부터)  
  바로 계산할수 없는 값(``div 1 0``, 문자열과 숫자 더하기 등)이거나 이름을 다시 정의/삭제했다면 원래대로 호출하므로, 결과와 오류는 같습니다. ``if``/``while``의 조건이 하나라면 인자 배열 없이 바로 참/거짓을 판단합니다.

### 실험적 최적화 (기본값은 꺼짐)
``Interpreter``의 속성으로 켤수 있습니다. 켜도 실행 결과, 출력, 오류는 끈 것과 같아야 합니다. (다르다면 버그입니다)
```cs
interpreter.EscapeAnalysis = true; //탈출 분석
interpreter.Inlining = true;       //인라이닝
```
- ``EscapeAnalysis``: 구조체 값의 복사를 줄입니다.
  - 변수를 읽어서 만든 복사본이나 생성자의 결과처럼 방금 만들어져서 아무 곳에도 담기지 않은 구조체 값은, 변수/파라미터에 넣을 때 다시 복사하지 않고 그대로 넣습니다. (``b = a``, ``f p``, ``v = v.plus w`` 등에서 복사가 한번씩 줄어듭니다)
  - C# 함수에 넘기거나, C# 프로그램에 반환하거나, 배열/리스트에 담은 값은 다른 곳에서도 볼수 있으므로 원래대로 복사합니다. 바깥의 배열/리스트를 필드에 담은 생성자 결과도 원래대로 복사합니다.
  - ``equal``, ``less``, ``greater``, ``not``, ``and``, ``or``처럼 결과가 bool/정수뿐인 내장 함수에는 변수의 구조체 값을 복사하지 않고 넘깁니다. (뒤의 인자가 상수나 변수 읽기일 때만)
  - 반복문 안이나 ``goto``가 있는 함수의 ``param``은 다시 실행될수 있으므로 인자를 원래대로 복사합니다.
- ``Inlining``: ``param`` 줄들과 ``return`` 한 줄로만 된 함수(구조체 함수 포함)는 함수 영역과 호출 정보를 만들지 않고, 파라미터를 인자에서 바로 읽어서 계산합니다.
  - 파라미터가 구조체 값이라면 ``p.x``는 인자인 구조체 값에서 바로 읽습니다.
  - ``return`` 식에 대입(``=``)이나 레이블(``:``)이 있거나, 파라미터를 함수처럼 부르거나 인덱스로 쓴다면(``p 0``) 원래대로 호출합니다.
  - 호출 깊이 제한(``MaxCallDepth``)과 ``CurrentExecuteLine``은 원래와 같습니다.

## 코드 분석 (Jyuno.Analysis, 1.4.0부터)
스크립트를 실행하지 않고 인터프리터와 같은 규칙으로 분석할수 있습니다. 편집기, 검사 도구, 다른 언어로의 변환기(예: [JyunoSdk](https://github.com/jyunrcaea/JyunoSdk)의 Native JS) 등에서 사용합니다.

```cs
using Jyuno.Analysis;

ScriptAnalysis analysis = ScriptAnalysis.Analyze(File.ReadAllLines("code.jyuno"));
foreach (var error in analysis.StructureErrors)      // if/end 짝 등 블록 구조 오류
    Console.WriteLine(error);
foreach (Statement statement in analysis.Root!.Statements)  // 구문 트리
    Console.WriteLine($"{statement.Line}: {statement.Kind}");
```

| API | 내용 |
|---|---|
| ``ScriptAnalysis.Analyze(lines)`` | 토큰, 블록 구조, 구문 트리(``Root``), 변수 영역(``TopScope``, ``Scopes``), 함수 목록, goto 대상 |
| ``Statement`` / ``Expression`` | 문장(``IfStatement``, ``LoopStatement``, ``ForStatement``, ``FunctionStatement``, ``GotoStatement`` 등)과 식(``CallExpression``, ``AssignExpression``, ``RemoveExpression`` 등). 줄을 컴파일하는 규칙과 같게 나눕니다. |
| ``Scope`` | 영역(최상위, 함수 호출 한번, for 반복 한번, 구조체 값 하나)마다 만들어질수 있는 이름과 종류(변수/함수/상수/구조체) |
| ``GotoStatement.Targets`` | goto가 같은 블록이나 바깥 블록으로 이동할 때의 실행 위치 |
| ``ScriptAnalysis.CheckNativeJs(globals)`` | JavaScript로 변환할때(Native JS) 필요한 제약 검사 |
| ``Runtime.GetGlobals()`` | 런타임의 전역 이름(필수 명령어, 콘솔/수학 명령어, 프로그램이 추가한 함수/변수)과 종류 |

### Native JS 제약
Native JS(Jyuno 코드를 JavaScript로 변환해서 실행)는 다음을 지켜야 합니다. ``CheckNativeJs``가 실행하기 전에 줄 번호와 함께 알려줍니다.
1. 변수/함수 삭제(``이름 =``)를 쓸수 없습니다.
2. 내장 함수와 전역 함수/상수(``add``, ``mul``, ``console.writeline``, ``math.pi``, ``true`` 등)를 다시 정의하거나(``func``, ``local``, ``param``, ``for``, 레이블, 상수) 대입할수 없습니다.
3. ``goto``로 블록(``if``, ``while``, ``repeat``, ``for``) 안쪽으로 들어갈수 없습니다. (같은 블록이나 바깥 블록의 줄로는 이동할수 있으며, 계산한 대상은 실행할때 검사합니다)
4. 블록 구조 오류(``end`` 누락 등)는 그 줄에 도달하기 전에, 실행하기 전에 보고합니다.
5. 구조체(``struct``, 1.5.0부터)는 아직 쓸수 없습니다.

### 구조체 값 (StructValue, 1.5.0부터)
스크립트가 반환하거나 C# 함수의 인자로 넘긴 구조체 값은 ``Jyuno.Language.StructValue``입니다. ``TypeName``(구조체 이름), ``FieldNames``(필드 이름), ``value["필드"]`` 또는 ``TryGetField``로 필드를 읽을수 있습니다.  
C#에 넘어간 값은 스크립트 변수의 구조체 값과 따로 복사된 값입니다.

## 테스트
```
dotnet test Jyuno.Tests
```

## 릴리즈
``v1.2.3`` 형식의 태그를 푸시하면 GitHub Actions가 테스트 후 NuGet에 배포하고 GitHub 릴리즈를 만듭니다. (``v1.2.3-beta.1`` 처럼 ``-``가 붙으면 프리릴리즈)
```
git tag v1.0.0
git push origin v1.0.0
```
태그를 푸시하는 대신 Actions 탭의 ``Release`` 워크플로우를 직접 실행(``Run workflow``)하고 버전을 입력해도 됩니다. (선택한 브랜치의 커밋에 ``v(버전)`` 태그와 릴리즈를 만듭니다)
NuGet 배포는 API 키 대신 [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)을 사용합니다.  
nuget.org의 Trusted Publishing 정책(저장소 ``jyunrcaea/Jyuno``, 워크플로우 ``release.yml``, 환경 ``production``)이 워크플로우와 일치해야 합니다.
