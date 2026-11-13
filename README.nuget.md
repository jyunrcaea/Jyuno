# Jyuno

Jyuno is a small, lightweight language.  
[Website & Playground](https://jyunrcaea.github.io/JyunoWeb) · [GitHub](https://github.com/jyunrcaea/Jyuno)

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

### Execution model and performance
- Each line is compiled into a closure tree only once, the first time it runs. After that, the compiled tree is executed without re-parsing the tokens.
- Built-in function calls whose arguments are all constants, such as ``(add 1 2)`` (``add``, ``sub``, ``mul``, ``div``, ``mod``, ``equal``, ``less``, ``greater``, ``and``, ``or``, ``not``, ``int``, ``double``, ``string``, ``bool``, ``length``, ``math.*``), are computed ahead of time at compile time.  
  If the script redefines the same name (``func add``, ``local add``) or removes it (``add =``), the function is called normally instead of using the precomputed value. Computations that fail at run time (such as ``div 1 0``) and computations that create arrays/lists are not precomputed.
- Name (variable/function) lookups are cached at each place the name appears, and are looked up again automatically when a variable is created, removed or replaced.
- Where a built-in function (``add``, ``sub``, ``mul``, ``div``, ``mod``, ``equal``, ``less``, ``greater``, ``and``, ``or``, ``not``) is called with 1-3 arguments, such as ``add i 1``, it only checks at run time that the name still refers to the same built-in function, and then computes the result directly without an argument array. (Since 1.3.4)  
  If the value can't be computed directly (``div 1 0``, adding a string and a number, etc.) or the name was redefined/removed, the function is called normally, so results and errors are the same. When an ``if``/``while`` has a single condition, it is evaluated as true/false directly without an argument array.

### Experimental optimizations (since 1.5.2, off by default)
Turn them on with properties of ``Interpreter``. Results, output and errors are the same as with them off. (If they differ, it is a bug.)
```cs
interpreter.EscapeAnalysis = true;
interpreter.Inlining = true;
```
- ``EscapeAnalysis`` copies struct values less often.
  - A struct value that was just made and is not stored anywhere yet (the copy made by reading a variable, or the result of a constructor) is stored into a variable or parameter as it is, without a second copy. (``b = a``, ``f p`` and ``v = v.plus w`` each make one copy fewer.)
  - A value that was passed to a C# function, returned to the C# program, or stored in an array/list can be seen from elsewhere, so it is copied as before.
  - A constructor result that could differ from its copy is copied as before. (A field holds an array/list from outside, the constructor removed or replaced a member name of a struct field, or the struct defines another struct inside.)  
    This is checked when the constructor result is stored into a variable or parameter. A value passed straight to a C# function or a list is not checked. (Since 1.5.3)
  - Built-in functions that only return a bool/integer (``equal``, ``less``, ``greater``, ``not``, ``and``, ``or``) receive the variable's struct value without a copy. (Only when the arguments after it are constants or variable reads. The comparison gives the same result as comparing copies.)  
    If a later argument is a variable provided by the program (``AddVariable``), the struct value is copied before the C# code that reads that variable runs. (Since 1.5.3)
  - A ``param`` inside a loop, or in a function that has a ``goto``, can run again, so its argument is copied as before.
- ``Inlining`` evaluates a function that consists only of ``param`` lines and one ``return`` line (struct functions included) without creating a function scope or a call frame. Parameters are read directly from the arguments.
  - If a parameter is a struct value, ``p.x`` is read directly from that argument.
  - If the ``return`` expression has an assignment (``=``) or a label (``:``), calls or indexes a parameter (``p 0``), or parameter names repeat or contain ``.``, the function scope is created as before.
  - If a C# function calls ``ExecuteLine()`` inside an inlined function, the function scope is created at that point and the line runs inside the function.
  - The same holds if a C# function calls ``Run()`` inside an inlined function. While it runs, ``for``/``repeat`` progress and the arguments read by ``param`` belong to that function. (Since 1.5.3)
  - If the program replaced or inserted lines in ``scripts`` between runs, whether a function can be inlined is checked again. (Since 1.5.3)
  - The call depth limit (``MaxCallDepth``) and ``CurrentExecuteLine`` are the same as before.
- The gains are in code that uses structs and in one-line functions. (With both on: about 2.4x for functions that take a struct, 2.5x for struct comparison, 1.9x for calls to one-line functions, 1.6x for struct functions that build a new struct value and 1.3x for struct assignment. Other code is unaffected.)
- 1.5.3 fixes 4 problems where a run with the optimizations on differed from a run with them off. All of them involve the C# program stepping in during or between runs. (C# code that runs when a variable is read, ``Run()`` called inside an inlined function, ``scripts`` changed between runs.)  
  Tests that compare runs with the optimizations on and off were added (``Jyuno.Tests/Differential``, 215 scripts).

## Code analysis (Jyuno.Analysis, since 1.4.0)
You can analyze a script without running it, using the same rules as the interpreter. This is used by editors, checking tools, and translators to other languages (for example, Native JS in [JyunoSdk](https://github.com/jyunrcaea/JyunoSdk)).

```cs
using Jyuno.Analysis;

ScriptAnalysis analysis = ScriptAnalysis.Analyze(File.ReadAllLines("code.jyuno"));
foreach (var error in analysis.StructureErrors)      // block structure errors, such as unmatched if/end
    Console.WriteLine(error);
foreach (Statement statement in analysis.Root!.Statements)  // syntax tree
    Console.WriteLine($"{statement.Line}: {statement.Kind}");
```

| API | Description |
|---|---|
| ``ScriptAnalysis.Analyze(lines)`` | Tokens, block structure, syntax tree (``Root``), variable scopes (``TopScope``, ``Scopes``), function list, goto targets |
| ``Statement`` / ``Expression`` | Statements (``IfStatement``, ``LoopStatement``, ``ForStatement``, ``FunctionStatement``, ``GotoStatement``, etc.) and expressions (``CallExpression``, ``AssignExpression``, ``RemoveExpression``, etc.), split by the same rules used to compile lines. Since 1.6.0, operators are ``BinaryExpression``, ``UnaryExpression``, ``ConditionalExpression`` and ``CompoundAssignExpression``, and a struct's operator function has its symbol in ``FunctionStatement.Operator``. |
| ``Scope`` | For each scope (top level, one function call, one for iteration, one struct value), the names that can be created there and their kinds (variable/function/constant/struct) |
| ``GotoStatement.Targets`` | Execution positions when a goto jumps within the same block or to an outer block |
| ``ScriptAnalysis.CheckNativeJs(globals)`` | Checks the restrictions required for translating to JavaScript (Native JS) |
| ``Runtime.GetGlobals()`` | Global names of the runtime (essential commands, console/math commands, functions/variables added by the program) and their kinds |

### Native JS restrictions
Native JS (translating Jyuno code to JavaScript and running it) requires the following. ``CheckNativeJs`` reports violations with line numbers before running.
1. Removing variables/functions (``name =``) is not allowed.
2. Built-in functions and global functions/constants (``add``, ``mul``, ``console.writeline``, ``math.pi``, ``true``, etc.) cannot be redefined (``func``, ``local``, ``param``, ``for``, labels, constants) or assigned to.
3. ``goto`` cannot jump into a block (``if``, ``while``, ``repeat``, ``for``). (It can jump to lines in the same block or an outer block; computed targets are checked at run time.)
4. Block structure errors (a missing ``end``, etc.) are reported before running, before that line is reached.
5. Structs (``struct``, supported by Native JS since 1.5.1) can't be defined inside another struct.

The operators added in 1.6.0 (``+``, ``==``, ``&&``, etc.) are not supported yet by Native JS in JyunoSdk 0.4.1.

### Struct values (StructValue, since 1.5.0)
A struct value returned by a script or passed as an argument to a C# function is a ``Jyuno.Language.StructValue``. You can read ``TypeName`` (the struct name), ``FieldNames`` (the field names), and fields with ``value["field"]`` or ``TryGetField``.  
A value passed to C# is a separate copy from the struct value in the script variable.

## License
MIT
