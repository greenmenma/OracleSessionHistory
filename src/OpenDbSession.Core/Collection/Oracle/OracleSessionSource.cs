using Oracle.ManagedDataAccess.Client;
using OpenDbSession.Core.Entities;

namespace OpenDbSession.Core.Collection.Oracle;

/// <summary>
/// gv$session からセッション情報を取得する。
/// RAC でも全インスタンスを取得できるよう gv$ ビューを使う。
/// 必要権限は docs/oracle-privileges.md を参照。
/// </summary>
public sealed class OracleSessionSource : ISessionSource
{
    public string Provider => SourceProviders.Oracle;

    internal const string InstanceSql = "select inst_id from gv$instance";

    internal const string SessionSql = """
        select s.inst_id, s.sid, s.serial# as serial, s.audsid, s.username, s.schemaname, s.osuser,
               s.machine, s.terminal, s.program, s.module, s.action, s.client_info, s.client_identifier,
               s.service_name, s.process, s.port, s.type, s.status, s.sql_id, s.prev_sql_id, s.event,
               s.logon_time, p.spid
          from gv$session s
          left join gv$process p on p.inst_id = s.inst_id and p.addr = s.paddr
         where (:inc_bg = 1 or s.type <> 'BACKGROUND')
           and not (s.inst_id = sys_context('USERENV', 'INSTANCE') and s.sid = sys_context('USERENV', 'SID'))
        """;

    // 現在/直前の SQL のテキスト（セッションが参照している SQL だけに絞る）
    internal const string SqlTextSql = """
        select a.inst_id, a.sql_id, substr(a.sql_text, 1, 1000) as sql_text
          from gv$sqlarea a
         where (a.inst_id, a.sql_id) in (
                select s.inst_id, nvl(s.sql_id, s.prev_sql_id) from gv$session s
                 where nvl(s.sql_id, s.prev_sql_id) is not null and s.type <> 'BACKGROUND')
        """;

    // クライアントドライバ（ODP.NET / JDBC 等）。システム特定の手がかりになる
    internal const string ConnectInfoSql = """
        select inst_id, sid, max(client_driver) as client_driver, max(client_version) as client_version
          from gv$session_connect_info
         group by inst_id, sid
        """;

    // スケジューラジョブ（DBMS_SCHEDULER）
    internal const string SchedulerJobSql = """
        select r.running_instance as inst_id, r.session_id as sid, r.owner, r.job_name,
               j.job_type, substr(nvl(j.job_action, j.program_name), 1, 1000) as job_action
          from dba_scheduler_running_jobs r
          left join dba_scheduler_jobs j on j.owner = r.owner and j.job_name = r.job_name
         where r.session_id is not null
        """;

    // 旧来のジョブ（DBMS_JOB）
    internal const string DbmsJobSql = """
        select r.instance as inst_id, r.sid, j.job, j.log_user, j.schema_user, substr(j.what, 1, 1000) as what
          from dba_jobs_running r
          join dba_jobs j on j.job = r.job
        """;

    // Data Pump
    internal const string DataPumpSql = """
        select s.inst_id, s.sid, d.owner_name, d.job_name, d.session_type
          from dba_datapump_sessions d
          join gv$session s on s.inst_id = d.inst_id and s.saddr = d.saddr
        """;

    // パラレル実行スレーブ → コーディネータ
    internal const string PxSql = """
        select inst_id, sid, qcinst_id, qcsid
          from gv$px_session
         where sid <> qcsid and qcsid is not null
        """;

