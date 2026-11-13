using Jyuno.Compiler;
using System.Globalization;

namespace Jyuno.Language;

public static class JyunoCommands
{
    public static void AddDefault(VariableDictionary dict)
    {
        dict.AddConstantVariable("null" , () => null);
        dict.AddConstantVariable("true", () => true);
        dict.AddConstantVariable("false",() => false);
        dict.AddPureFunction("int" , args => {
            if (args.Length is 0)
                return 0L;
            if (args.Length == 1)
                return Values.ToInteger(args[0] ?? throw null_exception);
            return null;
        });
        dict.AddPureFunction("double" , args => {
            if (args.Length is 0)
                return 0.0;
            if (args.Length is 1)
                return Values.ToReal(args[0] ?? throw null_exception);
            return null;
        });
        dict.AddPureFunction("string" , args => args.Length is 0 ? string.Empty : Convert.ToString((args[0] ?? throw null_exception) , CultureInfo.InvariantCulture));
        dict.AddFunction("array" , args => Collections.NewArray(args));
        dict.AddFunction("list" , args => Collections.NewList(args));
        dict.AddPureFunction("length" , args => {
            if (args.Length is 0)
                throw new JyunoException("길이를 구할 값이 필요합니다.");
            return args[0] switch {
                string text => (long)text.Length,
                System.Collections.ICollection collection => (long)collection.Count,
                null => throw null_exception,
                _ => throw new JyunoException($"'{args[0]}'은/는 문자열 또는 배열/리스트가 아니므로 길이를 구할수 없습니다.")
            };
        });
        dict.AddPureFunction("add" , Add , intrinsic: Intrinsic.Add);
        dict.AddPureFunction("sub" , Sub , intrinsic: Intrinsic.Sub);
        dict.AddPureFunction("mul" , Mul , fold_mul , intrinsic: Intrinsic.Mul);
        dict.AddPureFunction("div" , Div , intrinsic: Intrinsic.Div);
        dict.AddPureFunction("mod" , Mod , intrinsic: Intrinsic.Mod);
        dict.AddPureFunction("equal" , Equal , intrinsic: Intrinsic.Equal);
        //인자가 셋 이상이라면 모든 인자가 오름차순(less)/내림차순(greater)인지 검사 (less 1 2 3 = 1 < 2 && 2 < 3)
        dict.AddPureFunction("less" , Less , intrinsic: Intrinsic.Less);
        dict.AddPureFunction("greater" , Greater , intrinsic: Intrinsic.Greater);
        //인자가 모두 정수라면 비트 연산(C의 a & b & c), 하나라도 정수가 아니라면 논리 연산(C의 a && b && c)
        dict.AddPureFunction("and" , And , intrinsic: Intrinsic.And);
        dict.AddPureFunction("or" , Or , intrinsic: Intrinsic.Or);
        //정수라면 비트 반전(C의 ~a), 그 외에는 논리 부정(C의 !a), 두번째 인자부터는 무시
        dict.AddPureFunction("not" , Not , intrinsic: Intrinsic.Not);
        dict.AddPureFunction("bool" , args => args.Length > 0 && Parser.IsTrue(args[0]));
        AddCollection(dict);
        Strings.Add(dict);
    }
    //배열/리스트를 다루는 명령어 (array.*, list.*)
    //sort, reverse, shuffle은 원래 컬렉션을 바꾸고 그대로 반환하며, 배열과 리스트 모두 받을수 있음
    //list.push, list.pop 등 길이를 바꾸는 명령어는 리스트에서만 쓸수 있음
    //컬렉션은 상수가 될수 없으므로 미리 계산(상수 계산)하지 않는 일반 함수로 추가
    static void AddCollection(VariableDictionary dict)
    {
        foreach (string kind in new[] { "array" , "list" })
        {
            string sort = $"{kind}.sort", reverse = $"{kind}.reverse", shuffle = $"{kind}.shuffle", contains = $"{kind}.contains",
                indexof = $"{kind}.indexof", slice = $"{kind}.slice", join = $"{kind}.join", create = $"{kind}.new";
            bool array = kind is "array";
            dict.AddFunction(sort , args => Collections.Sort(args , sort));
            dict.AddFunction(reverse , args => Collections.Reverse(args , reverse));
            dict.AddFunction(shuffle , args => Collections.Shuffle(args , shuffle));
            dict.AddFunction(contains , args => Collections.Contains(args , contains));
            dict.AddFunction(indexof , args => Collections.IndexOf(args , indexof));
            dict.AddFunction(slice , args => Collections.Slice(args , slice));
            dict.AddFunction(join , args => Collections.Join(args , join));
            dict.AddFunction(create , args => Collections.New(args , array , create));
            //1.4.2
            string copy = $"{kind}.copy", lastindexof = $"{kind}.lastindexof", count = $"{kind}.count", distinct = $"{kind}.distinct",
                binary_search = $"{kind}.binary_search", fill = $"{kind}.fill", min = $"{kind}.min", max = $"{kind}.max", sum = $"{kind}.sum", range = $"{kind}.range";
            dict.AddFunction(copy , args => Collections.Copy(args , copy));
            dict.AddFunction(lastindexof , args => Collections.LastIndexOf(args , lastindexof));
            dict.AddFunction(count , args => Collections.Count(args , count));
            dict.AddFunction(distinct , args => Collections.Distinct(args , distinct));
            dict.AddFunction(binary_search , args => Collections.BinarySearch(args , binary_search));
            dict.AddFunction(fill , args => Collections.Fill(args , fill));
            dict.AddFunction(min , args => Collections.Extreme(args , -1 , min));
            dict.AddFunction(max , args => Collections.Extreme(args , 1 , max));
            dict.AddFunction(sum , args => Collections.Sum(args , sum));
            dict.AddFunction(range , args => Collections.Range(args , array , range));
        }
        dict.AddFunction("list.push" , args => Collections.Push(args , "list.push"));
        dict.AddFunction("list.pop" , args => Collections.Pop(args , "list.pop"));
        dict.AddFunction("list.insert" , args => Collections.Insert(args , "list.insert"));
        dict.AddFunction("list.removeat" , args => Collections.RemoveAt(args , "list.removeat"));
        dict.AddFunction("list.remove" , args => Collections.Remove(args , "list.remove"));
        dict.AddFunction("list.clear" , args => Collections.Clear(args , "list.clear"));
    }
    public static void AddConsole(VariableDictionary dict)
    {
        dict.AddFunction("console.write" ,args => {
            if (args.Length is 0)
                Console.Write(string.Empty);
            else if (args.Length is 1)
                Console.Write(args[0] ?? "null");
            else
            {
                Console.Write((string?)args[0] ?? "null" , args);
            }
            return null;
        });
        dict.AddFunction("console.writeline" , args => {
            if (args.Length is 0)
                Console.WriteLine();
            else if (args.Length is 1)
                Console.WriteLine(args[0]);
            else
                Console.WriteLine((string)(args[0] ?? throw null_exception) , args);
            return null;
        });
        dict.AddFunction("console.readline" ,args => {
            return Console.ReadLine();
        });
        dict.AddFunction("console.read" ,args => {
            return (char)Console.Read();
        });
        if (OperatingSystem.IsWindows())
        {
            dict.AddVariable("console.title" , () => Console.Title , v => Console.Title = (string)(v ?? throw null_exception));
        }
        dict.AddFunction("console.clear" ,args => {
            Console.Clear();
            return null;
        });
    }
    public static void AddMath(VariableDictionary dict)
    {
        dict.AddConstantVariable("math.pi" , () => Math.PI);
        dict.AddConstantVariable("math.nan" , () => double.NaN);
        dict.AddConstantVariable("math.tau" , () => Math.Tau);
        dict.AddPureFunction("math.sin" , args => {
            if (args.Length is 0)
                throw new JyunoException("사인 값을 구할 인자가 필요합니다.");
            return Math.Sin(real_argument(args[0]));
        });
        dict.AddPureFunction("math.cos" , args => {
            if (args.Length is 0)
                throw new JyunoException("코사인 값을 구할 인자가 필요합니다.");
            return Math.Cos(real_argument(args[0]));
        });
        dict.AddPureFunction("math.tan" , args => {
            if (args.Length is 0)
                throw new JyunoException("탄젠트 값을 구할 인자가 필요합니다.");
            return Math.Tan(real_argument(args[0]));
        });
        dict.AddPureFunction("math.pow" , args => {
            if (args.Length < 2)
                throw new JyunoException("거듭제곱에 필요한 밑과 지수가 누락되었습니다.");
            return Math.Pow(real_argument(args[0]) , real_argument(args[1]));
        });
        dict.AddPureFunction("math.log" , args => {
            if (args.Length < 1)
                throw new JyunoException("로그에 필요한 진수가 누락되었습니다.");
            if (args.Length is 1)
                return Math.Log10(real_argument(args[0]));
            return Math.Log(real_argument(args[0]) , real_argument(args[1]));
        });
        dict.AddPureFunction("math.log2" , args => {
            if (args.Length < 1)
                throw new JyunoException("로그에 필요한 진수가 누락되었습니다.");
            return Math.Log2(real_argument(args[0]));
        });
        //난수는 호출할때마다 값이 달라지므로 미리 계산하지 않는 일반 함수로 추가
        //math.random: 빠른 난수 (System.Random), math.csprng: 암호학적으로 안전한 난수 (RandomNumberGenerator)
        dict.AddFunction("math.random" , args => RandomValues.Fast.Next(args , "math.random"));
        dict.AddFunction("math.csprng" , args => RandomValues.Secure.Next(args , "math.csprng"));
        dict.AddPureFunction("math.abs" , args => {
            if (args.Length is 0)
                throw new JyunoException("절댓값을 구할 값이 필요합니다.");
            return Math.Abs((dynamic)(args[0] ?? throw null_exception));
        });
    }
    public static void AddFile(VariableDictionary dict)
    {
        dict.AddFunction("file.exist" , args => {
            if (args.Length is 0 || (args[0] ?? throw null_exception) is not string)
                throw new JyunoException("파일 경로를 입력해주세요.");
            return File.Exists((string?)(args[0] ?? throw null_exception));
        });
        dict.AddFunction("file.read" , args => {
            if (args.Length is 0 || (args[0] ?? throw null_exception) is not string)
                throw new JyunoException("파일의 경로를 입력해야 합니다.");
            return File.Exists((string)(args[0] ?? throw null_exception)) ? File.ReadAllText((string)(args[0] ?? throw null_exception)) : null;
        });
        dict.AddFunction("directory.now" , args => {
            return Directory.GetCurrentDirectory();
        });
        dict.AddFunction("directory.exist" , args => {
            if (args.Length is 0 || (args[0] ?? throw null_exception) is not string)
                throw new JyunoException("경로를 입력해주세요.");
            return Directory.Exists((string?)(args[0] ?? throw null_exception));
        });
        dict.AddFunction("file.write" , args => {
            if (args.Length < 2)
                throw new JyunoException("파일의 경로, 그리고 내용을 입력해야합니다.");
            File.WriteAllText((string)(args[0] ?? throw null_exception) , (string?)(args[1] ?? throw null_exception));
            return null;
        });
        dict.AddFunction("directory.files" , args => {
            if (args.Length > 0 && (args[0] ?? throw null_exception) is string path)
            {
                if (!Directory.Exists((string?)(args[0] ?? throw null_exception)))
                    return false;
            }
            else
            {
                path = Directory.GetCurrentDirectory();
            }
            return Directory.GetFiles(path);
        });
        dict.AddFunction("directory.list" , args => {
            if (args.Length > 0 && (args[0] ?? throw null_exception) is string path)
            {
                if (!Directory.Exists(path))
                    return false;
            } else
            {
                path = Directory.GetCurrentDirectory();
            }
            return Directory.GetDirectories(path);
        });
        dict.AddFunction("directory.move" , args => {
            if (args.Length >= 2 && args[0] is string source && args[1] is string target)
            {
                Directory.Move(source , target);
                return true;
            }
            throw new JyunoException("기존 경로, 그리고 이동할 새 경로를 입력하지 않았습니다.");
        });
        dict.AddFunction("file.remove" , args => {
            if (args.Length is 0)
            {
                throw new JyunoException("제거할 파일의 경로를 입력해야 합니다.");
            }
            string path = (string)(args[0] ?? throw null_exception);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        });
    }

