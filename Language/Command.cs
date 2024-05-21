using Jyuno.Complier;

namespace Jyuno.Language;

public static class JyunoCommands
{
    public static void AddDefault(VariableDictionary dict)
    {
        dict.AddConstantVariable("null" , () => null);
        dict.AddConstantVariable("true", () => true);
        dict.AddConstantVariable("false",() => false);
        dict.AddFunction("int" , args => {
            if (args.Length is 0)
                return 0;
            if (args.Length == 1)
            {
                if (long.TryParse((string)(args[0] ?? throw null_exception) , out var value))
                    return value;
                return null;
            }
            return null;
        });
        dict.AddFunction("double" , args => {
            if (args.Length is 0)
                return 0.0;
            if (args.Length is 1)
            {
                if (double.TryParse((string)(args[0] ?? throw null_exception),out var value)) return value;
                return null;
            }
            return null;
        });
        dict.AddFunction("string" , args => args.Length is 0 ? string.Empty : Convert.ToString((args[0] ?? throw null_exception)));
        dict.AddFunction("add" , args => {
            if (args.Length is 0)
                throw new JyunoException("덧셈을 할 값들을 넣지 않았습니다.");
            if (args.Length is 1)
                return args[0];
            //문자열이 하나라도 있다면, 모두 문자열 연산
            if (args.Count(x => x is string) > 0)
            {
                return string.Concat(args);
            }
            //그렇지 않으면 수 연산
            dynamic? sum = args[0];
            for(int i=1 ;i<args.Length; i++)
            {
                sum += (dynamic?)args[i];
            }
            return sum;
        });
        dict.AddFunction("sub" , args => {
            if (args.Length < 2)
                throw new JyunoException("뺄셈 할 값 2개가 필요합니다.");
            if (args[0] is string str)
                return str.Replace((string)(args[1] ?? throw null_exception), string.Empty);
            if (args[0] is long int_a && args[1] is long int_b)
                return int_a - int_b;
            if (args[0] is double double_a && args[1] is double double_b)
                return double_a - double_b;
            throw new JyunoException("숫자 또는 문자열 이외에는 뺄셈 연산을 할수 없습니다.");
        });
        dict.AddFunction("mul" , args => {
            if (args.Length is 0)
                return 1;
            if (args[0] is string)
            {
                return string.Concat(Enumerable.Repeat((string)(args[0] ?? throw null_exception) , (int)(args[1] ?? throw null_exception)));
            }
            dynamic? mul = 1;
            for(int i=0 ;i<args.Length;i++)
                mul *= (dynamic)args[i];
            return mul;
        });
        dict.AddFunction("div" , args => {
            if (args.Length < 2)
                throw new JyunoException("나눗셈 할 값 2개가 필요합니다.");
            if ((args[1] ?? throw null_exception).Equals(0))
                throw new JyunoException("0으로 나눌수 없습니다.");
            return (dynamic)(args[0] ?? throw null_exception) / (dynamic)args[1];
        });
        dict.AddFunction("mod" , args => {
            if (args.Length < 2)
                throw new JyunoException("모듈로 연산 할 값 2개가 필요합니다.");
            return (dynamic)(args[0] ?? throw null_exception) % (dynamic)(args[1] ?? null_exception);
        });
        dict.AddFunction("equal" , args => {
            if (args.Length < 2)
                throw new JyunoException("비교할 대상이 최소 2개 이상 있어야 합니다.");
            for(int i=1 ;i<args.Length;i++)
            {
                if ((dynamic?)args[i-1] != (dynamic?)args[i])
                    return false;
            }
            return true;
        });
        dict.AddFunction("bool" , args => Parser.IsTrue(args));
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
        dict.AddFunction("math.sin" , args => {
            if (args.Length is 0)
                throw new JyunoException("사인 값을 구할 인자가 필요합니다.");
            return Math.Sin((double)(args[0] ?? throw null_exception));
        });
        dict.AddFunction("math.cos" , args => {
            if (args.Length is 0)
                throw new JyunoException("코사인 값을 구할 인자가 필요합니다.");
            return Math.Cos((double)(args[0] ?? throw null_exception));
        });
        dict.AddFunction("math.tan" , args => {
            if (args.Length is 0)
                throw new JyunoException("탄젠트 값을 구할 인자가 필요합니다.");
            return Math.Tan((double)(args[0] ?? throw null_exception));
        });
        dict.AddFunction("math.pow" , args => {
            if (args.Length < 1)
                throw new JyunoException("거듭제곱에 필요한 밑과 지수가 누락되었습니다.");
            return Math.Pow((double)(args[0] ?? throw null_exception) , (double)(args[1] ?? throw null_exception));
        });
        dict.AddFunction("math.log" , args => {
            if (args.Length < 0)
                throw new JyunoException("로그에 필요한 진수가 누락되었습니다.");
            if (args.Length is 1)
                return Math.Log10((double)(args[0] ?? throw null_exception));
            return Math.Log((double)(args[0] ?? throw null_exception) , (double)(args[1] ?? throw null_exception));
        });
        dict.AddFunction("math.log2" , args => {
            if (args.Length < 1)
                throw new JyunoException("로그에 필요한 진수가 누락되었습니다.");
            return Math.Log2((double)(args[0] ?? throw null_exception));
        });
        dict.AddFunction("math.abs" , args => {
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

    public static readonly JyunoException null_exception = new("null을 처리할수 없습니다.");
}