    public async Task<SessionSnapshot> CaptureAsync(MonitorTarget target, CancellationToken ct)
    {
        var timeout = Math.Max(target.IntervalSeconds * 3, 15);
        await using var conn = await OracleDb.OpenAsync(target.ConnectionString, ct);
        var warnings = new List<string>();

        // ---- 必須（失敗したら例外＝取得失敗として扱い、切断判定しない） ----
        var instances = await OracleDb.QueryAsync(conn, InstanceSql, r => r.Int("inst_id"), ct, timeout);
        var rows = await OracleDb.QueryAsync(conn, SessionSql, r => new
        {
            InstId = r.Int("inst_id"),
            Sid = r.Int("sid"),
            Serial = r.Int("serial"),
            AuditSid = r.LongN("audsid"),
            Username = r.Str("username"),
            SchemaName = r.Str("schemaname"),
            OsUser = r.Str("osuser"),
            Machine = r.Str("machine"),
            Terminal = r.Str("terminal"),
            Program = r.Str("program"),
            Module = r.Str("module"),
            Action = r.Str("action"),
            ClientInfo = r.Str("client_info"),
            ClientIdentifier = r.Str("client_identifier"),
            ServiceName = r.Str("service_name"),
            Process = r.Str("process"),
            Port = r.IntN("port"),
            Type = r.Str("type"),
            Status = r.Str("status"),
            SqlId = r.Str("sql_id"),
            PrevSqlId = r.Str("prev_sql_id"),
            Event = r.Str("event"),
            LogonTime = r.Date("logon_time"),
            Spid = r.Str("spid"),
        }, ct, timeout, new OracleParameter("inc_bg", target.IncludeBackground ? 1 : 0));

        // ---- 付加情報（失敗しても警告のみ） ----
        var sqlTexts = new Dictionary<(int, string), string>();
        if (target.CollectSqlText)
        {
            foreach (var t in await OracleDb.TryQueryAsync(conn, SqlTextSql,
                         r => (Inst: r.Int("inst_id"), Id: r.Str("sql_id")!, Text: r.Str("sql_text")), warnings, "SQLテキスト", ct, timeout))
                if (t.Text is not null) sqlTexts[(t.Inst, t.Id)] = t.Text;
        }

        var connectInfo = (await OracleDb.TryQueryAsync(conn, ConnectInfoSql,
                r => (Inst: r.Int("inst_id"), Sid: r.Int("sid"), Driver: r.Str("client_driver"), Version: r.Str("client_version")),
                warnings, "接続情報", ct, timeout))
            .GroupBy(x => (x.Inst, x.Sid)).ToDictionary(g => g.Key, g => g.First());

        var jobs = new Dictionary<(int, int), (string Type, string? Owner, string Name, string? Action)>();
        foreach (var j in await OracleDb.TryQueryAsync(conn, SchedulerJobSql,
                     r => (Inst: r.Int("inst_id"), Sid: r.Int("sid"), Owner: r.Str("owner"), Name: r.Str("job_name")!,
                         JobType: r.Str("job_type"), Action: r.Str("job_action")), warnings, "スケジューラジョブ", ct, timeout))
            jobs[(j.Inst, j.Sid)] = ($"SCHEDULER{(j.JobType is null ? "" : $"/{j.JobType}")}", j.Owner, j.Name, j.Action);

        foreach (var j in await OracleDb.TryQueryAsync(conn, DbmsJobSql,
                     r => (Inst: r.Int("inst_id"), Sid: r.Int("sid"), Job: r.Str("job")!, Owner: r.Str("schema_user") ?? r.Str("log_user"),
                         What: r.Str("what")), warnings, "DBMS_JOB", ct, timeout))
        {
            // dba_jobs_running.instance は 0 の場合があるため、SID のみで突き合わせる候補も登録する
            jobs.TryAdd((j.Inst, j.Sid), ("DBMS_JOB", j.Owner, $"JOB#{j.Job}", j.What));
            if (j.Inst == 0) jobs.TryAdd((-1, j.Sid), ("DBMS_JOB", j.Owner, $"JOB#{j.Job}", j.What));
        }

        foreach (var d in await OracleDb.TryQueryAsync(conn, DataPumpSql,
                     r => (Inst: r.Int("inst_id"), Sid: r.Int("sid"), Owner: r.Str("owner_name"), Name: r.Str("job_name")!,
                         Type: r.Str("session_type")), warnings, "Data Pump", ct, timeout))
            jobs.TryAdd((d.Inst, d.Sid), ("DATAPUMP", d.Owner, d.Name, d.Type));

        var px = (await OracleDb.TryQueryAsync(conn, PxSql,
                r => (Inst: r.Int("inst_id"), Sid: r.Int("sid"), QcInst: r.IntN("qcinst_id"), QcSid: r.Int("qcsid")),
                warnings, "パラレル実行", ct, timeout))
            .GroupBy(x => (x.Inst, x.Sid)).ToDictionary(g => g.Key, g => g.First());

        var bySid = rows.GroupBy(x => (x.InstId, x.Sid)).ToDictionary(g => g.Key, g => g.First());

        var sessions = rows.Select(s =>
        {
            var key = (s.InstId, s.Sid);
            var sqlId = s.SqlId ?? s.PrevSqlId;
            sqlTexts.TryGetValue((s.InstId, sqlId ?? ""), out var sqlText);
            connectInfo.TryGetValue(key, out var ci);
            var hasJob = jobs.TryGetValue(key, out var job) || jobs.TryGetValue((-1, s.Sid), out job);

            string? parent = null;
            if (px.TryGetValue(key, out var p))
            {
                var qcKey = (p.QcInst ?? s.InstId, p.QcSid);
                parent = bySid.TryGetValue(qcKey, out var qc)
                    ? $"PXコーディネータ inst={qcKey.Item1} sid={qc.Sid} user={qc.Username} machine={qc.Machine} program={qc.Program}"
                    : $"PXコーディネータ inst={qcKey.Item1} sid={qcKey.QcSid}";
            }

            return new ObservedSession
            {
                InstId = s.InstId,
                Sid = s.Sid,
                Serial = s.Serial,
                LogonTime = s.LogonTime,
                AuditSid = s.AuditSid,
                Username = s.Username,
                SchemaName = s.SchemaName,
                OsUser = s.OsUser,
                Machine = s.Machine,
                Terminal = s.Terminal,
                Program = s.Program,
                Module = s.Module,
                Action = s.Action,
                ClientInfo = s.ClientInfo,
                ClientIdentifier = s.ClientIdentifier,
                ServiceName = s.ServiceName,
                ClientProcess = s.Process,
                ServerProcess = s.Spid,
                Port = s.Port,
                ClientDriver = ci.Driver,
                ClientVersion = ci.Version,
                SessionType = s.Type,
                Status = s.Status,
                SqlId = sqlId,
                SqlText = sqlText,
                Event = s.Event,
                JobType = hasJob ? job.Type : null,
                JobOwner = hasJob ? job.Owner : null,
                JobName = hasJob ? job.Name : null,
                JobAction = hasJob ? job.Action : null,
                ParentInfo = parent,
            };
        }).ToList();

        return new SessionSnapshot
        {
            CapturedAt = DateTime.Now,
            RespondedInstances = instances.ToHashSet(),
            Sessions = sessions,
            Warnings = warnings,
        };
    }

    public async Task<string> TestConnectionAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = await OracleDb.OpenAsync(connectionString, ct);
        var cnt = await OracleDb.QueryAsync(conn, "select count(*) as cnt from gv$session", r => r.Int("cnt"), ct);
        return $"接続成功: Oracle {conn.ServerVersion} / gv$session {cnt.FirstOrDefault()} 件";
    }
}
