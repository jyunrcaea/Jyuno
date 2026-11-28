using Jyuno.Analysis;

namespace Jyuno.Tests;

//file.*, directory.*, path.* 명령어 (Runtime.AddJyunoCommandType.File)
public sealed class FileFunctionTests : IDisposable
{
    readonly string root;

    public FileFunctionTests()
    {
        root = Path.Combine(Path.GetTempPath() , "jyuno-file-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root , true);
    }

    static object? Eval(string expression) => new Runtime(Runtime.AddJyunoCommandType.File).Create().ExecuteLine(expression);

    static object? Run(params string[] lines) => new Runtime(Runtime.AddJyunoCommandType.All).Create(lines).Run();

    static string Lit(string value) => "'" + value.Replace("\\" , "\\\\").Replace("'" , "\\'") + "'";

    [Fact]
    public void FileCommands_AreOnlyAddedWithFileFlag()
    {
        var defaultGlobals = new Runtime().GetGlobals();
        Assert.False(defaultGlobals.ContainsKey("file.exist"));
        Assert.False(defaultGlobals.ContainsKey("directory.create"));
        Assert.False(defaultGlobals.ContainsKey("path.combine"));
        Assert.False(defaultGlobals.ContainsKey("path.sep"));

        var fileGlobals = new Runtime(Runtime.AddJyunoCommandType.File).GetGlobals();
        Assert.Equal(GlobalKind.Function , fileGlobals["file.exist"].Kind);
        Assert.Equal(GlobalKind.Function , fileGlobals["directory.create"].Kind);
        Assert.Equal(GlobalKind.Function , fileGlobals["path.combine"].Kind);
        Assert.Equal(GlobalKind.Constant , fileGlobals["path.sep"].Kind);
        Assert.Equal(Path.DirectorySeparatorChar.ToString() , fileGlobals["path.sep"].Value);
    }

    [Fact]
    public void Path_FunctionsAndConstants_WorkAsExpected()
    {
        Assert.Equal(Path.DirectorySeparatorChar.ToString() , Eval("path.sep"));
        Assert.Equal(Path.AltDirectorySeparatorChar.ToString() , Eval("path.altsep"));
        Assert.Equal(Path.PathSeparator.ToString() , Eval("path.pathsep"));
        Assert.Equal(Path.VolumeSeparatorChar.ToString() , Eval("path.volsep"));

        Assert.Equal(Path.Combine("a" , "b" , "c.txt") , Eval("path.combine 'a' 'b' 'c.txt'"));
        Assert.Equal(Path.Join("a" , "/b" , "c") , Eval("path.join 'a' '/b' 'c'"));
        Assert.Equal("c.txt" , Eval($"path.filename {Lit(Path.Combine("a" , "b" , "c.txt"))}"));
        Assert.Equal("c" , Eval($"path.filenamewithoutext {Lit(Path.Combine("a" , "b" , "c.txt"))}"));
        Assert.Equal(".txt" , Eval($"path.extension {Lit(Path.Combine("a" , "b" , "c.txt"))}"));
        Assert.Equal(Path.Combine("a" , "b") , Eval($"path.directory {Lit(Path.Combine("a" , "b" , "c.txt"))}"));
        Assert.Equal(Path.ChangeExtension("report.txt" , ".md") , Eval("path.changeext 'report.txt' '.md'"));
        Assert.Equal(Path.ChangeExtension("report.txt" , null) , Eval("path.changeext 'report.txt' null"));
        Assert.Equal(true , Eval("path.hasext 'report.txt'"));
        Assert.Equal(false , Eval("path.hasext 'report'"));

        string rooted = Path.Combine(root , "sub");
        Assert.Equal(true , Eval($"path.isrooted {Lit(rooted)}"));
        Assert.Equal(false , Eval("path.isrooted 'sub'"));
        Assert.Equal(true , Eval($"path.isfullyqualified {Lit(rooted)}"));
        Assert.Equal(Path.GetPathRoot(rooted) , Eval($"path.root {Lit(rooted)}"));
        Assert.Equal(Path.GetFullPath("sub" , root) , Eval($"path.full 'sub' {Lit(root)}"));
        Assert.Equal(Path.GetRelativePath(root , Path.Combine(root , "a" , "b")) , Eval($"path.relative {Lit(root)} {Lit(Path.Combine(root , "a" , "b"))}"));

        string withSep = rooted + Path.DirectorySeparatorChar;
        Assert.Equal(true , Eval($"path.endsinsep {Lit(withSep)}"));
        Assert.Equal(false , Eval($"path.endsinsep {Lit(rooted)}"));
        Assert.Equal(rooted , Eval($"path.trimsep {Lit(withSep)}"));

        Assert.Equal(Path.GetTempPath() , Eval("path.temp"));
        string tempFile = Assert.IsType<string>(Eval("path.tempfile"));
        try
        {
            Assert.True(File.Exists(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
        string randomName = Assert.IsType<string>(Eval("path.randomname"));
        Assert.False(string.IsNullOrEmpty(randomName));
    }

    [Fact]
    public void File_TextLinesAndBytes_WorkAsExpected()
    {
        string textPath = Path.Combine(root , "sample.txt");
        Assert.Equal(false , Eval($"file.exist {Lit(textPath)}"));
        Assert.Null(Eval($"file.read {Lit(textPath)}"));
        Assert.Null(Eval($"file.readlines {Lit(textPath)}"));
        Assert.Null(Eval($"file.readbytes {Lit(textPath)}"));
        Assert.Equal(false , Eval($"file.remove {Lit(textPath)}"));

        Eval($"file.write {Lit(textPath)} 'hello'");
        Assert.Equal(true , Eval($"file.exist {Lit(textPath)}"));
        Assert.Equal("hello" , Eval($"file.read {Lit(textPath)}"));
        Eval($"file.append {Lit(textPath)} ' world'");
        Assert.Equal("hello world" , Eval($"file.read {Lit(textPath)}"));
        Assert.Equal((long)new FileInfo(textPath).Length , Eval($"file.size {Lit(textPath)}"));

        string linesPath = Path.Combine(root , "lines.txt");
        Eval($"file.writelines {Lit(linesPath)} (array 'first' 'second')");
        Eval($"file.appendlines {Lit(linesPath)} 'third' 4");
        Assert.Equal(new[] { "first" , "second" , "third" , "4" } , Eval($"file.readlines {Lit(linesPath)}"));

        string bytesPath = Path.Combine(root , "data.bin");
        Eval($"file.writebytes {Lit(bytesPath)} (array 0 65 255)");
        Eval($"file.appendbytes {Lit(bytesPath)} 10 20");
        Assert.Equal(new long[] { 0 , 65 , 255 , 10 , 20 } , Eval($"file.readbytes {Lit(bytesPath)}"));

        Assert.Equal(true , Eval($"file.remove {Lit(bytesPath)}"));
        Assert.Equal(false , Eval($"file.exist {Lit(bytesPath)}"));
    }

    [Fact]
    public void File_CopyMoveReplaceTouchTimesReadonly_WorkAsExpected()
    {
        string src = Path.Combine(root , "a.txt");
        string copy = Path.Combine(root , "b.txt");
        string moved = Path.Combine(root , "c.txt");
        string backup = Path.Combine(root , "c.bak");

        Eval($"file.touch {Lit(src)}");
        Assert.True(File.Exists(src));
        Assert.Equal(0L , Eval($"file.size {Lit(src)}"));
        Eval($"file.write {Lit(src)} 'v1'");

        Assert.Equal(true , Eval($"file.copy {Lit(src)} {Lit(copy)}"));
        Assert.Equal("v1" , File.ReadAllText(copy));
        Assert.Throws<JyunoException>(() => Eval($"file.copy {Lit(src)} {Lit(copy)}"));
        Eval($"file.write {Lit(src)} 'v2'");
        Assert.Equal(true , Eval($"file.copy {Lit(src)} {Lit(copy)} true"));
        Assert.Equal("v2" , File.ReadAllText(copy));

        Assert.Equal(true , Eval($"file.move {Lit(copy)} {Lit(moved)}"));
        Assert.False(File.Exists(copy));
        Assert.Equal("v2" , File.ReadAllText(moved));

        Eval($"file.write {Lit(src)} 'v3'");
        Assert.Equal(true , Eval($"file.replace {Lit(src)} {Lit(moved)} {Lit(backup)}"));
        Assert.False(File.Exists(src));
        Assert.Equal("v3" , File.ReadAllText(moved));
        Assert.Equal("v2" , File.ReadAllText(backup));

        long created = Assert.IsType<long>(Eval($"file.createdtime {Lit(moved)}"));
        long modified = Assert.IsType<long>(Eval($"file.modifiedtime {Lit(moved)}"));
        long accessed = Assert.IsType<long>(Eval($"file.accessedtime {Lit(moved)}"));
        Assert.True(created > 0 && modified > 0 && accessed > 0);

        Eval($"file.setreadonly {Lit(moved)} true");
        Assert.Equal(true , Eval($"file.isreadonly {Lit(moved)}"));
        Eval($"file.setreadonly {Lit(moved)} false");
        Assert.Equal(false , Eval($"file.isreadonly {Lit(moved)}"));
    }

    [Fact]
    public void Directory_Operations_WorkAsExpected()
    {
        string nested = Path.Combine(root , "one" , "two");
        Assert.Equal(false , Eval($"directory.exist {Lit(nested)}"));
        Assert.Equal(Path.GetFullPath(nested) , Eval($"directory.create {Lit(nested)}"));
        Assert.Equal(true , Eval($"directory.exist {Lit(nested)}"));
        Assert.Equal(true , Eval($"directory.isempty {Lit(nested)}"));
        Assert.Equal(Path.Combine(root , "one") , Eval($"directory.parent {Lit(nested)}"));
        Assert.Equal(Directory.GetDirectoryRoot(nested) , Eval($"directory.root {Lit(nested)}"));

        string fileA = Path.Combine(root , "one" , "a.txt");
        string fileB = Path.Combine(nested , "b.log");
        File.WriteAllText(fileA , "a");
        File.WriteAllText(fileB , "b");
        Assert.Equal(false , Eval($"directory.isempty {Lit(Path.Combine(root , "one"))}"));

        Assert.Equal(new[] { fileA } , Eval($"directory.files {Lit(Path.Combine(root , "one"))}"));
        Assert.Equal(new[] { nested } , Eval($"directory.list {Lit(Path.Combine(root , "one"))}"));
        string[] allTxt = Assert.IsType<string[]>(Eval($"directory.files {Lit(Path.Combine(root , "one"))} '*.txt' true"));
        Assert.Equal(new[] { fileA } , allTxt);
        string[] allEntries = Assert.IsType<string[]>(Eval($"directory.entries {Lit(Path.Combine(root , "one"))} '*' true"));
        Assert.Contains(fileA , allEntries);
        Assert.Contains(fileB , allEntries);
        Assert.Contains(nested , allEntries);
        Assert.Equal(false , Eval($"directory.files {Lit(Path.Combine(root , "missing"))}"));

        string copied = Path.Combine(root , "copied");
        Assert.Equal(true , Eval($"directory.copy {Lit(Path.Combine(root , "one"))} {Lit(copied)}"));
        Assert.Equal("b" , File.ReadAllText(Path.Combine(copied , "two" , "b.log")));
        Assert.Throws<JyunoException>(() => Eval($"directory.copy {Lit(Path.Combine(root , "one"))} {Lit(Path.Combine(root , "one" , "sub"))}"));

        string moved = Path.Combine(root , "moved");
        Assert.Equal(true , Eval($"directory.move {Lit(copied)} {Lit(moved)}"));
        Assert.False(Directory.Exists(copied));
        Assert.True(Directory.Exists(moved));

        long created = Assert.IsType<long>(Eval($"directory.createdtime {Lit(moved)}"));
        long modified = Assert.IsType<long>(Eval($"directory.modifiedtime {Lit(moved)}"));
        long accessed = Assert.IsType<long>(Eval($"directory.accessedtime {Lit(moved)}"));
        Assert.True(created > 0 && modified > 0 && accessed > 0);

        Assert.Throws<JyunoException>(() => Eval($"directory.remove {Lit(moved)}"));
        Assert.Equal(true , Eval($"directory.remove {Lit(moved)} true"));
        Assert.Equal(false , Eval($"directory.remove {Lit(moved)}"));
    }

    [Fact]
    public void Directory_Copy_KeepsDirectoryLinks()
    {
        //상위 디렉토리를 가리키는 링크를 따라가지 않고 링크로 복사 (따라가면 OS의 한도까지 같은 내용을 복사하다가 오류)
        string source = Path.Combine(root , "linked");
        Directory.CreateDirectory(Path.Combine(source , "inner"));
        File.WriteAllText(Path.Combine(source , "f.txt") , "f");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(source , "inner" , "up") , "..");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; //Windows에서 심볼릭 링크를 만들 권한이 없음
        }
        string target = Path.Combine(root , "linked-copy");
        Assert.Equal(true , Eval($"directory.copy {Lit(source)} {Lit(target)}"));
        Assert.Equal("f" , File.ReadAllText(Path.Combine(target , "f.txt")));
        Assert.Equal(".." , new DirectoryInfo(Path.Combine(target , "inner" , "up")).LinkTarget);
        Assert.Single(Directory.GetFileSystemEntries(Path.Combine(target , "inner")));
    }

    [Fact]
    public void Directory_CdAndNow_RoundTrip()
    {
        string original = Directory.GetCurrentDirectory();
        try
        {
            string expected = new DirectoryInfo(root).FullName;
            Assert.Equal(expected , Eval($"directory.cd {Lit(root)}"));
            Assert.Equal(expected , Eval("directory.now"));
            Assert.Equal(Path.GetRelativePath(expected , Path.Combine(expected , "child.txt")) , Eval("path.relative 'child.txt'"));
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    [Fact]
    public void FileCommands_ValidateArgumentsAndMemoryLimit()
    {
        Assert.Throws<JyunoException>(() => Eval("path.combine"));
        Assert.Throws<JyunoException>(() => Eval("path.combine 'a' 1"));
        Assert.Throws<JyunoException>(() => Eval("path.combine null"));
        Assert.Throws<JyunoException>(() => Eval("file.writebytes 'x' -1"));
        Assert.Throws<JyunoException>(() => Eval("file.writebytes 'x' 256"));
        Assert.Throws<JyunoException>(() => Eval("file.writebytes 'x' 'a'"));
        Assert.Throws<JyunoException>(() => Eval($"file.size {Lit(Path.Combine(root , "none"))}"));
        Assert.Throws<JyunoException>(() => Eval($"file.modifiedtime {Lit(Path.Combine(root , "none"))}"));
        Assert.Throws<JyunoException>(() => Eval($"directory.isempty {Lit(Path.Combine(root , "none"))}"));
        Assert.Throws<JyunoException>(() => Eval($"directory.modifiedtime {Lit(Path.Combine(root , "none"))}"));

        string bigPath = Path.Combine(root , "big.txt");
        File.WriteAllText(bigPath , new string('a' , 2_000_000));
        Interpreter limited = new Runtime(Runtime.AddJyunoCommandType.File).Create(new[] { $"x = file.read {Lit(bigPath)}" });
        limited.MemoryLimit = 1024 * 1024;
        Assert.Throws<JyunoLimitException>(() => limited.Run());

        object? result = Run(
            $"dir = directory.create (path.combine {Lit(root)} 'script-dir')" ,
            "p = path.combine dir 'note.txt'" ,
            "file.write p 'jyuno'" ,
            "return (file.read p)");
        Assert.Equal("jyuno" , result);
    }
}
