# Jyuno

작고 가벼운 언어 Jyuno 입니다.

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
- **매우 느립니다. (반복문에서 파이썬보다 약 100배 느립니다.)**
- 클래스 및 구조체가 없습니다.

## 언어
[Jyuno Language](/Language/README.md) 를 참고하세요.

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