    //─── 사칙연산/비교/논리 (연산자 +, -, *, /, %, ==, <, >, &, |, ! 와 같은 계산) ───
    //첫 인자가 그 연산자를 다시 정의한 구조체 값이라면 구조체 함수를 호출함 (1.6.0부터)

    //첫 인자가 연산자를 다시 정의한 구조체 값이라면, 첫 두 인자를 그 연산자로 계산한 값을 첫 인자로 바꾼 인자들 (왼쪽부터 차례대로 계산하기 위함)
    static bool overloaded(object?[] args , string symbol , out object?[] rest)
    {
        rest = args;
        if (args.Length < 2 || args[0] is not StructValue structure || !structure.TryOperator(symbol , new[] { args[1] } , out object? result))
            return false;
        rest = new object?[args.Length - 1];
        rest[0] = result;
        Array.Copy(args , 2 , rest , 1 , args.Length - 2);
        return true;
    }

    internal static object? Add(object?[] args)
    {
        if (args.Length is 0)
            throw new JyunoException("덧셈을 할 값들을 넣지 않았습니다.");
        if (overloaded(args , "+" , out object?[] rest))
            return rest.Length is 1 ? rest[0] : Add(rest);
        //첫 인자가 배열/리스트라면, 나머지 인자를 이어붙인 새 컬렉션 (컬렉션 인자는 한번만 펼침)
        if (args[0] is System.Collections.IList collection)
            return Collections.Add(collection , args , 1);
        if (args.Length is 1)
            return args[0];
        //문자열이 하나라도 있다면, 모두 문자열 연산
        foreach (object? arg in args)
        {
            if (arg is string)
                return string.Concat(args);
        }
        //그렇지 않으면 수 연산
        object? sum = args[0];
        for(int i=1 ;i<args.Length; i++)
        {
            sum = Values.Add(sum , args[i]);
        }
        return sum;
    }

