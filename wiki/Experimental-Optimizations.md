Extra optimizations you turn on with properties of ``Interpreter``. (Since 1.5.2, off by default)  
Results, output and errors are the same as with them off. (If they differ, it is a bug.)
```cs
interpreter.EscapeAnalysis = true;
interpreter.Inlining = true;
```

## Escape analysis (EscapeAnalysis)
Copies struct values less often.
- A struct value that was just made and is not stored anywhere yet (the copy made by reading a variable, or the result of a constructor) is stored into a variable or parameter as it is, without a second copy. (``b = a``, ``f p`` and ``v = v.plus w`` each make one copy fewer.)
- A value that was passed to a C# function, returned to the C# program, or stored in an array/list can be seen from elsewhere, so it is copied as before.
- A constructor result that could differ from its copy is copied as before. (A field holds an array/list from outside, the constructor removed or replaced a member name of a struct field, or the struct defines another struct inside.)  
  This is checked when the constructor result is stored into a variable or parameter. A value passed straight to a C# function or a list is not checked. (Since 1.5.3)
- Built-in functions that only return a bool/integer (``equal``, ``less``, ``greater``, ``not``, ``and``, ``or``) receive the variable's struct value without a copy. (Only when the arguments after it are constants or variable reads. The comparison gives the same result as comparing copies.)  
  If a later argument is a variable provided by the program (``AddVariable``), the struct value is copied before the C# code that reads that variable runs. (Since 1.5.3)
- A ``param`` inside a loop, or in a function that has a ``goto``, can run again, so its argument is copied as before.

## Inlining
A function that consists only of ``param`` lines and one ``return`` line (struct functions included) is evaluated without creating a function scope or a call frame. Parameters are read directly from the arguments.
- If a parameter is a struct value, ``p.x`` is read directly from that argument.
- If the ``return`` expression has an assignment (``=``) or a label (``:``), calls or indexes a parameter (``p 0``), or parameter names repeat or contain ``.``, the function scope is created as before.
- If a C# function calls ``ExecuteLine()`` inside an inlined function, the function scope is created at that point and the line runs inside the function.
- The same holds if a C# function calls ``Run()`` inside an inlined function. While it runs, ``for``/``repeat`` progress and the arguments read by ``param`` belong to that function. (Since 1.5.3)
- If the program replaced or inserted lines in ``scripts`` between runs, whether a function can be inlined is checked again. (Since 1.5.3)
- The call depth limit (``CallDepthLimit``) and ``CurrentExecuteLine`` are the same as before.

## Gains
The gains are in code that uses structs and in one-line functions. (With both on: about 2.4x for functions that take a struct, 2.5x for struct comparison, 1.9x for calls to one-line functions, 1.6x for struct functions that build a new struct value and 1.3x for struct assignment. Other code is unaffected.)

## Fixed problems
- 1.6.2 fixes results that differed between runs with the optimizations on and off for structs that redefine comparison operators. When a redefined ``==`` (including one on a struct inside a field) is met while comparing a borrowed struct value, the comparison is done again on copies, and ``less``/``greater`` with a struct on the right side use the original built-in function.  
  About 40,000 random scripts whose operator functions change values were used to check that all four settings give the same results.
- 1.5.3 fixes 4 problems where a run with the optimizations on differed from a run with them off. All of them involve the C# program stepping in during or between runs. (C# code that runs when a variable is read, ``Run()`` called inside an inlined function, ``scripts`` changed between runs.)  
  Tests that compare runs with the optimizations on and off were added (``Jyuno.Tests/Differential``, 215 scripts).
