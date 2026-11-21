Limits the size of the values a script holds. (Since 1.7.0)

```cs
runtime.DefaultMemoryLimit = 256L * 1024 * 1024;     //applies to every interpreter of this runtime (counted per interpreter, 0 = no limit)
interpreter.MemoryLimit = 64L * 1024 * 1024;  //a different limit for this interpreter only (if unset, follows the runtime; 0 = no limit for this interpreter)
try
{
    interpreter.Run();
}
catch (JyunoLimitException e) { /* memory limit exceeded (limit 64MB, using about 64.3MB). Scripts can't catch it with try */ }
Console.WriteLine(interpreter.MemoryUsage);   //size measured last time (bytes)
```
- Compares the approximate size of the values a script holds (variables, struct values, arrays/lists, loop states, call arguments, large values of the line being computed, thrown values being handled in a ``catch``) against the limit. Discarded values (garbage) are not counted.
- **When it measures**: at the next iteration of a loop (``end``), ``goto``, function calls and inside long-running built-in loops, it only reads the per-thread allocation counter (``GC.GetAllocatedBytesForCurrentThread``, about 1.5ns), and walks the held values only after enough has been allocated since the last measurement. It does not check on every line.
- **May exceed slightly**: allocations smaller than 1MB run first and are caught at the next measurement. Measurements get more frequent near the limit, so held values **may exceed the limit by about 1MB**. (Sizes are estimates for 64-bit .NET.)
- **Rejected up front**: built-in functions that create values larger than 1MB (``array.new``/``range``, ``add``/``sub``/``copy``/``slice``/``distinct`` of arrays/lists, ``push`` growing a list, string repetition (``mul``) and concatenation, ``string.format``/``join``/``pad*``/``split``, the items of ``for``, etc.) compute the size first and throw instead of allocating when it would exceed the remaining limit. Strings longer than about 500K characters fall in this category.
- Regardless of the limit, a string longer than the maximum length (about 500M characters) is not created and an error is thrown (``string.format '{0:D999999999}' 1`` etc.).
- **Runtime and interpreter**: both can be set. ``Runtime.DefaultMemoryLimit`` is the default for all interpreters of the runtime (including existing ones), and an interpreter whose ``Interpreter.MemoryLimit`` is set uses that value. Allocation is counted per thread and held values per interpreter, so there is no runtime-wide total.
- **When writing C# functions (``AddFunction``)**: Jyuno cannot watch memory while a C# function runs. If a function builds large values or loops for a long time, limit the size and time inside the function itself. Checks resume as soon as the function returns: what it allocated on this thread is part of the thread's allocation count and is measured at the next check point, and a string/array/list it returns is counted (even if made on another thread) until the line ends. Memory allocated on other threads and left on the C# side, or values kept in C# objects (static fields, captured variables, variables added by the program (``AddVariable``)), are not counted.
- After the limit is exceeded, the values the interpreter held are reclaimed by the GC once the interpreter is dropped.
- Exceeding the limit inside a script's time-limit block (``ttl``) is still a ``JyunoLimitException``. A ttl block only leaves without an error when its time is up; the memory limit and ``CancellationToken`` stop execution regardless of ttl. (See [Stopping Execution and Errors](Stopping-Execution-and-Errors).)
- The memory limit only restricts values held by scripts; to protect the whole host process, use it together with a container/process memory limit (``GCHeapHardLimit`` etc.).