    internal static object? Sub(object?[] args)
    {
        if (args.Length < 2)
            throw new JyunoException("뺄셈 할 값 2개가 필요합니다.");
        if (args[0] is StructValue structure && structure.TryOperator("-" , new[] { args[1] } , out object? result))
            return result;
        //첫 인자가 배열/리스트라면 차집합 (두번째 인자에 있는 원소를 모두 제거한 새 컬렉션)
        if (args[0] is System.Collections.IList collection)
            return Collections.Sub(collection , args[1]);
        if (args[0] is string str)
            return str.Replace((string)(args[1] ?? throw null_exception), string.Empty);
        //정수끼리는 정수로, 실수가 섞여있다면 실수로 계산
        if (Values.TryGetInteger(args[0] , out long int_a) && Values.TryGetInteger(args[1] , out long int_b))
            return int_a - int_b;
        if (Values.TryGetReal(args[0] , out double double_a) && Values.TryGetReal(args[1] , out double double_b))
            return double_a - double_b;
        throw new JyunoException("숫자 또는 문자열 이외에는 뺄셈 연산을 할수 없습니다.");
    }

    internal static object? Mul(object?[] args)
    {
        if (args.Length is 0)
            return 1L;
        if (overloaded(args , "*" , out object?[] rest))
            return rest.Length is 1 ? rest[0] : Mul(rest);
        if (args[0] is string str)
        {
            if (args.Length is 1)
                return str;
            if (!Values.TryGetInteger(args[1] ?? throw null_exception , out long count))
                throw new JyunoException("문자열을 반복할 횟수는 정수여야 합니다.");
            //음수 횟수는 0번 반복한 것으로 취급
            return string.Concat(Enumerable.Repeat(str , (int)Math.Clamp(count , 0 , int.MaxValue)));
        }
        object? mul = 1L;
        for(int i=0 ;i<args.Length;i++)
            mul = Values.Multiply(mul , args[i] ?? throw null_exception);
        return mul;
    }

