using OracleSessionHistory.Server.Support;

namespace OracleSessionHistory.Server.Api;

/// <summary>セッション履歴の Excel 出力（一覧のシートと検索条件のシート）</summary>
public static class SessionExport
{
    /// <summary>出力する最大行数（それより多い場合は新しい順に切り詰め、条件のシートに書く）</summary>
    public const int MaxRows = 50_000;

    /// <summary>種別の表示名（画面の format.ts の kindLabels と同じ）</summary>
    private static readonly Dictionary<string, string> KindLabels = new()
    {
        ["Client"] = "クライアント",
        ["DbLinkSuspect"] = "DB Linkの可能性",
        ["JobSlave"] = "ジョブ",
        ["ParallelSlave"] = "パラレル実行",
        ["AqSlave"] = "AQ",
        ["DataPump"] = "Data Pump",
        ["OracleInternal"] = "Oracle内部",
        ["Background"] = "バックグラウンド",
        ["Self"] = "このシステム",
    };

    /// <summary>DB Link 利用元の特定方法の表示名（画面の matchTypeLabels と同じ）</summary>
    private static readonly Dictionary<string, string> MatchTypeLabels = new()
    {
        ["PROCESS"] = "プロセス一致（確定）",
        ["SQL"] = "SQLの@DBLINK",
        ["SYNONYM"] = "シノニム経由",
    };

    private static readonly string[] Header =
    [
        "行の種類", "状態", "監視対象", "ログオン", "切断・最終確認", "切断時刻", "接続時間", "実行端末", "アプリ", "アプリの補足",
        "DB Link 経由のアプリ", "DB Link 経由の端末", "DB Link 経由の実行ユーザ", "実行ユーザ", "接続ユーザ", "スキーマ", "モジュール",
        "PROGRAM", "種別", "ジョブ", "INST_ID", "SID", "SERIAL#", "DB Link 接続元", "DB Link", "特定方法", "SQL", "ID",
    ];

    private static readonly double[] Widths =
        [12, 8, 14, 20, 12, 20, 12, 18, 28, 30, 24, 18, 24, 28, 14, 14, 24, 24, 14, 16, 8, 8, 9, 16, 16, 18, 50, 10];

    internal static byte[] Create(PagedResult<SessionRow> result, SessionSearch s, string? targetName,
        IReadOnlyDictionary<string, string> userNames, DateTime now)
    {
        string? User(string? account) =>
            string.IsNullOrWhiteSpace(account) ? null
            : userNames.TryGetValue(account.Trim().ToUpperInvariant(), out var name) ? $"{name}（{account}）" : account;

        var rows = result.Items.Select(r => r.Session is { } x
            ? (IReadOnlyList<object?>)
            [
                "セッション", x.IsConnected ? (x.Status == "ACTIVE" ? "実行中" : "接続中") : "切断", x.TargetName, x.LogonTime,
                x.IsConnected ? null : x.DisconnectUncertain ? "切断（推定）" : "切断", x.DisconnectedAt,
                Duration(x.LogonTime, x.IsConnected ? now : x.DisconnectedAt), x.Machine, x.AppName, x.AppDetail,
                x.LinkOriginApp, x.LinkOriginMachine, User(x.LinkOriginUser), User(x.ProcOwner ?? x.OsUser), x.Username, x.SchemaName,
                x.Module, x.Program, KindLabels.GetValueOrDefault(x.ProcessKind, x.ProcessKind), x.JobName, x.InstId, x.Sid, x.Serial,
                null, null, null, null, x.Id,
            ]
            : r.Usage is { } u
                ? [
                    "DB Link利用元", null, r.TargetName, u.LogonTime, "最終確認", u.LastSeenAt, Duration(u.LogonTime, u.LastSeenAt),
                    u.Machine, u.AppName, u.AppDetail, null, null, null, User(u.ProcOwner ?? u.OsUser), u.Username, u.SchemaName,
                    u.Module, u.Program, null, null, u.InstId, u.Sid, u.Serial,
                    u.LinkSourceName, u.DbLinkName, MatchTypeLabels.GetValueOrDefault(u.MatchType, u.MatchType), u.SqlText,
                    u.MatchedSessionId,
                ]
                : []).ToList();

        return XlsxWriter.Write(
        [
            new("セッション履歴", Header, rows, Widths),
            new("検索条件", ["項目", "値"], Conditions(result, s, targetName, now), [24, 80]),
        ]);
    }

    /// <summary>検索条件と件数（出力の元になった条件を残す）</summary>
    private static List<IReadOnlyList<object?>> Conditions(PagedResult<SessionRow> result, SessionSearch s, string? targetName, DateTime now)
    {
        var list = new List<IReadOnlyList<object?>> { new object?[] { "出力日時", now } };
        void Add(string name, object? value)
        {
            if (value is null || value is string str && string.IsNullOrWhiteSpace(str)) return;
            list.Add([name, value]);
        }
        Add("監視対象", s.TargetId is null ? "すべて" : targetName ?? $"ID {s.TargetId}");
        Add("対象", s.Source switch
        {
            SessionEndpoints.DbLinkSource => "DB Link利用元",
            SessionEndpoints.AllSource => "すべて（セッションと DB Link利用元）",
            _ => "セッション",
        });
        Add("状態", s.State switch { "connected" => "接続中", "disconnected" => "切断済み", _ => s.State });
        Add("種別", s.Kind is { } k ? KindLabels.GetValueOrDefault(k, k) : null);
        Add("キーワード", s.Q);
        Add("実行端末", s.Machine);
        Add("アプリ", s.App);
        Add("接続ユーザ", s.Username);
        Add("実行ユーザ", s.OsUser);
        Add("実行ファイル（PROGRAM）", s.Program);
        Add("期間（から）", s.From);
        Add("期間（まで）", s.To);
        if (s.IncludeInternal) Add("Oracle 内部プロセス", "表示する");
        if (s.Exact) Add("端末・アプリ", "完全一致");
        Add("接続アプリの行：端末", s.Host);
        Add("接続アプリの行：経由", s.Via == SessionEndpoints.DirectVia ? "直接の接続のみ" : s.Via is { Length: > 0 } via ? $"DB Link 経由（{via}）" : null);
        Add("接続アプリの行：DB Link 接続元", s.LinkSource);
        Add("接続アプリの行：接続元の端末", s.Origin);
        Add("該当件数", result.Total);
        Add("出力件数", result.Items.Count);
        if (result.Total > result.Items.Count)
            Add("注意", $"該当件数が {MaxRows:N0} 件を超えるため、ログオンが新しい順に {result.Items.Count:N0} 件だけ出力しました。期間等で絞り込んでください");
        return list;
    }

    /// <summary>接続時間（画面と同じ表記。例：2時間15分）</summary>
    internal static string? Duration(DateTime from, DateTime? to)
    {
        if (to is null) return null;
        var ms = (to.Value - from).TotalMilliseconds;
        if (ms < 0) return null;
        var m = (long)(ms / 60000);
        if (m < 1) return "1分未満";
        if (m < 60) return $"{m}分";
        var h = m / 60;
        return h < 24 ? $"{h}時間{m % 60}分" : $"{h / 24}日{h % 24}時間";
    }
}
