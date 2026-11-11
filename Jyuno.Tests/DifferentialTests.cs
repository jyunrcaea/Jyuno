namespace Jyuno.Tests;

//[실험] 탈출 분석과 인라이닝의 차등 테스트 (1.5.3)
//Differential 폴더의 스크립트를 최적화를 끈 실행과 켠 실행(3가지 조합)으로 실행해서 출력/반환값/오류/마지막 상태가 같은지 확인
//스크립트는 탈출 분석과 인라이닝의 허점을 노리고 만든 것으로, 지시문과 host.* 함수는 DifferentialHarness.cs 참고
public class DifferentialTests
{
    static string Root => Path.Combine(AppContext.BaseDirectory , "Differential");

    public static IEnumerable<object[]> Cases() => Directory.GetFiles(Root , "*.jyuno" , SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(Root , path).Replace('\\' , '/'))
        .OrderBy(name => name , StringComparer.Ordinal)
        .Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Optimizations_DoNotChangeBehavior(string name)
    {
        string[] lines = DifferentialHarness.ReadLines(Path.Combine(Root , name));
        string run(bool escape , bool inline)
        {
            Task<string> task = Task.Run(() => DifferentialHarness.Run(lines , escape , inline));
            if (Task.WhenAny(task , Task.Delay(TimeSpan.FromSeconds(20))).GetAwaiter().GetResult() != task)
                throw new TimeoutException("스크립트가 끝나지 않습니다. (무한 루프?)");
            return task.GetAwaiter().GetResult();
        }
        string expected = run(false , false);
        Assert.Equal(expected , run(true , true));
        Assert.Equal(expected , run(true , false));
        Assert.Equal(expected , run(false , true));
    }

    [Fact]
    public void Cases_AreIncluded()
    {
        //스크립트가 출력 폴더에 복사되지 않으면 위의 테스트가 조용히 사라지므로 개수를 확인
        Assert.True(Cases().Count() >= 100);
    }
}
