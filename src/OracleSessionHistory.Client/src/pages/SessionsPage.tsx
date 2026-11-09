import { useEffect, useRef, useState } from 'react'
import { api, type DbLinkUsage, type SessionListItem } from '../api'
import { useAsync } from '../hooks'
import { fmtDateTime, fmtDuration, internalKinds, kindLabels, matchTypeLabels } from '../format'
import SessionDetailPanel from '../components/SessionDetailPanel'
import KindBadge from '../components/KindBadge'
import Pager from '../components/Pager'
import AppCell from '../components/AppCell'
import UserName from '../components/UserName'
import { replaceQuery } from '../router'

interface Filters {
  /** 表示するもの：空はセッションのみ、dblink は DB Link 利用元のみ、all は両方 */
  source: string
  q: string
  state: string
  kind: string
  machine: string
  app: string
  username: string
  osUser: string
  from: string
  to: string
  /** Oracle 内部プロセス（ジョブ・パラレル実行等）も表示する */
  includeInternal: boolean
  /** 実行端末・アプリを完全一致で検索する（接続アプリ表からの遷移） */
  exact: boolean
  /** 経由（接続アプリ表の親子の行からの遷移）：direct は直接の接続のみ、それ以外は経由した DB サーバーの DB Link 経由の接続のみ */
  via: string
  /** 接続アプリ表の行からの遷移：MACHINE の完全一致（DB Link 経由も DB サーバーの端末で探す） */
  host: string
  /** 接続アプリ表の行からの遷移：実行ファイル（PROGRAM） */
  exe: string
  /** 接続アプリ表の行からの遷移：DB Link 接続元の名前 */
  linkSource: string
  /** 接続アプリ表の行からの遷移：DB Link 経由で特定した接続元の端末 */
  origin: string
}

type BoolKey = 'includeInternal' | 'exact'

/** 検索条件を URL のクエリにする（空・false は省く） */
function toQuery(f: Filters): Record<string, string | undefined> {
  const q: Record<string, string | undefined> = {}
  for (const [k, v] of Object.entries(f)) q[k] = typeof v === 'boolean' ? (v ? 'true' : undefined) : v
  return q
}

function fromQuery(query: URLSearchParams): Filters {
  return {
    source: query.get('source') ?? '',
    q: query.get('q') ?? '',
    state: query.get('state') ?? '',
    kind: query.get('kind') ?? '',
    machine: query.get('machine') ?? '',
    // 旧画面からのリンク（program=）も受け付ける
    app: query.get('app') ?? query.get('program') ?? '',
    username: query.get('username') ?? '',
    osUser: query.get('osUser') ?? '',
    from: query.get('from') ?? '',
    to: query.get('to') ?? '',
    includeInternal: query.get('includeInternal') === 'true',
    exact: query.get('exact') === 'true',
    via: query.get('via') ?? '',
    host: query.get('host') ?? '',
    exe: query.get('exe') ?? '',
    linkSource: query.get('linkSource') ?? '',
    origin: query.get('origin') ?? '',
  }
}

/** 接続アプリ表の行から渡された条件（画面に入力欄は無く、バッジで表示する） */
const treeKeys = ['host', 'exe', 'via', 'linkSource', 'origin'] as const

function treeLabels(f: Filters): string[] {
  const labels: string[] = []
  if (f.host) labels.push(`端末 ${f.host}`)
  if (f.exe) labels.push(`実行ファイル ${f.exe}`)
  if (f.via) labels.push(viaLabel(f.via))
  if (f.linkSource) labels.push(`DB Link 接続元 ${f.linkSource}`)
  if (f.origin) labels.push(`接続元の端末 ${f.origin}`)
  return labels
}

function viaLabel(via: string): string {
  return via === 'direct' ? '直接の接続のみ（DB Link 経由を除く）' : `DB Link 経由（${via}）の接続のみ`
}

/**
 * 監視対象のセッションだけにある条件（サーバーの SessionEndpoints.HasSessionOnlyCondition と同じ）。
 * 実行ファイル（exe）は DB Link 利用元の PROGRAM でも探すため含めない
 */
const sessionOnlyTreeKeys = ['host', 'via', 'linkSource', 'origin'] as const

function hasSessionOnlyCondition(f: Filters): boolean {
  return !!(f.state || f.kind || f.includeInternal || sessionOnlyTreeKeys.some((k) => f[k]))
}

