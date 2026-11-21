How the program stops or limits a running script, and the exceptions it gets when something goes wrong. (Since 1.6.2, including what was added in 1.7.0.)

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

## Time and call depth limits
- ``CancellationToken``: once cancelled, ``OperationCanceledException`` is thrown before the next line runs.  
  Since 1.7.0 it is checked not only between lines but also on function calls (including inlined calls) and inside long-running built-in computations (comparison, string conversion, copying, sorting, distinct, etc.), so computations that never finish within one line can be stopped too.
- ``CallDepthLimit``: the maximum function call depth. Even below it, a ``JyunoException`` is thrown if the thread runs low on stack. (``JyunoLimitException`` since 1.7.0)
- To limit memory, see [Memory Limit](Memory-Limit). (Since 1.7.0)

## Errors
- ``JyunoException.Line``: the line (0-based) where the error happened. For other exceptions thrown by functions the program added, use ``interpreter.LastErrorLine``.
- If an expression on one line is nested too deeply (hundreds of parentheses or operators), or a value to compare/copy/convert to a string is nested too deeply, a ``JyunoException`` is thrown instead of ending the process with a stack overflow.
- Passing a wrong kind of value to a built-in function throws ``JyunoException`` instead of a C# exception (``InvalidCastException`` etc.). I/O errors of the file commands are also rethrown as ``JyunoException`` (the original exception is the ``InnerException``).

## ttl and try in scripts (since 1.7.0)
- To time-limit only part of a script, the script can use a ``ttl (seconds)`` ~ ``endttl`` block. (See [Jyuno Language](https://github.com/jyunrcaea/Jyuno/blob/master/Language/README.md#ttl-시간-제한-170부터) (in Korean).) When the time is up, execution leaves the block without an error and continues after ``endttl``. Code outside a ttl block does not check the time at all. To limit the whole script from the program, use ``CancellationToken``.
- Scripts can catch errors with ``try`` ~ ``catch`` ~ ``tried`` and throw any value with ``throw``. (See [Jyuno Language](https://github.com/jyunrcaea/Jyuno/blob/master/Language/README.md#try-catch-예외-처리-170부터) (in Korean).) An uncaught ``throw`` comes out as a ``JyunoThrownException`` whose ``Value`` is the thrown value (a struct value is a ``StructValue``).
  - Errors from limits set by the program (memory limit, ``CallDepthLimit``, running low on stack, expressions nested too deeply) are ``JyunoLimitException`` and cannot be caught by scripts. It derives from ``JyunoException``, so existing ``catch (JyunoException)`` code keeps working.
  - Cancellation (``OperationCanceledException``), ``ttl`` time limits, block-structure grammar errors, and exceptions other than ``JyunoException`` thrown by functions the program added cannot be caught by scripts either. To let a script catch an error from your function, throw a ``JyunoException`` (its message becomes the caught value) or a ``JyunoThrownException(value)``.

## Running again during execution
- If a function added by the program calls ``Run()`` again during execution, execution returns to the calling line and scope afterwards (previously it continued from line 0 in the top-level scope). A line added with ``ExecuteLine(string)`` during execution is not run again by the outer ``Run()``.
