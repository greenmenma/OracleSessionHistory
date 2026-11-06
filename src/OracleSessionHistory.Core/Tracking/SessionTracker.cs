using OracleSessionHistory.Core.Collection;
using OracleSessionHistory.Core.Entities;
using OracleSessionHistory.Core.Processes;

namespace OracleSessionHistory.Core.Tracking;

/// <summary>追跡結果</summary>
public sealed class TrackResult
{
    public List<TrackedSession> Added { get; } = [];
    public List<TrackedSession> Updated { get; } = [];
    public List<TrackedSession> Disconnected { get; } = [];
    public List<TrackedSession> Reconnected { get; } = [];
}

/// <summary>
/// 取得したスナップショットを既存の追跡行に反映する（DB に依存しない純粋なロジック）。
/// ・同じセッション（inst_id, sid, serial#, logon_time）は既存行を更新する
/// ・応答したインスタンスに存在しなくなった接続中セッションを切断とする
/// ・取得失敗時はこのメソッドを呼ばない（＝誤って切断扱いにしない）
/// </summary>
public static class SessionTracker
{
    public readonly record struct Key(int InstId, int Sid, int Serial, DateTime LogonTime);

    public static Key KeyOf(TrackedSession s) => new(s.InstId, s.Sid, s.Serial, s.LogonTime);
    public static Key KeyOf(ObservedSession s) => new(s.InstId, s.Sid, s.Serial, s.LogonTime);

    /// <param name="targetId">監視対象 ID</param>
    /// <param name="known">接続中の行と、直近に切断扱いにした行（復帰判定用）</param>
    /// <param name="snapshot">今回取得したスナップショット</param>
    /// <param name="lastSuccessAt">前回取得に成功した時刻（切断時刻の精度判定に使う）</param>
    /// <param name="interval">取得間隔</param>
    public static TrackResult Apply(
        int targetId,
        IReadOnlyCollection<TrackedSession> known,
        SessionSnapshot snapshot,
        DateTime? lastSuccessAt,
        TimeSpan interval)
    {
        var result = new TrackResult();
        var now = snapshot.CapturedAt;
        var byKey = new Dictionary<Key, TrackedSession>();
        foreach (var k in known)
        {
            // 同じキーが複数ある場合は接続中を優先
            var key = KeyOf(k);
            if (!byKey.TryGetValue(key, out var existing) || (!existing.IsConnected && k.IsConnected))
                byKey[key] = k;
        }

        var seen = new HashSet<Key>();
        foreach (var o in snapshot.Sessions)
        {
            var key = KeyOf(o);
            if (!seen.Add(key)) continue;

            if (byKey.TryGetValue(key, out var row))
            {
                if (!row.IsConnected)
                {
                    // 一時的に見えなかっただけ（インスタンス応答なし等）なので接続中に戻す
                    row.IsConnected = true;
                    row.DisconnectedAt = null;
                    row.DisconnectDetectedAt = null;
                    row.DisconnectUncertain = false;
                    // 切断扱いの間に「取得前に切断」で対象外にしたプロセス情報は取り直す（対象外の理由は次の取得で判定し直す）
                    if (row.ProcessKind == ProcessKinds.Client && ClientProcessRules.IsInspectedProgram(row.Program)
                        && row.ProcInfoState == ProcInfoStates.NotApplicable)
                    {
                        row.ProcInfoState = ProcInfoStates.Pending;
                        row.ProcInfoError = null;
                    }
                    result.Reconnected.Add(row);
                }
                CopyObserved(o, row, now);
                result.Updated.Add(row);
            }
            else
            {
                row = new TrackedSession
                {
                    TargetId = targetId,
                    InstId = o.InstId,
                    Sid = o.Sid,
                    Serial = o.Serial,
                    LogonTime = o.LogonTime,
                    FirstSeenAt = now,
                    IsConnected = true,
                };
                CopyObserved(o, row, now);
                // プロセス情報は通常のクライアント接続のうち、取得対象のプログラム（w3wp.exe・sqlplus.exe）のみ取得する
                // （DB Link・Oracle 内部プロセスは接続元が DB サーバー自身）
                row.ProcInfoState = row.ProcessKind == ProcessKinds.Client && ClientProcessRules.IsInspectedProgram(row.Program)
                    ? ProcInfoStates.Pending
                    : ProcInfoStates.NotApplicable;
                result.Added.Add(row);
            }
        }

        // 前回成功から間が空いている場合（取得失敗が続いた・Batch 停止など）は切断時刻が不正確
        var gapTooLarge = lastSuccessAt is null || now - lastSuccessAt.Value > interval * 3 + TimeSpan.FromSeconds(5);

        foreach (var row in byKey.Values)
        {
            if (!row.IsConnected) continue;
            if (seen.Contains(KeyOf(row))) continue;
            // 応答しなかったインスタンスのセッションは判断できないので保留
            if (!snapshot.RespondedInstances.Contains(row.InstId)) continue;

            row.IsConnected = false;
            row.DisconnectedAt = row.LastSeenAt;
            row.DisconnectDetectedAt = now;
            row.DisconnectUncertain = gapTooLarge || now - row.LastSeenAt > interval * 3 + TimeSpan.FromSeconds(5);
            result.Disconnected.Add(row);
        }

        return result;
    }