    internal static object? Div(object?[] args)
    {
        if (args.Length < 2)
            throw new JyunoException("나눗셈 할 값 2개가 필요합니다.");
        if (args[0] is StructValue structure && structure.TryOperator("/" , new[] { args[1] } , out object? result))
            return result;
        //문자열을 나누면 구분자로 자른 문자열 배열 (C#의 a.Split(b))
        if (args[0] is string text)
            return text.Split(Convert.ToString(args[1] ?? throw null_exception , CultureInfo.InvariantCulture) ?? string.Empty);
        object divisor = args[1] ?? throw null_exception;
        if (Values.TryGetInteger(divisor , out long zero) && zero is 0)
            throw new JyunoException("0으로 나눌수 없습니다.");
        return Values.Divide(args[0] ?? throw null_exception , divisor);
    }

    internal static object? Mod(object?[] args)
    {
        if (args.Length < 2)
            throw new JyunoException("모듈로 연산 할 값 2개가 필요합니다.");
        if (args[0] is StructValue structure && structure.TryOperator("%" , new[] { args[1] } , out object? result))
            return result;
        object divisor = args[1] ?? throw null_exception;
        if (Values.TryGetInteger(divisor , out long zero) && zero is 0)
            throw new JyunoException("0으로 나눌수 없습니다.");
        return Values.Modulo(args[0] ?? throw null_exception , divisor);
    }

