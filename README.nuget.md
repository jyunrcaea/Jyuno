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

### Stopping execution and errors (since 1.6.2)
```cs
using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(5));
interpreter.CancellationToken = cancel.Token; //checked before each line runs (stops infinite loops too)
interpreter.CallDepthLimit = 1000;            //maximum function call depth (default MaxCallDepth, 200)
try
{
    interpreter.Run();
}
catch (OperationCanceledException) { /* cancelled */ }
catch (JyunoException e)
{
    Console.WriteLine($"line {e.Line + 1}: {e.Message}");
}
```
- ``CancellationToken``: once cancelled, ``OperationCanceledException`` is thrown before the next line runs. (Since 1.7.0 it is also checked on function calls and inside long-running built-in computations.)
- ``CallDepthLimit``: the maximum function call depth. Even below it, a ``JyunoException`` is thrown if the thread runs low on stack.
- ``JyunoException.Line``: the line (0-based) where the error happened. For other exceptions thrown by functions the program added, use ``interpreter.LastErrorLine``.
- If an expression on one line is nested too deeply (hundreds of parentheses or operators), or a value to compare/copy/convert to a string is nested too deeply, a ``JyunoException`` is thrown instead of ending the process with a stack overflow.
- Passing a wrong kind of value to a built-in function throws ``JyunoException`` instead of a C# exception (``InvalidCastException`` etc.). I/O errors of the file commands are also rethrown as ``JyunoException`` (the original exception is the ``InnerException``).
- If a function added by the program calls ``Run()`` again during execution, execution returns to the calling line and scope afterwards (previously it continued from line 0 in the top-level scope). A line added with ``ExecuteLine(string)`` during execution is not run again by the outer ``Run()``.

### Memory limit (since 1.7.0)
```cs
runtime.DefaultMemoryLimit = 256L * 1024 * 1024;     //applies to every interpreter of this runtime (counted per interpreter, 0 = no limit)
interpreter.MemoryLimit = 64L * 1024 * 1024;  //a different limit for this interpreter only (if unset, follows the runtime; 0 = no limit for this interpreter)
try
{
    interpreter.Run();
}
catch (JyunoException e) { /* memory limit exceeded */ }
Console.WriteLine(interpreter.MemoryUsage);   //size measured last time (bytes)
```
- Compares the approximate size of the values a script holds (variables, struct values, arrays/lists, loop states, call arguments, large values of the line being computed) against the limit. Discarded values (garbage) are not counted.
- **When it measures**: at the next iteration of a loop (``end``), ``goto``, function calls and inside long-running built-in loops, it only reads the per-thread allocation counter (``GC.GetAllocatedBytesForCurrentThread``, about 1.5ns), and walks the held values only after enough has been allocated since the last measurement. It does not check on every line.
- **May exceed slightly**: allocations smaller than 1MB run first and are caught at the next measurement. Measurements get more frequent near the limit, so held values **may exceed the limit by about 1MB**. (Sizes are estimates for 64-bit .NET.)
- **Rejected up front**: built-in functions that create values larger than 1MB (``array.new``/``range``, ``add``/``sub``/``copy``/``slice``/``distinct`` of arrays/lists, ``push`` growing a list, string repetition (``mul``) and concatenation, ``string.format``/``join``/``pad*``/``split``, the items of ``for``, etc.) compute the size first and throw instead of allocating when it would exceed the remaining limit. Strings longer than about 500K characters fall in this category.
- Regardless of the limit, a string longer than the maximum length (about 500M characters) is not created and an error is thrown (``string.format '{0:D999999999}' 1`` etc.).
- **Runtime and interpreter**: both can be set. ``Runtime.DefaultMemoryLimit`` is the default for all interpreters of the runtime (including existing ones), and an interpreter whose ``Interpreter.MemoryLimit`` is set uses that value. Allocation is counted per thread and held values per interpreter, so there is no runtime-wide total.
- **When writing C# functions (``AddFunction``)**: Jyuno cannot watch memory while a C# function runs. If a function builds large values or loops for a long time, limit the size and time inside the function itself. Checks resume as soon as the function returns: what it allocated on this thread is part of the thread's allocation count and is measured at the next check point, and a string/array/list it returns is counted (even if made on another thread) until the line ends. Memory allocated on other threads and left on the C# side, or values kept in C# objects (static fields, captured variables, variables added by the program (``AddVariable``)), are not counted.
- After the limit is exceeded, the values the interpreter held are reclaimed by the GC once the interpreter is dropped.
- The memory limit only restricts values held by scripts; to protect the whole host process, use it together with a container/process memory limit (``GCHeapHardLimit`` etc.).
- Also since 1.7.0, ``CancellationToken`` is checked not only between lines but also on function calls (including inlined calls) and inside long-running built-in computations (comparison, string conversion, copying, sorting, distinct, etc.), so computations that never finish within one line can be stopped too.

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
  - The call depth limit (``CallDepthLimit``) and ``CurrentExecuteLine`` are the same as before.
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
| ``Statement`` / ``Expression`` | Statements (``IfStatement``, ``LoopStatement``, ``ForStatement``, ``FunctionStatement``, ``GotoStatement``, etc.) and expressions (``CallExpression``, ``AssignExpression``, ``RemoveExpression``, etc.), split by the same rules used to compile lines. Since 1.6.0, operators are ``BinaryExpression``, ``UnaryExpression``, ``ConditionalExpression`` and ``CompoundAssignExpression``, and a struct's operator function has its symbol in ``FunctionStatement.Operator``. Since 1.6.1, ``CallExpression.IsKeywordArgument`` tells whether a name was written without parentheses in the operator expression of a keyword argument (``if``, ``return``, etc.), where a missing name gets a different error message. |
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

The operators added in 1.6.0 (``+``, ``==``, ``&&``, etc.) and struct operator functions (``func +``) are supported by Native JS since JyunoSdk 0.5.0 (requires Jyuno 1.6.1).

### Struct values (StructValue, since 1.5.0)
A struct value returned by a script or passed as an argument to a C# function is a ``Jyuno.Language.StructValue``. You can read ``TypeName`` (the struct name), ``FieldNames`` (the field names), and fields with ``value["field"]`` or ``TryGetField``.  
A value passed to C# is a separate copy from the struct value in the script variable.

## License
MIT