    private static void CopyObserved(ObservedSession o, TrackedSession row, DateTime now)
    {
        row.AuditSid = o.AuditSid;
        row.Username = o.Username;
        row.SchemaName = o.SchemaName;
        row.OsUser = o.OsUser;
        // 前後の空白は除く（接続アプリ表のまとめ方と完全一致の検索を、照合順序によらずそろえるため）
        row.Machine = string.IsNullOrWhiteSpace(o.Machine) ? o.Machine?.Trim() : o.Machine.Trim();
        row.Terminal = o.Terminal;
        row.Program = o.Program;
        row.Module = o.Module ?? row.Module;
        row.Action = o.Action ?? row.Action;
        row.ClientInfo = o.ClientInfo ?? row.ClientInfo;
        row.ClientIdentifier = o.ClientIdentifier ?? row.ClientIdentifier;
        row.ServiceName = o.ServiceName;
        row.ClientProcess = o.ClientProcess;
        row.ServerProcess = o.ServerProcess;
        row.Port = o.Port;
        row.ClientDriver = o.ClientDriver ?? row.ClientDriver;
        row.ClientVersion = o.ClientVersion ?? row.ClientVersion;
        row.SessionType = o.SessionType;
        row.Status = o.Status;
        row.Event = o.Event;
        if (o.SqlId is not null)
        {
            row.SqlId = o.SqlId;
            if (o.SqlText is not null) row.LastSqlText = o.SqlText;
        }

        // このシステム自身の接続は MODULE で判定する。MODULE は後から設定されるため取れない観測もあり、
        // 前回までの値で補い、いったん Self と判定した行は Self のままにする（一覧に出たり消えたりしないように）
        if (row.ProcessKind != ProcessKinds.Self)
            row.ProcessKind = ProgramClassifier.Classify(o.Program, o.SessionType, row.Module);

        // ジョブ情報は一度取れたら保持する（ジョブ終了後も何のジョブだったか残すため）
        if (o.JobName is not null)
        {
            row.JobType = o.JobType;
            row.JobOwner = o.JobOwner;
            row.JobName = o.JobName;
            row.JobAction = o.JobAction;
        }
        else if (row.JobName is null && row.ProcessKind == ProcessKinds.JobSlave
                 && string.Equals(o.Module, "DBMS_SCHEDULER", StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrEmpty(o.Action))
        {
            // スケジューラジョブは module=DBMS_SCHEDULER, action=ジョブ名 が設定される
            row.JobType = "SCHEDULER";
            row.JobName = o.Action;
        }
        if (o.ParentInfo is not null) row.ParentInfo = o.ParentInfo;

        // プロセス情報で確定したアプリ名は上書きしない
        if (row.ProcInfoState != ProcInfoStates.Resolved)
            (row.AppName, row.AppDetail) = AppIdentifier.Identify(null, row.Program, row.Module, row.OsUser);

        row.LastSeenAt = now;
        if (string.Equals(o.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)) row.LastActiveAt = now;
    }
}
