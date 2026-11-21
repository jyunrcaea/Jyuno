# Jyuno

Jyuno is a small, lightweight language.  
[Website & Playground](https://jyunrcaea.github.io/JyunoWeb) · [GitHub](https://github.com/jyunrcaea/Jyuno) · [Wiki](https://github.com/jyunrcaea/Jyuno/wiki)

## Purpose
Jyuno was made to make it easy to add plugin support to C# programs.<br>
Add this library to the C# program you are building, register a few functions, and you have a plugin system!

### Why a new language?
You could also run Python modules with IronPython, or run C#/F# modules directly.  
The problem with those approaches is that there is no way to make sure a script is safe: it could freely modify files or leak information.  
To solve this security problem, Jyuno lets you restrict which commands a script can use!  
If you never provide commands that can touch files in the first place, a script that tries to use them just fails with an error about a missing command, so you can build plugin support safely!

## Pros
- You can add C# native functions/variables to Jyuno and use them from scripts.  
- Scripts can be run safely with features such as command restrictions and preventing variables from being removed.

## Cons
- It is on the slow side. (It became much faster in 1.3.1, but simple loops are still about 2-4x slower than Python.)
- There are no classes. (Structs (since 1.5.0) and arrays/lists are supported instead.)

## Language
See [Jyuno Language](https://github.com/jyunrcaea/Jyuno/blob/master/Language/README.md) (in Korean).

## Installation
Install it from NuGet.
```
dotnet add package Jyuno
```

## Usage
Reference this library and use it.

```cs
Runtime runtime = new();
Interpreter interpreter = runtime.Create(File.ReadAllLines("code.txt"));
interpreter.Run();
```
Create a Runtime, then create an Interpreter from it and run it.

### Runtime
The core execution manager of Jyuno. You add global functions/variables to it, and you create interpreters from it.<br>
If you only run one piece of code this may not seem necessary, but when you run several, the runtime groups them together<br>
so you don't have to add the same functions/variables to every interpreter. Functions/variables added here are available to every interpreter created from this runtime.

### Interpreter
The executor that actually runs Jyuno code. You can also add functions/variables to an interpreter, but they are local, not global.<br>
(If they were global, the variable names would already have collided....)<br>

``Run()`` runs the script and returns the value returned with ``return``. When it reaches a line with a syntax error, it stops and returns a ``GrammarError``.<br>
You can also run code one line at a time with ``ExecuteLine()``, but keywords that move the execution position (``if``, ``while``, ``repeat``, ``goto``, etc.) can only be used when running with ``Run()``.<br>
Even if a script removes a global function/variable of the runtime, it is removed only in that interpreter and does not affect other interpreters of the same runtime.

## More
Detailed documentation is in the [wiki](https://github.com/jyunrcaea/Jyuno/wiki).

| Page | Contents |
|---|---|
| [Stopping Execution and Errors](https://github.com/jyunrcaea/Jyuno/wiki/Stopping-Execution-and-Errors) | Time limits (``CancellationToken``), call depth limit (``CallDepthLimit``), exception types (``JyunoException``, ``JyunoLimitException``, ``JyunoThrownException``) and the script's ``ttl``/``try`` (since 1.6.2) |
| [Memory Limit](https://github.com/jyunrcaea/Jyuno/wiki/Memory-Limit) | Limiting the memory a script holds (``DefaultMemoryLimit``, ``MemoryLimit``, since 1.7.0) |
| [Execution Model and Performance](https://github.com/jyunrcaea/Jyuno/wiki/Execution-Model-and-Performance) | Per-line compilation, constant folding, name caching |
| [Experimental Optimizations](https://github.com/jyunrcaea/Jyuno/wiki/Experimental-Optimizations) | ``EscapeAnalysis``, ``Inlining`` (since 1.5.2, off by default) |
| [Code Analysis](https://github.com/jyunrcaea/Jyuno/wiki/Code-Analysis) | Analyzing scripts without running them (``Jyuno.Analysis``, since 1.4.0) and Native JS restrictions |
| [Struct Values](https://github.com/jyunrcaea/Jyuno/wiki/Struct-Values) | Struct values received in C# (``StructValue``, since 1.5.0) |

## License
MIT
