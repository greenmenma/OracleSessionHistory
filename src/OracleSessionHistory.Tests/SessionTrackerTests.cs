using OracleSessionHistory.Core.Collection;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Tracking;

namespace OracleSessionHistory.Tests;

public class SessionTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 10, 0, 0);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private static ObservedSession Obs(int sid, int inst = 1, string program = "app.exe", string status = "INACTIVE") => new()
    {
        InstId = inst, Sid = sid, Serial = sid * 10, LogonTime = T0.AddMinutes(-sid),
        Program = program, Machine = "PC01", Username = "SCOTT", Status = status, SessionType = "USER",
    };

    private static SessionSnapshot Snap(DateTime at, params ObservedSession[] sessions) => new()
    {
        CapturedAt = at,
        RespondedInstances = new HashSet<int> { 1, 2 },
        Sessions = sessions,
    };

    [Fact]
    public void 端末名は前後の空白を除いて保存する()
    {
        var row = SessionTracker.Apply(1, [], Snap(T0, Obs(1) with { Machine = " PC01 " }), null, Interval).Added.Single();
        Assert.Equal("PC01", row.Machine);
    }

    [Fact]
    public void 再接続したクライアントは対象外にしたプロセス情報を取り直す()
    {
        var row = SessionTracker.Apply(1, [], Snap(T0, Obs(1)), null, Interval).Added.Single();
        row.IsConnected = false;
        row.ProcInfoState = ProcInfoStates.NotApplicable;
        row.ProcInfoError = "切断までに取得できませんでした";

        var r = SessionTracker.Apply(1, [row], Snap(T0.AddSeconds(5), Obs(1)), T0, Interval);

        Assert.Single(r.Reconnected);
        Assert.Equal(ProcInfoStates.Pending, row.ProcInfoState);
        Assert.Null(row.ProcInfoError);
    }

    [Fact]
    public void このシステムのMODULEの新規セッションはSelfで取得対象外()
    {
        var r = SessionTracker.Apply(1, [], Snap(T0, Obs(1, program: "w3wp.exe") with { Module = "OracleSessionHistory" }), null, Interval);

        var row = r.Added.Single();
        Assert.Equal(ProcessKinds.Self, row.ProcessKind);
        Assert.Equal(ProcInfoStates.NotApplicable, row.ProcInfoState);
    }

    [Fact]
    public void MODULEが取れない観測があってもSelfのまま()
    {
        var row = SessionTracker.Apply(1, [], Snap(T0, Obs(1, program: "w3wp.exe") with { Module = "OracleSessionHistory" }), null, Interval).Added.Single();

        SessionTracker.Apply(1, [row], Snap(T0.AddSeconds(5), Obs(1, program: "w3wp.exe") with { Module = null }), T0, Interval);
        Assert.Equal(ProcessKinds.Self, row.ProcessKind);
        SessionTracker.Apply(1, [row], Snap(T0.AddSeconds(10), Obs(1, program: "w3wp.exe") with { Module = "受注" }), T0.AddSeconds(5), Interval);
        Assert.Equal(ProcessKinds.Self, row.ProcessKind);
    }

    [Fact]
    public void 新しいセッションは追加される()
    {
        var r = SessionTracker.Apply(1, [], Snap(T0, Obs(1), Obs(2)), null, Interval);
        Assert.Equal(2, r.Added.Count);
        Assert.All(r.Added, s => Assert.True(s.IsConnected));
        Assert.All(r.Added, s => Assert.Equal(T0, s.FirstSeenAt));
    }

    [Fact]
    public void 同じセッションは追加せず既存行を更新する()
    {
        var first = SessionTracker.Apply(1, [], Snap(T0, Obs(1)), null, Interval);
        var row = first.Added.Single();

        var second = SessionTracker.Apply(1, [row], Snap(T0.AddSeconds(5), Obs(1, status: "ACTIVE")), T0, Interval);

        Assert.Empty(second.Added);
        Assert.Same(row, second.Updated.Single());
        Assert.Equal(T0.AddSeconds(5), row.LastSeenAt);
        Assert.Equal("ACTIVE", row.Status);
        Assert.Equal(T0, row.FirstSeenAt);
    }

    [Fact]
    public void 存在しなくなったセッションは切断として履歴に残る()
    {
        var row = SessionTracker.Apply(1, [], Snap(T0, Obs(1), Obs(2)), null, Interval).Added;

        var r = SessionTracker.Apply(1, row, Snap(T0.AddSeconds(5), Obs(1)), T0, Interval);

        var d = Assert.Single(r.Disconnected);
        Assert.Equal(2, d.Sid);
        Assert.False(d.IsConnected);
        Assert.Equal(T0, d.DisconnectedAt);
        Assert.Equal(T0.AddSeconds(5), d.DisconnectDetectedAt);
        Assert.False(d.DisconnectUncertain);
    }

    [Fact]
    public void 同じSIDでもSERIALが変われば別セッション()
    {
        var rows = SessionTracker.Apply(1, [], Snap(T0, Obs(1)), null, Interval).Added;
        var reused = Obs(1) with { Serial = 999, LogonTime = T0.AddSeconds(3) };

        var r = SessionTracker.Apply(1, rows, Snap(T0.AddSeconds(5), reused), T0, Interval);

        Assert.Single(r.Added);
        Assert.Single(r.Disconnected);
    }

    [Fact]
    public void 応答しなかったインスタンスのセッションは切断扱いにしない()
    {
        var rows = SessionTracker.Apply(1, [], Snap(T0, Obs(1, inst: 1), Obs(2, inst: 2)), null, Interval).Added;
        var snap = new SessionSnapshot
        {
            CapturedAt = T0.AddSeconds(5),
            RespondedInstances = new HashSet<int> { 1 }, // インスタンス 2 は応答なし
            Sessions = [Obs(1, inst: 1)],
        };

        var r = SessionTracker.Apply(1, rows, snap, T0, Interval);

        Assert.Empty(r.Disconnected);
        Assert.True(rows.Single(x => x.InstId == 2).IsConnected);
    }

    [Fact]
    public void 前回成功から間が空いた場合は切断時刻を推定扱いにする()
    {
        var rows = SessionTracker.Apply(1, [], Snap(T0, Obs(1)), null, Interval).Added;

        // 取得失敗が続き 5 分後に成功
        var r = SessionTracker.Apply(1, rows, Snap(T0.AddMinutes(5)), T0, Interval);

        var d = Assert.Single(r.Disconnected);
        Assert.True(d.DisconnectUncertain);
        Assert.Equal(T0, d.DisconnectedAt);
    }

    [Fact]
    public void 切断扱いの行が再び見えたら接続中に戻す()
    {
        var rows = SessionTracker.Apply(1, [], Snap(T0, Obs(1)), null, Interval).Added;
        SessionTracker.Apply(1, rows, Snap(T0.AddSeconds(5)), T0, Interval);
        Assert.False(rows[0].IsConnected);

        var r = SessionTracker.Apply(1, rows, Snap(T0.AddSeconds(10), Obs(1)), T0.AddSeconds(5), Interval);

        Assert.Empty(r.Added);
        Assert.Single(r.Reconnected);
        Assert.True(rows[0].IsConnected);
        Assert.Null(rows[0].DisconnectedAt);
    }

    [Fact]
    public void ORACLE_EXEはDBLinkの可能性として分類される()
    {
        var r = SessionTracker.Apply(1, [], Snap(T0, Obs(1, program: "ORACLE.EXE")), null, Interval);
        Assert.Equal(ProcessKinds.DbLinkSuspect, r.Added.Single().ProcessKind);
    }

    [Fact]
    public void ジョブ情報はジョブ終了後も保持する()
    {
        var job = Obs(1, program: "ORACLE.EXE (J000)") with { JobType = "SCHEDULER", JobName = "NIGHTLY", JobOwner = "BATCH" };
        var rows = SessionTracker.Apply(1, [], Snap(T0, job), null, Interval).Added;

        // ジョブ終了後もスレーブセッションは残る（ジョブ情報は取れない）
        SessionTracker.Apply(1, rows, Snap(T0.AddSeconds(5), job with { JobName = null, JobType = null }), T0, Interval);

        Assert.Equal("NIGHTLY", rows[0].JobName);
        Assert.Equal(ProcessKinds.JobSlave, rows[0].ProcessKind);
    }

    [Fact]
    public void スケジューラジョブはACTIONからジョブ名を補完する()
    {
        var job = Obs(1, program: "oracle@db (J001)") with { Module = "DBMS_SCHEDULER", Action = "PURGE_LOG" };
        var row = SessionTracker.Apply(1, [], Snap(T0, job), null, Interval).Added.Single();
        Assert.Equal("PURGE_LOG", row.JobName);
        Assert.Equal("SCHEDULER", row.JobType);
    }
    [Fact]
    public void 新規のクライアント行はプロセス情報未取得でアプリ名はPROGRAM()
    {
        var row = SessionTracker.Apply(1, [], Snap(T0, Obs(1, program: "w3wp.exe")), null, Interval).Added.Single();
        Assert.Equal(ProcInfoStates.Pending, row.ProcInfoState);
        Assert.Equal("w3wp.exe", row.AppName);
    }

    [Fact]
    public void DbLinkの可能性の行はプロセス情報の対象外()
    {
        var row = SessionTracker.Apply(1, [], Snap(T0, Obs(1, program: "ORACLE.EXE")), null, Interval).Added.Single();
        Assert.Equal(ProcInfoStates.NotApplicable, row.ProcInfoState);
        Assert.Equal("ORACLE.EXE", row.AppName);
    }

    [Fact]
    public void プロセス情報で確定したアプリ名は再観測で上書きしない()
    {
        var row = SessionTracker.Apply(1, [], Snap(T0, Obs(1, program: "w3wp.exe")), null, Interval).Added.Single();
        row.ProcInfoState = ProcInfoStates.Resolved;
        row.AppName = "IIS: Foo";
        row.AppDetail = "Default Web Site/foo";

        SessionTracker.Apply(1, [row], Snap(T0.AddSeconds(5), Obs(1, program: "w3wp.exe")), T0, Interval);

        Assert.Equal("IIS: Foo", row.AppName);
        Assert.Equal("Default Web Site/foo", row.AppDetail);
        Assert.Equal(ProcInfoStates.Resolved, row.ProcInfoState);
    }

    [Fact]
    public void 未確定のアプリ名はモジュールの変化に追従する()
    {
        var row = SessionTracker.Apply(1, [], Snap(T0, Obs(1, program: "販売.exe")), null, Interval).Added.Single();
        SessionTracker.Apply(1, [row], Snap(T0.AddSeconds(5), Obs(1, program: "販売.exe") with { Module = "受注入力" }), T0, Interval);
        Assert.Equal("受注入力", row.AppDetail);
    }
}
