using System.Collections;
using Jyuno.Compiler;

namespace Jyuno.Language;

//file.*, directory.*, path.* 명령어: C#의 System.IO(File, Directory, Path) 기능을 Jyuno에서 쓸수 있게 옮긴 것
//Runtime.AddJyunoCommandType.File이 켜진 런타임에서만 추가됨
internal static class Files
{
    public static void Add(VariableDictionary dict)
    {
        add_path(dict);
        add_file(dict);
        add_directory(dict);
    }

    static void add_path(VariableDictionary dict)
    {
        void add(string name , Func<object?[] , string , object?> function)
        {
            string full = "path." + name;
            dict.AddFunction(full , args => function(args , full));
        }

        dict.AddConstantVariable("path.sep" , () => Path.DirectorySeparatorChar.ToString());
        dict.AddConstantVariable("path.altsep" , () => Path.AltDirectorySeparatorChar.ToString());
        dict.AddConstantVariable("path.pathsep" , () => Path.PathSeparator.ToString());
        dict.AddConstantVariable("path.volsep" , () => Path.VolumeSeparatorChar.ToString());

        add("combine" , (args , name) => {
            need(args , 1 , name);
            string[] parts = text_array(args , name);
            return io(() => Path.Combine(parts));
        });
        add("join" , (args , name) => {
            need(args , 1 , name);
            string[] parts = text_array(args , name);
            return io(() => Path.Join(parts));
        });
        add("filename" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Path.GetFileName(path));
        });
        add("filenamewithoutext" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Path.GetFileNameWithoutExtension(path));
        });
        add("extension" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Path.GetExtension(path));
        });
        add("directory" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Path.GetDirectoryName(path));
        });
        add("root" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Path.GetPathRoot(path));
        });
        add("full" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            if (args.Length > 1)
            {
                string base_path = text(args , 1 , name);
                return io(() => Path.GetFullPath(path , base_path));
            }
            return io(() => Path.GetFullPath(path));
        });
        add("relative" , (args , name) => {
            need(args , 1 , name);
            if (args.Length is 1)
            {
                string target = text(args , 0 , name);
                return io(() => Path.GetRelativePath(Directory.GetCurrentDirectory() , target));
            }
            string relative_to = text(args , 0 , name);
            string path = text(args , 1 , name);
            return io(() => Path.GetRelativePath(relative_to , path));
        });
        add("changeext" , (args , name) => {
            need(args , 2 , name);
            string path = text(args , 0 , name);
            string? ext = args[1] is null ? null : text(args , 1 , name);
            return io(() => Path.ChangeExtension(path , ext));
        });
        add("hasext" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Path.HasExtension(path));
        });
        add("isrooted" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Path.IsPathRooted(path));
        });
        add("isfullyqualified" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Path.IsPathFullyQualified(path));
        });
        add("endsinsep" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Path.EndsInDirectorySeparator(path));
        });
        add("trimsep" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Path.TrimEndingDirectorySeparator(path));
        });
        add("temp" , (_ , _) => io(() => Path.GetTempPath()));
        add("tempfile" , (_ , _) => io(() => Path.GetTempFileName()));
        add("randomname" , (_ , _) => Path.GetRandomFileName());
    }

    static void add_file(VariableDictionary dict)
    {
        void add(string name , Func<object?[] , string , object?> function)
        {
            string full = "file." + name;
            dict.AddFunction(full , args => function(args , full));
        }

        add("exist" , (args , _) => {
            if (args.Length is 0 || (args[0] ?? throw JyunoCommands.null_exception) is not string path)
                throw new JyunoException("파일 경로를 입력해주세요.");
            return File.Exists(path);
        });
        add("read" , (args , _) => {
            if (args.Length is 0 || (args[0] ?? throw JyunoCommands.null_exception) is not string path)
                throw new JyunoException("파일의 경로를 입력해야 합니다.");
            if (!File.Exists(path))
                return null;
            return io(() => {
                long length = new FileInfo(path).Length;
                Interpreter.ReserveString(length);
                string content = File.ReadAllText(path);
                return Interpreter.Hold(content , content.Length * 2L);
            });
        });
        add("write" , (args , _) => {
            if (args.Length < 2)
                throw new JyunoException("파일의 경로, 그리고 내용을 입력해야합니다.");
            if ((args[0] ?? throw JyunoCommands.null_exception) is not string path)
                throw new JyunoException("파일의 경로는 문자열이어야 합니다.");
            string content = Values.Text(args[1] ?? throw JyunoCommands.null_exception);
            io(() => File.WriteAllText(path , content));
            return null;
        });
        add("append" , (args , name) => {
            need(args , 2 , name);
            string path = text(args , 0 , name);
            string content = Values.Text(args[1] ?? throw JyunoCommands.null_exception);
            io(() => File.AppendAllText(path , content));
            return null;
        });
        add("remove" , (args , _) => {
            if (args.Length is 0)
                throw new JyunoException("제거할 파일의 경로를 입력해야 합니다.");
            if ((args[0] ?? throw JyunoCommands.null_exception) is not string path)
                throw new JyunoException("제거할 파일의 경로는 문자열이어야 합니다.");
            if (!File.Exists(path))
                return false;
            io(() => File.Delete(path));
            return true;
        });
        add("readlines" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            if (!File.Exists(path))
                return null;
            return io(() => {
                long length = new FileInfo(path).Length;
                Interpreter.Reserve(length * 2L + 64);
                string[] lines = File.ReadAllLines(path);
                return Interpreter.Hold(lines , length * 2L + lines.Length * 32L);
            });
        });
        add("writelines" , (args , name) => {
            need(args , 2 , name);
            string path = text(args , 0 , name);
            string[] lines = lines_arg(args);
            io(() => File.WriteAllLines(path , lines));
            return null;
        });
        add("appendlines" , (args , name) => {
            need(args , 2 , name);
            string path = text(args , 0 , name);
            string[] lines = lines_arg(args);
            io(() => File.AppendAllLines(path , lines));
            return null;
        });
        add("readbytes" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            if (!File.Exists(path))
                return null;
            return io(() => {
                long length = new FileInfo(path).Length;
                if (length > Array.MaxLength)
                    throw new JyunoException($"{name}: 파일이 너무 큽니다. ({length}바이트)");
                Interpreter.ReserveItems(length);
                byte[] raw = File.ReadAllBytes(path);
                long[] result = new long[raw.Length];
                for (int i = 0 ; i < raw.Length ; i++)
                {
                    Interpreter.Tick();
                    result[i] = raw[i];
                }
                return Interpreter.Hold(result , result.Length * 8L);
            });
        });
        add("writebytes" , (args , name) => {
            need(args , 2 , name);
            string path = text(args , 0 , name);
            byte[] bytes = bytes_arg(args , name);
            io(() => File.WriteAllBytes(path , bytes));
            return null;
        });
        add("appendbytes" , (args , name) => {
            need(args , 2 , name);
            string path = text(args , 0 , name);
            byte[] bytes = bytes_arg(args , name);
            io(() => {
                using FileStream stream = new(path , FileMode.Append , FileAccess.Write);
                stream.Write(bytes , 0 , bytes.Length);
            });
            return null;
        });
        add("copy" , (args , name) => {
            need(args , 2 , name);
            string source = text(args , 0 , name);
            string target = text(args , 1 , name);
            bool overwrite = args.Length > 2 && Parser.IsTrue(args[2]);
            io(() => File.Copy(source , target , overwrite));
            return true;
        });
        add("move" , (args , name) => {
            need(args , 2 , name);
            string source = text(args , 0 , name);
            string target = text(args , 1 , name);
            bool overwrite = args.Length > 2 && Parser.IsTrue(args[2]);
            io(() => File.Move(source , target , overwrite));
            return true;
        });
        add("replace" , (args , name) => {
            need(args , 2 , name);
            string source = text(args , 0 , name);
            string target = text(args , 1 , name);
            string? backup = args.Length > 2 && args[2] is not null ? text(args , 2 , name) : null;
            io(() => File.Replace(source , target , backup));
            return true;
        });
        add("size" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => new FileInfo(path).Length);
        });
        add("touch" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            io(() => {
                if (!File.Exists(path))
                {
                    using FileStream stream = File.Create(path);
                }
                else
                {
                    File.SetLastWriteTimeUtc(path , DateTime.UtcNow);
                }
            });
            return null;
        });
        add("createdtime" , (args , name) => file_time(args , name , File.GetCreationTimeUtc));
        add("modifiedtime" , (args , name) => file_time(args , name , File.GetLastWriteTimeUtc));
        add("accessedtime" , (args , name) => file_time(args , name , File.GetLastAccessTimeUtc));
        add("isreadonly" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => {
                if (!File.Exists(path))
                    throw new FileNotFoundException($"파일을 찾을수 없습니다: '{path}'" , path);
                return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
            });
        });
        add("setreadonly" , (args , name) => {
            need(args , 2 , name);
            string path = text(args , 0 , name);
            bool read_only = Parser.IsTrue(args[1]);
            io(() => {
                if (!File.Exists(path))
                    throw new FileNotFoundException($"파일을 찾을수 없습니다: '{path}'" , path);
                FileAttributes attributes = File.GetAttributes(path);
                File.SetAttributes(path , read_only ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
            });
            return null;
        });
    }

    static void add_directory(VariableDictionary dict)
    {
        void add(string name , Func<object?[] , string , object?> function)
        {
            string full = "directory." + name;
            dict.AddFunction(full , args => function(args , full));
        }

        add("now" , (_ , _) => io(() => Directory.GetCurrentDirectory()));
        add("cd" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => {
                Directory.SetCurrentDirectory(path);
                return Directory.GetCurrentDirectory();
            });
        });
        add("exist" , (args , _) => {
            if (args.Length is 0 || (args[0] ?? throw JyunoCommands.null_exception) is not string path)
                throw new JyunoException("경로를 입력해주세요.");
            return Directory.Exists(path);
        });
        add("create" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Directory.CreateDirectory(path).FullName);
        });
        add("remove" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            if (!Directory.Exists(path))
                return false;
            bool recursive = args.Length > 1 && Parser.IsTrue(args[1]);
            io(() => Directory.Delete(path , recursive));
            return true;
        });
        add("files" , (args , name) => list_entries(args , name , Directory.EnumerateFiles));
        add("list" , (args , name) => list_entries(args , name , Directory.EnumerateDirectories));
        add("entries" , (args , name) => list_entries(args , name , Directory.EnumerateFileSystemEntries));
        add("move" , (args , _) => {
            if (args.Length >= 2 && args[0] is string source && args[1] is string target)
            {
                io(() => Directory.Move(source , target));
                return true;
            }
            throw new JyunoException("기존 경로, 그리고 이동할 새 경로를 입력하지 않았습니다.");
        });
        add("copy" , (args , name) => {
            need(args , 2 , name);
            string source = text(args , 0 , name);
            string target = text(args , 1 , name);
            bool overwrite = args.Length > 2 && Parser.IsTrue(args[2]);
            io(() => copy_directory(source , target , overwrite , name));
            return true;
        });
        add("parent" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Directory.GetParent(path)?.FullName);
        });
        add("root" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => Directory.GetDirectoryRoot(path));
        });
        add("isempty" , (args , name) => {
            need(args , 1 , name);
            string path = text(args , 0 , name);
            return io(() => {
                if (!Directory.Exists(path))
                    throw new DirectoryNotFoundException($"디렉토리를 찾을수 없습니다: '{path}'");
                return !Directory.EnumerateFileSystemEntries(path).Any();
            });
        });
        add("createdtime" , (args , name) => directory_time(args , name , Directory.GetCreationTimeUtc));
        add("modifiedtime" , (args , name) => directory_time(args , name , Directory.GetLastWriteTimeUtc));
        add("accessedtime" , (args , name) => directory_time(args , name , Directory.GetLastAccessTimeUtc));
    }

    static void need(object?[] args , int count , string name)
    {
        if (args.Length < count)
            throw new JyunoException($"{name}: 인자가 {count}개 이상 필요합니다.");
    }

    static string text(object?[] args , int index , string name)
    {
        return args[index] switch {
            string s => s,
            null => throw JyunoCommands.null_exception,
            _ => throw new JyunoException($"{name}: '{Values.Show(args[index])}'은/는 문자열이 아닙니다.")
        };
    }

    static string[] text_array(object?[] args , string name)
    {
        string[] result = new string[args.Length];
        for (int i = 0 ; i < args.Length ; i++)
            result[i] = text(args , i , name);
        return result;
    }

    static string[] lines_arg(object?[] args)
    {
        if (args.Length is 2 && args[1] is IList list)
        {
            string[] lines = new string[list.Count];
            for (int i = 0 ; i < list.Count ; i++)
                lines[i] = Values.Text(list[i]);
            return lines;
        }
        string[] result = new string[args.Length - 1];
        for (int i = 1 ; i < args.Length ; i++)
            result[i - 1] = Values.Text(args[i]);
        return result;
    }

    static byte[] bytes_arg(object?[] args , string name)
    {
        if (args.Length is 2 && args[1] is IList list)
        {
            byte[] bytes = new byte[list.Count];
            for (int i = 0 ; i < list.Count ; i++)
                bytes[i] = to_byte(list[i] , name);
            return bytes;
        }
        byte[] result = new byte[args.Length - 1];
        for (int i = 1 ; i < args.Length ; i++)
            result[i - 1] = to_byte(args[i] , name);
        return result;
    }

    static byte to_byte(object? value , string name)
    {
        if (!Values.TryGetInteger(value ?? throw JyunoCommands.null_exception , out long integer))
            throw new JyunoException($"{name}: 바이트 값은 정수여야 합니다. ('{Values.Show(value)}')");
        if (integer < 0 || integer > byte.MaxValue)
            throw new JyunoException($"{name}: 바이트 값 {integer}은/는 0 ~ 255 범위를 벗어났습니다.");
        return (byte)integer;
    }

    static long file_time(object?[] args , string name , Func<string , DateTime> getter)
    {
        need(args , 1 , name);
        string path = text(args , 0 , name);
        return io(() => {
            if (!File.Exists(path))
                throw new FileNotFoundException($"파일을 찾을수 없습니다: '{path}'" , path);
            return new DateTimeOffset(DateTime.SpecifyKind(getter(path) , DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        });
    }

    static long directory_time(object?[] args , string name , Func<string , DateTime> getter)
    {
        need(args , 1 , name);
        string path = text(args , 0 , name);
        return io(() => {
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"디렉토리를 찾을수 없습니다: '{path}'");
            return new DateTimeOffset(DateTime.SpecifyKind(getter(path) , DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        });
    }

    static object list_entries(object?[] args , string name , Func<string , string , SearchOption , IEnumerable<string>> enumerate)
    {
        string path;
        if (args.Length > 0 && (args[0] ?? throw JyunoCommands.null_exception) is string given)
        {
            if (!Directory.Exists(given))
                return false;
            path = given;
        }
        else
        {
            path = Directory.GetCurrentDirectory();
        }
        string pattern = args.Length > 1 && args[1] is not null ? text(args , 1 , name) : "*";
        SearchOption option = args.Length > 2 && Parser.IsTrue(args[2]) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return io(() => {
            List<string> result = new();
            long chars = 0;
            foreach (string entry in enumerate(path , pattern , option))
            {
                Interpreter.Tick();
                result.Add(entry);
                chars += entry.Length;
                if ((result.Count & 0xFFF) == 0)
                    Interpreter.Reserve(chars * 2L + result.Count * 40L);
            }
            string[] array = result.ToArray();
            return Interpreter.Hold(array , chars * 2L + array.Length * 40L);
        });
    }

    static void copy_directory(string source , string target , bool overwrite , string name)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"디렉토리를 찾을수 없습니다: '{source}'");
        string full_source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        string full_target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(full_source , full_target , comparison) ||
            full_target.StartsWith(full_source + Path.DirectorySeparatorChar , comparison))
            throw new JyunoException($"{name}: 대상 디렉토리가 원본 디렉토리 안에 있습니다.");

        Stack<(string Source, string Target)> pending = new();
        pending.Push((full_source, full_target));
        while (pending.Count > 0)
        {
            Interpreter.Tick();
            (string current_source, string current_target) = pending.Pop();
            Directory.CreateDirectory(current_target);
            foreach (string file in Directory.EnumerateFiles(current_source))
            {
                Interpreter.Tick();
                string dest_file = Path.Combine(current_target , Path.GetFileName(file));
                File.Copy(file , dest_file , overwrite);
            }
            foreach (string sub in Directory.EnumerateDirectories(current_source))
            {
                Interpreter.Tick();
                string dest_sub = Path.Combine(current_target , Path.GetFileName(sub));
                pending.Push((sub, dest_sub));
            }
        }
    }

    static T io<T>(Func<T> action) => JyunoCommands.io(action);
    static void io(Action action) => JyunoCommands.io(action);
}
