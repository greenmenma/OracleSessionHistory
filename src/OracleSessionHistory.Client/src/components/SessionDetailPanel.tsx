import { api, type DbLinkUsage } from '../api'
import { useAsync } from '../hooks'
import { fmtDateTime, fmtDuration, kindLabels, matchTypeLabels, procInfoLabels } from '../format'

export default function SessionDetailPanel({ id, onClose }: { id: number; onClose: () => void }) {
  const { data, error } = useAsync(() => api.session(id), [id], 10000)
  const s = data?.session

  return (
    <div className="drawer-backdrop" onClick={onClose}>
      <aside className="drawer" onClick={(e) => e.stopPropagation()}>
        <div className="drawer-head">
          <h2>セッション詳細</h2>
          <button onClick={onClose} aria-label="閉じる">
            ✕
          </button>
        </div>
        {error && <div className="alert error">{error}</div>}
        {s && data && (
          <div className="drawer-body">
            <dl className="props">
              <Prop label="監視対象" value={data.targetName} />
              <Prop
                label="状態"
                value={
                  s.isConnected
                    ? `接続中（${s.status ?? ''}）`
                    : `切断（${fmtDateTime(s.disconnectedAt)}${s.disconnectUncertain ? ' ※推定' : ''}）`
                }
              />
              <Prop label="ログオン" value={fmtDateTime(s.logonTime)} />
              <Prop label="接続時間" value={fmtDuration(s.logonTime, s.isConnected ? null : s.disconnectedAt)} />
              <Prop label="初回検知 / 最終確認" value={`${fmtDateTime(s.firstSeenAt)} / ${fmtDateTime(s.lastSeenAt)}`} />
              <Prop label="INST_ID / SID,SERIAL#" value={`${s.instId} / ${s.sid},${s.serial}`} />
              <Prop label="種別" value={kindLabels[s.processKind] ?? s.processKind} />
            </dl>

            <h3>接続アプリ</h3>
            <dl className="props">
              <Prop label="アプリ" value={s.appName} />
              <Prop label="補足" value={s.appDetail} />
              <Prop
                label="プロセス情報"
                value={`${procInfoLabels[s.procInfoState] ?? s.procInfoState}${s.procInfoError ? `：${s.procInfoError}` : ''}${
                  s.procInfoAt ? `（${fmtDateTime(s.procInfoAt)}）` : ''
                }`}
              />
              {s.procInfoState === 'Resolved' && (
                <>
                  <Prop label="IIS アプリケーションプール" value={s.iisAppPool} />
                  <Prop label="IIS サイト / パス" value={s.iisSite} />
                  <Prop label="プロセス名" value={s.procName} />
                  <Prop label="実行ファイル" value={s.procPath} />
                  <Prop label="コマンドライン" value={s.procCommandLine} pre />
                  <Prop label="実行ユーザ" value={s.procOwner} />
                  <Prop label="プロセス開始" value={fmtDateTime(s.procStartedAt)} />
                  <Prop label="親プロセス" value={s.procParentName} />
                </>
              )}
            </dl>
            {s.procInfoState === 'Failed' && (
              <p className="muted">
                接続元サーバーに WMI（WinRM / DCOM）で接続できませんでした。<a href="#/settings">設定</a>
                の「プロセス情報の取得」でアカウントと接続テストを確認してください。時間をおいて自動で再試行します。
              </p>
            )}

            <h3>利用システムの特定情報（Oracle）</h3>
            <dl className="props">
              <Prop label="実行端末 (MACHINE)" value={s.machine} />
              <Prop label="端末 (TERMINAL)" value={s.terminal} />
              <Prop label="実行プログラム (PROGRAM)" value={s.program} />
              <Prop label="接続ユーザ (USERNAME)" value={s.username} />
              <Prop label="実行スキーマ (SCHEMANAME)" value={s.schemaName} />
              <Prop label="実行ユーザ (OSUSER)" value={s.osUser} />
              <Prop label="モジュール (MODULE)" value={s.module} />
              <Prop label="アクション (ACTION)" value={s.action} />
              <Prop label="CLIENT_INFO" value={s.clientInfo} />
              <Prop label="CLIENT_IDENTIFIER" value={s.clientIdentifier} />
              <Prop label="サービス名" value={s.serviceName} />
              <Prop label="クライアントドライバ" value={[s.clientDriver, s.clientVersion].filter(Boolean).join(' ')} />
              <Prop label="クライアントプロセス (PROCESS)" value={s.clientProcess} />
              <Prop label="サーバープロセス (SPID)" value={s.serverProcess} />
              <Prop label="ポート" value={s.port?.toString()} />
            </dl>

            {(s.jobName || s.parentInfo) && (
              <>
                <h3>ジョブ・親セッション</h3>
                <dl className="props">
                  <Prop label="ジョブ種別" value={s.jobType} />
                  <Prop label="ジョブ所有者" value={s.jobOwner} />
                  <Prop label="ジョブ名" value={s.jobName} />
                  <Prop label="ジョブ内容" value={s.jobAction} pre />
                  <Prop label="親セッション" value={s.parentInfo} />
                </dl>
              </>
            )}

            {s.processKind === 'DbLinkSuspect' && (
              <>
                <h3>DB Link の接続元</h3>
                <p className="muted">
                  実行プログラムが ORACLE.EXE / oracle@host のため、別の Oracle から DB Link 経由で接続されている可能性があります。
                </p>
                {!data.hasLinkSource && (
                  <div className="alert info">
                    接続元 DB の接続文字列を<a href="#/settings">設定</a>で登録し、DB Link を選択すると、
                    DB Link を使っている端末・プログラム・ユーザを特定できます。
                  </div>
                )}
                {data.origins.length > 0 && <UsageList title="特定された接続元" items={data.origins} />}
                {data.origins.length === 0 && data.candidates.length > 0 && (
                  <UsageList title="同時間帯に DB Link を使っていた接続元（候補）" items={data.candidates} />
                )}
                {data.hasLinkSource && data.origins.length === 0 && data.candidates.length === 0 && (
                  <p className="muted">接続元はまだ特定できていません。</p>
                )}
              </>
            )}

            <h3>SQL</h3>
            <dl className="props">
              <Prop label="SQL_ID" value={s.sqlId} />
              <Prop label="待機イベント" value={s.event} />
            </dl>
            {s.lastSqlText && <pre className="sql">{s.lastSqlText}</pre>}
          </div>
        )}
      </aside>
    </div>
  )
}

