Struct values as the C# program receives them. (Since 1.5.0)

A struct value returned by a script or passed as an argument to a C# function is a ``Jyuno.Language.StructValue``. You can read ``TypeName`` (the struct name), ``FieldNames`` (the field names), and fields with ``value["field"]`` or ``TryGetField``.  
A value passed to C# is a separate copy from the struct value in the script variable.
