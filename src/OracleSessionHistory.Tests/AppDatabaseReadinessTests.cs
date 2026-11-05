using OracleSessionHistory.Core.Data;

namespace OracleSessionHistory.Tests;

public class AppDatabaseReadinessTests
{
    [Fact]
    public async Task 初期化が終わるまで待ち終わったら進む()
    {
        var r = new AppDatabaseReadiness();
        var wait = r.WaitAsync(default);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);

        r.MarkReady();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(r.WaitAsync(default).IsCompleted);
    }
}