/** DB Link 利用元を表示する場合に効かない条件の説明 */
function sessionOnlyLabel(f: Filters): string | undefined {
  if (!hasSessionOnlyCondition(f)) return undefined
  if (f.source === 'dblink') return '状態・種別・Oracle 内部プロセス・接続アプリの行の条件は、DB Link 利用元には効きません'
  if (f.source === 'all') return '状態・種別・Oracle 内部プロセス・接続アプリの行の条件があるため、DB Link 利用元は表示していません'
  return undefined
}

export default function SessionsPage({ targetId, query }: { targetId?: number; query: URLSearchParams }) {
  const queryKey = query.toString()
  const [form, setForm] = useState<Filters>(() => fromQuery(query))
  const [filters, setFilters] = useState<Filters>(() => fromQuery(query))
  const [page, setPage] = useState(1)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [selected, setSelected] = useState<number>()

  // サマリー等からの遷移でクエリが変わった場合は条件を置き換える。
  // 自分で検索条件を URL に書いた（replaceQuery）だけの場合は、入力途中のフォームを消さないよう何もしない
  const filtersRef = useRef(filters)
  filtersRef.current = filters
  useEffect(() => {
    const f = fromQuery(new URLSearchParams(queryKey))
    if (JSON.stringify(f) === JSON.stringify(filtersRef.current)) return
    setForm(f)
    setFilters(f)
    setPage(1)
  }, [queryKey])

  const { data, error, loading } = useAsync(
    () =>
      api.sessions({
        targetId,
        ...filters,
        from: filters.from || undefined,
        to: filters.to || undefined,
        includeInternal: filters.includeInternal || undefined,
        exact: filters.exact || undefined,
        exe: undefined,
        program: filters.exe || undefined,
        page,
        pageSize: 50,
      }),
    [targetId, JSON.stringify(filters), page],
    autoRefresh ? 10000 : undefined,
  )

  // 検索条件を URL に残す（リロード・リンク共有で同じ条件を開けるように）
  const apply = (f: Filters) => {
    setFilters(f)
    setPage(1)
    replaceQuery('/sessions', toQuery(f))
  }

  const setBool = (k: BoolKey, applyNow: boolean) => (e: React.ChangeEvent<HTMLInputElement>) => {
    setForm({ ...form, [k]: e.target.checked })
    if (applyNow) apply({ ...filters, [k]: e.target.checked })
  }

  const set = (k: Exclude<keyof Filters, BoolKey>) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
    // 接続アプリから開いた（完全一致の）状態で端末・アプリを書き換えたら、部分一致で探したいものとして完全一致を外す
    // 経由の条件も、接続アプリの行に合わせたものなので外す
    setForm({ ...form, [k]: e.target.value, ...(k === 'machine' || k === 'app' ? { exact: false, via: '', host: '', exe: '', linkSource: '', origin: '' } : {}) })

  const search = (e: React.FormEvent) => {
    e.preventDefault()
    apply(form)
  }

  const clear = () => {
    const empty = fromQuery(new URLSearchParams())
    setForm(empty)
    apply(empty)
  }

  const dbLinkOnly = form.source === 'dblink'
  const excluded = sessionOnlyLabel(filters)

  return (
    <div className="page">
      <div className="page-head">
        <h1>セッション履歴</h1>
        <label className="check">
          <input type="checkbox" checked={autoRefresh} onChange={(e) => setAutoRefresh(e.target.checked)} />
          10秒ごとに自動更新
        </label>
      </div>

      <form className="card filters" onSubmit={search}>
        <select
          value={form.source}
          onChange={(e) => {
            // 表示するものはすぐに反映する
            setForm({ ...form, source: e.target.value })
            apply({ ...filters, source: e.target.value })
          }}
          title="DB Link 利用元：DB Link 接続元 DB 上で DB Link を使っていたセッション（設定 → DB Link 接続元 で登録）"
        >
          <option value="">対象：セッション</option>
          <option value="dblink">対象：DB Link利用元</option>
          <option value="all">対象：すべて</option>
        </select>
        <input
          className="wide"
          placeholder="キーワード（端末・アプリ・プール名・実行ファイル・ユーザ・モジュール・SQL など）"
          value={form.q}
          onChange={set('q')}
        />
        <select value={form.state} onChange={set('state')} disabled={dbLinkOnly}>
          <option value="">状態：すべて</option>
          <option value="connected">接続中</option>
          <option value="disconnected">切断済み</option>
        </select>
        <select value={form.kind} onChange={set('kind')} disabled={dbLinkOnly}>
          <option value="">種別：すべて</option>
          {Object.entries(kindLabels)
            .filter(([k]) => !internalKinds.includes(k))
            .map(([k, v]) => (
              <option key={k} value={k}>
                {v}
              </option>
            ))}
          <optgroup label="Oracle 内部プロセス・このシステム">
            {internalKinds.map((k) => (
              <option key={k} value={k}>
                {kindLabels[k] ?? k}
              </option>
            ))}
          </optgroup>
        </select>
        <input placeholder="実行端末" value={form.machine} onChange={set('machine')} />
        <input placeholder="アプリ" value={form.app} onChange={set('app')} />
        <input placeholder="接続ユーザ" value={form.username} onChange={set('username')} />
        <input placeholder="実行ユーザ（アカウント・表示名）" value={form.osUser} onChange={set('osUser')} />
        <label className="inline">
          期間
          <input type="datetime-local" value={form.from} onChange={set('from')} />〜
          <input type="datetime-local" value={form.to} onChange={set('to')} />
        </label>
        <label className="check" title="ORACLE.EXE (J000) などのジョブ・パラレル実行・バックグラウンドプロセス。DB Link の可能性があるものは常に表示します">
          {/* チェックの切り替えはすぐに反映する */}
          <input type="checkbox" checked={form.includeInternal} onChange={setBool('includeInternal', true)} disabled={dbLinkOnly} />
          Oracle 内部プロセス・このシステムの接続も表示
        </label>
        <label className="check" title="実行端末・アプリを部分一致ではなく完全一致で検索します（接続アプリから開いた場合）">
          <input type="checkbox" checked={form.exact} onChange={setBool('exact', false)} />
          端末・アプリを完全一致
        </label>
        {treeLabels(form).length > 0 && (
          <span className="inline" title="接続アプリの行から開いた条件">
            {treeLabels(form).map((l) => (
              <span key={l} className="badge gray">
                {l}
              </span>
            ))}
            <button
              type="button"
              onClick={() => {
                const cleared = Object.fromEntries(treeKeys.map((k) => [k, ''])) as Pick<Filters, (typeof treeKeys)[number]>
                setForm({ ...form, ...cleared })
                apply({ ...filters, ...cleared })
              }}
              title="接続アプリから開いた条件を外す"
            >
              ✕
            </button>
          </span>
        )}
        <div className="actions">
          <button type="submit" className="primary">
            検索
          </button>
          <button type="button" onClick={clear}>
            クリア
          </button>
        </div>
      </form>

      {error && <div className="alert error">{error}</div>}
      {excluded && <div className="alert info">{excluded}</div>}

      <section className="card">
        <div className="table-head">
          <span>
            {data ? `${data.total.toLocaleString()} 件` : loading ? '読み込み中…' : ''}
            <span className="muted">（ログオンが新しい順）</span>
          </span>
          {data && <Pager page={data.page} pageSize={data.pageSize} total={data.total} onChange={setPage} />}
        </div>
        <div className="table-wrap">
          <table className="hover">
            <thead>
              <tr>
                <th>状態</th>
                {targetId === undefined && <th>監視対象</th>}
                <th>ログオン</th>
                <th>切断</th>
                <th>接続時間</th>
                <th>実行端末</th>
                <th>アプリ</th>
                <th>実行ユーザ</th>
                <th>接続ユーザ</th>
                <th>モジュール</th>
                <th>種別</th>
              </tr>
            </thead>
            <tbody>
              {data?.items.map((r) =>
                r.type === 'session' ? (
                  <SessionRowView
                    key={`s${r.session.id}`}
                    s={r.session}
                    showTarget={targetId === undefined}
                    selected={selected === r.session.id}
                    onSelect={setSelected}
                  />
                ) : (
                  <UsageRowView
                    key={`u${r.usage.id}`}
                    u={r.usage}
                    targetName={r.targetName}
                    showTarget={targetId === undefined}
                    selected={selected !== undefined && selected === r.usage.matchedSessionId}
                    onSelect={setSelected}
                  />
                ),
              )}
              {data && data.items.length === 0 && (
                <tr>
                  <td colSpan={11} className="empty">
                    該当するセッションはありません
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>

      {selected !== undefined && <SessionDetailPanel id={selected} onClose={() => setSelected(undefined)} />}
    </div>
  )
}

function SessionRowView({
  s,
  showTarget,
  selected,
  onSelect,
}: {
  s: SessionListItem
  showTarget: boolean
  selected: boolean
  onSelect: (id: number) => void
}) {
  return (
    <tr onClick={() => onSelect(s.id)} className={selected ? 'selected' : ''}>
      <td>
        {s.isConnected ? (
          <span className={`badge ${s.status === 'ACTIVE' ? 'green' : 'blue'}`}>{s.status === 'ACTIVE' ? '実行中' : '接続中'}</span>
        ) : (
          <span className="badge gray">切断</span>
        )}
      </td>
      {showTarget && <td>{s.targetName}</td>}
      <td className="nowrap">{fmtDateTime(s.logonTime)}</td>
      <td className="nowrap">
        {fmtDateTime(s.disconnectedAt)}
        {s.disconnectUncertain && (
          <span className="badge amber" title="取得失敗や Batch 停止の後に検知したため、切断時刻は推定です">
            推定
          </span>
        )}
      </td>
      <td className="nowrap">{fmtDuration(s.logonTime, s.isConnected ? null : s.disconnectedAt)}</td>
      <td>{s.machine}</td>
      <td>
        <AppCell appName={s.appName} appDetail={s.appDetail} state={s.procInfoState} error={s.procInfoError} />
        {s.linkOriginApp && (
          <div className="link-origin" title="接続元 DB 上で DB Link を使っていたセッションのアプリ">
            DB Link 経由: <strong>{s.linkOriginApp}</strong>
            {(s.linkOriginMachine || s.linkOriginUser) && (
              <>
                （{s.linkOriginMachine}
                {s.linkOriginMachine && s.linkOriginUser && '・'}
                <UserName account={s.linkOriginUser} />）
              </>
            )}
          </div>
        )}
      </td>
      <td className="nowrap">
        <UserName account={s.procOwner ?? s.osUser} />
      </td>
      <td className="nowrap">
        {s.username}
        {s.schemaName && s.schemaName !== s.username && <div className="sub">スキーマ {s.schemaName}</div>}
      </td>
      <td className="ellipsis" title={s.module ?? ''}>
        {s.module}
      </td>
      <td>
        <KindBadge kind={s.processKind} />
        {s.jobName && <span className="badge purple">{s.jobName}</span>}
        {s.dbLinkOriginCount > 0 && <span className="badge green">接続元特定</span>}
      </td>
    </tr>
  )
}

/** DB Link 利用元の行（接続元 DB 上のセッション）。監視対象側のセッションを特定できていれば、クリックでその詳細を開く */
function UsageRowView({
  u,
  targetName,
  showTarget,
  selected,
  onSelect,
}: {
  u: DbLinkUsage
  targetName: string
  showTarget: boolean
  selected: boolean
  onSelect: (id: number) => void
}) {
  const matched = u.matchedSessionId
  return (
    <tr
      className={`usage-row ${selected ? 'selected' : ''} ${matched ? '' : 'no-detail'}`}
      onClick={matched ? () => onSelect(matched) : undefined}
      title={matched ? `クリックで監視対象側のセッション #${matched} を表示` : '監視対象側のセッションは特定できていません'}
    >
      <td>
        <span className="badge purple" title="DB Link 接続元 DB 上で DB Link を使っていたセッション">
          DB Link利用元
        </span>
      </td>
      {showTarget && <td>{targetName}</td>}
      <td className="nowrap">{fmtDateTime(u.logonTime)}</td>
      <td className="nowrap muted" title="接続元 DB 上で最後に DB Link の利用を確認した時刻">
        最終確認 {fmtDateTime(u.lastSeenAt)}
      </td>
      <td className="nowrap">{fmtDuration(u.logonTime, u.lastSeenAt)}</td>
      <td>{u.machine}</td>
      <td>
        <AppCell appName={u.appName} appDetail={u.appDetail} state={u.procInfoState} error={u.procInfoError} />
        {u.procCommandLine && (
          <div className="app-detail mono" title={u.procCommandLine}>
            {u.procCommandLine}
          </div>
        )}
      </td>
      <td className="nowrap">
        <UserName account={u.procOwner ?? u.osUser} />
      </td>
      <td className="nowrap">
        {u.username}
        {u.schemaName && u.schemaName !== u.username && <div className="sub">スキーマ {u.schemaName}</div>}
      </td>
      <td className="ellipsis" title={[u.module, u.sqlText].filter(Boolean).join('\n')}>
        {u.module}
        {u.sqlText && <div className="sub mono ellipsis">{u.sqlText}</div>}
      </td>
      <td>
        <span className="badge gray" title="接続元 DB（設定の DB Link 接続元）">
          {u.linkSourceName}
        </span>
        <span className="badge blue" title="DB Link">
          {u.dbLinkName}
        </span>
        <span className={`badge ${u.matchType === 'PROCESS' ? 'green' : 'amber'}`}>{matchTypeLabels[u.matchType] ?? u.matchType}</span>
      </td>
    </tr>
  )
}
