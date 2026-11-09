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
| ``Statement`` / ``Expression`` | Statements (``IfStatement``, ``LoopStatement``, ``ForStatement``, ``FunctionStatement``, ``GotoStatement``, etc.) and expressions (``CallExpression``, ``AssignExpression``, ``RemoveExpression``, etc.), split by the same rules used to compile lines. |
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
5. Structs (``struct``, since 1.5.0) can't be used yet.

### Struct values (StructValue, since 1.5.0)
A struct value returned by a script or passed as an argument to a C# function is a ``Jyuno.Language.StructValue``. You can read ``TypeName`` (the struct name), ``FieldNames`` (the field names), and fields with ``value["field"]`` or ``TryGetField``.  
A value passed to C# is a separate copy from the struct value in the script variable.

## License
MIT
