namespace Jyuno.Language;

public class JyunoCommand
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
                if (long.TryParse((string)args[0] , out var value))
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
                if (double.TryParse((string)args[0],out var value)) return value;
                return null;
            }
            return null;
        });
        dict.AddFunction("string" , args => args.Length is 0 ? string.Empty : Convert.ToString(args[0]));
        dict.AddFunction("add" , args => {
            if (args.Length is 0)
                throw new JyunoException("덧셈을 할 값들을 넣지 않았습니다.");
            if (args.Length is 1)
                return args[0];
            if (args.Length is 2)
                return args[0] + args[1];
            dynamic? sum = args[0];
            for(int i=1 ;i<args.Length; i++)
            {
                sum += args[i];
            }
            return sum;
        });
        dict.AddFunction("sub" , args => {
            if (args.Length < 2)
                throw new JyunoException("뺄셈 할 값 2개가 필요합니다.");
            if (args[0] is string str)
                return str.Replace((string)args[1] , string.Empty);
            return args[0] - args[1];
        });
        dict.AddFunction("mul" , args => {
            if (args.Length is 0)
                return 1;
            if (args[0] is string)
            {
                return string.Concat(Enumerable.Repeat<string>((string)args[0] , (int)args[1]));
            }
            dynamic? mul = 1;
            for(int i=0 ;i<args.Length;i++)
                mul *= args[i];
            return mul;
        });
        dict.AddFunction("div" , args => {
            if (args.Length < 2)
                throw new JyunoException("나눗셈 할 값 2개가 필요합니다.");
            if (args[1] == 0)
                throw new JyunoException("0으로 나눌수 없습니다.");
            return args[0] / args[1];
        });
        dict.AddFunction("mod" , args => {
            if (args.Length < 2)
                throw new JyunoException("모듈로 연산 할 값 2개가 필요합니다.");
            return args[0] % args[1];
        });
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
                Console.Write(args[0] ?? "null" , args);
            }
            return null;
        });
        dict.AddFunction("console.writeline" , args => {
            if (args.Length is 0)
                Console.WriteLine();
            else if (args.Length is 1)
                Console.WriteLine(args[0]);
            else
                Console.WriteLine((string?)args[0] , args);
            return null;
        });
        dict.AddFunction("console.readline" ,args => {
            return Console.ReadLine();
        });
        dict.AddFunction("console.read" ,args => {
            return (char)Console.Read();
        });
        dict.AddVariable("console.title" ,() => Console.Title , v => Console.Title = v);
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
            return Math.Sin(args[0]);
        });
        dict.AddFunction("math.cos" , args => {
            if (args.Length is 0)
                throw new JyunoException("코사인 값을 구할 인자가 필요합니다.");
            return Math.Cos(args[0]);
        });
        dict.AddFunction("math.tan" , args => {
            if (args.Length is 0)
                throw new JyunoException("탄젠트 값을 구할 인자가 필요합니다.");
            return Math.Tan(args[0]);
        });
        dict.AddFunction("math.pow" , args => {
            if (args.Length < 1)
                throw new JyunoException("거듭제곱에 필요한 밑과 지수가 누락되었습니다.");
            return Math.Pow(args[0] , args[1]);
        });
        dict.AddFunction("math.log" , args => {
            if (args.Length < 0)
                throw new JyunoException("로그에 필요한 진수가 누락되었습니다.");
            if (args.Length is 1)
                return Math.Log10((double)args[0]);
            return Math.Log(args[0] , args[1]);
        });
        dict.AddFunction("math.log2" , args => {
            if (args.Length < 1)
                throw new JyunoException("로그에 필요한 진수가 누락되었습니다.");
            return Math.Log2(args[0]);
        });
    }
    public static void AddFile(VariableDictionary dict)
    {
        dict.AddFunction("file.exist" , args => {
            if (args.Length is 0 || args[0] is not string)
                throw new JyunoException("파일 경로를 입력해주세요.");
            return File.Exists(args[0]);
        });
        dict.AddFunction("file.read" , args => {
            if (args.Length is 0 || args[0] is not string)
                throw new JyunoException("파일의 경로를 입력해야 합니다.");
            return File.Exists(args[0]) ? File.ReadAllText(args[0]) : null;
        });
        dict.AddFunction("directory.now" , args => {
            return Directory.GetCurrentDirectory();
        });
        dict.AddFunction("directory.exist" , args => {
            if (args.Length is 0 || args[0] is not string)
                throw new JyunoException("경로를 입력해주세요.");
            return Directory.Exists(args[0]);
        });
        dict.AddFunction("file.write" , args => {
            if (args.Length < 2)
                throw new JyunoException("파일의 경로, 그리고 내용을 입력해야합니다.");
            if (File.Exists(args[0]))
            {
                File.WriteAllText(args[0] , args[1]);
                return true;
            }
            return false;
        });
        dict.AddFunction("directory.files" , args => {
            if (args[0] is string path)
            {
                if (!Directory.Exists(args[0]))
                    return false;
            }
            else
            {
                path = Directory.GetCurrentDirectory();
            }
            return Directory.GetFiles(path);
        });
        dict.AddFunction("directory.list" , args => {
            if (args[0] is string path)
            {
                if (!Directory.Exists(args[0]))
                    return false;
            } else
            {
                path = Directory.GetCurrentDirectory();
            }
            return Directory.GetDirectories(path);
        });
    }
    public static void AddRuntime(VariableDictionary dict, Runtime runtime)
    {
        dict.AddFunction("interpreter.count" , args => runtime.Interpreters.Count);
        dict.AddFunction("runtime.execute" , args => {
            if (args.Length is 0 || args[0] is not string)
                throw new JyunoException("실행할수 없습니다.");
            if (!File.Exists(args[0]))
                throw new JyunoException("존재하지 않는 파일입니다.");
            using(Interpreter new_interpret = runtime.Create(File.ReadAllLines((string)args[0])))
                return new_interpret.Run();
        });
    }
}