    internal static object? Equal(object?[] args)
    {
        if (args.Length < 2)
            throw new JyunoException("비교할 대상이 최소 2개 이상 있어야 합니다.");
        for(int i=1 ;i<args.Length;i++)
        {
            //구조체가 ==를 다시 정의했다면 Values.Same이 그 결과를 돌려줌
            if (!Values.Same(args[i-1] , args[i]))
                return false;
        }
        return true;
    }

    internal static object? Less(object?[] args) => ordered(args , "less" , Overloads.Less , order => order < 0);
    internal static object? Greater(object?[] args) => ordered(args , "greater" , Overloads.Greater , order => order > 0);

    internal static object? And(object?[] args)
    {
        if (args.Length is 0)
            throw new JyunoException("and 연산을 할 값이 필요합니다.");
        if (overloaded(args , "&" , out object?[] rest))
            return rest.Length is 1 ? rest[0] : And(rest);
        if (bitwise(args , out long result , (a , b) => a & b))
            return result;
        foreach (object? arg in args)
        {
            if (!Parser.IsTrue(arg))
                return false;
        }
        return true;
    }

    internal static object? Or(object?[] args)
    {
        if (args.Length is 0)
            throw new JyunoException("or 연산을 할 값이 필요합니다.");
        if (overloaded(args , "|" , out object?[] rest))
            return rest.Length is 1 ? rest[0] : Or(rest);
        if (bitwise(args , out long result , (a , b) => a | b))
            return result;
        foreach (object? arg in args)
        {
            if (Parser.IsTrue(arg))
                return true;
        }
        return false;
    }

    internal static object? Not(object?[] args)
    {
        if (args.Length is 0)
            throw new JyunoException("not 연산을 할 값이 필요합니다.");
        if (args[0] is StructValue structure && structure.TryOperator("!" , Array.Empty<object?>() , out object? result))
            return result;
        if (Values.TryGetInteger(args[0] , out long integer))
            return ~integer;
        return !Parser.IsTrue(args[0]);
    }

    //문자열 반복은 결과가 너무 길어질수 있으므로, 짧을 때만 미리 계산
    static bool fold_mul(object?[] args)
    {
        if (args.Length >= 2 && args[0] is string str && Values.TryGetInteger(args[1] , out long count))
            return count <= 0 || str.Length * Math.Min(count , 1 << 20) <= 4096;
        return true;
    }

    //less/greater: 이웃한 두 인자끼리 비교한 결과가 모두 조건을 만족하는지 (인자가 하나라면 항상 true)
    static bool ordered(object?[] args , string name , Func<object? , object? , bool?> overload , Func<int , bool> condition)
    {
        if (args.Length is 0)
            throw new JyunoException($"{name} 비교를 할 값이 필요합니다.");
        //앞에서 이미 false가 나왔더라도 비교할수 없는 값이 있다면 오류가 나도록 끝까지 비교
        bool result = true;
        for (int i = 1 ; i < args.Length ; i++)
        {
            //구조체가 <, >를 다시 정의했다면 그 결과
            if (overload(args[i - 1] , args[i]) is bool overloaded_result)
            {
                if (!overloaded_result)
                    result = false;
                continue;
            }
            if (Values.Compare(args[i - 1] , args[i]) is not int order || !condition(order))
                result = false;
        }
        return result;
    }

    //and/or: 인자가 모두 정수라면 비트 연산한 결과를 계산 (하나라도 정수가 아니라면 false)
    static bool bitwise(object?[] args , out long result , Func<long , long , long> operation)
    {
        result = 0;
        for (int i = 0 ; i < args.Length ; i++)
        {
            if (!Values.TryGetInteger(args[i] , out long integer))
                return false;
            result = i is 0 ? integer : operation(result , integer);
        }
        return true;
    }

    //수학 함수의 인자를 실수로 변환 (박싱된 long을 (double)로 캐스팅하면 예외가 나므로)
    static double real_argument(object? value)
    {
        if (Values.TryGetReal(value ?? throw null_exception , out double real))
            return real;
        throw new JyunoException($"'{value}'은/는 숫자가 아닙니다.");
    }

    //예외 객체를 공유하면 스택 트레이스가 덮어써지므로 매번 새로 만듦
    public static JyunoException null_exception => new("null을 처리할수 없습니다.");
}
