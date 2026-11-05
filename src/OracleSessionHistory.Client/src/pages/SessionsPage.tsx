import { useEffect, useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { fmtDateTime, fmtDuration, internalKinds, kindLabels } from '../format'
import SessionDetailPanel from '../components/SessionDetailPanel'
import KindBadge from '../components/KindBadge'
import Pager from '../components/Pager'
import AppCell from '../components/AppCell'

interface Filters {
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
}

function fromQuery(query: URLSearchParams): Filters {
  return {
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
  }
}

export default function SessionsPage({ targetId, query }: { targetId?: number; query: URLSearchParams }) {
  const queryKey = query.toString()
  const [form, setForm] = useState<Filters>(() => fromQuery(query))
  const [filters, setFilters] = useState<Filters>(() => fromQuery(query))
  const [page, setPage] = useState(1)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [selected, setSelected] = useState<number>()

  // サマリー等からの遷移でクエリが変わった場合は条件を置き換える
  useEffect(() => {
    const f = fromQuery(new URLSearchParams(queryKey))
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
        page,
        pageSize: 50,
      }),
    [targetId, JSON.stringify(filters), page],
    autoRefresh ? 10000 : undefined,
  )

  const set = (k: Exclude<keyof Filters, 'includeInternal'>) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
    setForm({ ...form, [k]: e.target.value })

  const search = (e: React.FormEvent) => {
    e.preventDefault()
    setFilters(form)
    setPage(1)
  }

  const clear = () => {
    const empty = fromQuery(new URLSearchParams())
    setForm(empty)
    setFilters(empty)
    setPage(1)
  }

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
        <input
          className="wide"
          placeholder="キーワード（端末・アプリ・プール名・実行ファイル・ユーザ・モジュール・SQL など）"
          value={form.q}
          onChange={set('q')}
        />
        <select value={form.state} onChange={set('state')}>
          <option value="">状態：すべて</option>
          <option value="connected">接続中</option>
          <option value="disconnected">切断済み</option>
        </select>
        <select value={form.kind} onChange={set('kind')}>
          <option value="">種別：すべて</option>
          {Object.entries(kindLabels)
            .filter(([k]) => !internalKinds.includes(k))
            .map(([k, v]) => (
              <option key={k} value={k}>
                {v}
              </option>
            ))}
          <optgroup label="Oracle 内部プロセス">
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
        <input placeholder="実行ユーザ" value={form.osUser} onChange={set('osUser')} />
        <label className="inline">
          期間
          <input type="datetime-local" value={form.from} onChange={set('from')} />〜
          <input type="datetime-local" value={form.to} onChange={set('to')} />
        </label>
        <label className="check" title="ORACLE.EXE (J000) などのジョブ・パラレル実行・バックグラウンドプロセス。DB Link の可能性があるものは常に表示します">
          <input
            type="checkbox"
            checked={form.includeInternal}
            onChange={(e) => {
              // チェックの切り替えはすぐに反映する
              const next = { ...form, includeInternal: e.target.checked }
              setForm(next)
              setFilters({ ...filters, includeInternal: e.target.checked })
              setPage(1)
            }}
          />
          Oracle 内部プロセスも表示
        </label>
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
              {data?.items.map((s) => (
                <tr key={s.id} onClick={() => setSelected(s.id)} className={selected === s.id ? 'selected' : ''}>
                  <td>
                    {s.isConnected ? (
                      <span className={`badge ${s.status === 'ACTIVE' ? 'green' : 'blue'}`}>
                        {s.status === 'ACTIVE' ? '実行中' : '接続中'}
                      </span>
                    ) : (
                      <span className="badge gray">切断</span>
                    )}
                  </td>
                  {targetId === undefined && <td>{s.targetName}</td>}
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
                  </td>
                  <td className="nowrap">{s.procOwner ?? s.osUser}</td>
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
              ))}
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
