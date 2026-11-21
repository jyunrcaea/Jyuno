With ``Jyuno.Analysis`` you can analyze a script without running it, using the same rules as the interpreter. (Since 1.4.0) This is used by editors, checking tools, and translators to other languages (for example, Native JS in [JyunoSdk](https://github.com/jyunrcaea/JyunoSdk)).

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
| ``Statement`` / ``Expression`` | Statements (``IfStatement``, ``LoopStatement``, ``ForStatement``, ``FunctionStatement``, ``GotoStatement``, etc.) and expressions (``CallExpression``, ``AssignExpression``, ``RemoveExpression``, etc.), split by the same rules used to compile lines. Since 1.6.0, operators are ``BinaryExpression``, ``UnaryExpression``, ``ConditionalExpression`` and ``CompoundAssignExpression``, and a struct's operator function has its symbol in ``FunctionStatement.Operator``. Since 1.7.0, ``ttl`` ~ ``endttl`` is a ``TtlStatement`` (``Body``, with the time limit in ``Arguments[0]``), ``try`` ~ ``catch`` ~ ``tried`` is a ``TryStatement`` (``Body``, ``CatchLine``, ``CatchName``, ``CatchBody``), and ``throw`` is a ``ThrowStatement`` (``IsRethrow``). Since 1.6.1, ``CallExpression.IsKeywordArgument`` tells whether a name was written without parentheses in the operator expression of a keyword argument (``if``, ``return``, etc.), where a missing name gets a different error message. |
| ``Scope`` | For each scope (top level, one function call, one for iteration, one struct value), the names that can be created there and their kinds (variable/function/constant/struct) |
| ``GotoStatement.Targets`` | Execution positions when a goto jumps within the same block or to an outer block |
| ``ScriptAnalysis.CheckNativeJs(globals)`` | Checks the restrictions required for translating to JavaScript (Native JS) |
| ``Runtime.GetGlobals()`` | Global names of the runtime (essential commands, console/math commands, functions/variables added by the program) and their kinds |

## Native JS restrictions
Native JS (translating Jyuno code to JavaScript and running it) requires the following. ``CheckNativeJs`` reports violations with line numbers before running.
1. Removing variables/functions (``name =``) is not allowed.
2. Built-in functions and global functions/constants (``add``, ``mul``, ``console.writeline``, ``math.pi``, ``true``, etc.) cannot be redefined (``func``, ``local``, ``param``, ``for``, labels, constants) or assigned to.
3. ``goto`` cannot jump into a block (``if``, ``while``, ``repeat``, ``for``). (It can jump to lines in the same block or an outer block; computed targets are checked at run time.)
4. Block structure errors (a missing ``end``, etc.) are reported before running, before that line is reached.
5. Structs (``struct``, supported by Native JS since 1.5.1) can't be defined inside another struct.
6. Time-limit blocks (``ttl``, since 1.7.0) are not supported yet.
7. ``try``/``catch`` and ``throw`` (since 1.7.0) are not supported yet.

The operators added in 1.6.0 (``+``, ``==``, ``&&``, etc.) and struct operator functions (``func +``) are supported by Native JS since JyunoSdk 0.5.0 (requires Jyuno 1.6.1).