function Prop({ label, value, pre }: { label: string; value?: string | null; pre?: boolean }) {
  return (
    <>
      <dt>{label}</dt>
      <dd className={pre ? 'pre' : ''}>{value || <span className="muted">–</span>}</dd>
    </>
  )
}

function UsageList({ title, items }: { title: string; items: DbLinkUsage[] }) {
  return (
    <div className="usage-list">
      <h4>{title}</h4>
      {items.map((u) => (
        <div key={u.id} className="usage">
          <div>
            <strong>{u.machine}</strong> / <strong>{u.appName}</strong>
            {u.procInfoState !== 'Resolved' && u.procInfoState !== 'NotApplicable' && (
              <span className="muted">（{procInfoLabels[u.procInfoState]}）</span>
            )}
          </div>
          {u.appDetail && <div className="muted small">{u.appDetail}</div>}
          {u.procPath && u.procPath !== u.appDetail && <div className="muted small mono">{u.procPath}</div>}
          <div className="muted">
            ユーザ {u.username}（OS: {u.procOwner ?? u.osUser}）・PROGRAM {u.program}・{u.linkSourceName} ・ {u.dbLinkName} ・{' '}
            {matchTypeLabels[u.matchType] ?? u.matchType}
          </div>
          {u.sqlText && <pre className="sql small">{u.sqlText}</pre>}
        </div>
      ))}
    </div>
  )
}
