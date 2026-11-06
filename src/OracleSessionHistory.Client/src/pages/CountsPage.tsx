import { useState } from 'react'
import { api, type ConnectionCountGroup, type ConnectionCountRow, type PeriodCounts } from '../api'
import { useAsync } from '../hooks'
import { fmtDate, fmtDateTime } from '../format'
import AppCell from '../components/AppCell'
import { navigate } from '../router'

type CountKey = keyof PeriodCounts

const columns: { key: CountKey; label: string; period: string }[] = [
  { key: 'today', label: '本日', period: 'today' },
  { key: 'd1', label: '1日前', period: 'd1' },
  { key: 'd2', label: '2日前', period: 'd2' },
  { key: 'd3', label: '3日前', period: 'd3' },
  { key: 'd4', label: '4日前', period: 'd4' },
  { key: 'd5', label: '5日前', period: 'd5' },
  { key: 'd6', label: '6日前', period: 'd6' },
  { key: 'month1', label: '本日〜1ヶ月', period: 'month1Start' },
  { key: 'month2', label: '1ヶ月〜2ヶ月', period: 'month2Start' },
]

/** 端末なしの行を一覧で完全一致で開くための値（Server の SessionEndpoints.UnknownMachine） */
const unknownMachine = '(不明)'
const auditTitle = 'DBA_AUDIT_SESSION（監査証跡）のログオン件数を含みます（このシステムの履歴がまだ無い期間）'

/** 親の行（端末・DB Link 経由）からセッション履歴を開く */
function openGroup(g: ConnectionCountGroup) {
  if (g.kind === 'dblink') navigate('/sessions', { via: g.machine || unknownMachine })
  else navigate('/sessions', { machine: g.machine || unknownMachine, exact: 'true', via: 'direct' })
}

/** 子の行（アプリ）からセッション履歴を開く。部分一致だと WEB01 で WEB011 も出るため完全一致で開く */
function openRow(r: ConnectionCountRow) {
  navigate('/sessions', { machine: r.machine || unknownMachine, app: r.appName, exact: 'true', via: r.linkVia ?? 'direct' })
}

export default function CountsPage({ targetId }: { targetId?: number }) {
  const [input, setInput] = useState('')
  const [q, setQ] = useState('')
  const { data, error, loading } = useAsync(() => api.connectionCounts(targetId, q), [targetId, q], 60000)

  const groups = data?.groups ?? []
  const totals = columns.map((c) => groups.reduce((a, g) => a + g[c.key], 0))
  const max = Math.max(1, ...groups.flatMap((g) => g.children.flatMap((r) => columns.slice(0, 7).map((c) => r[c.key]))))
  const audits = data?.audit ?? []

  return (
    <div className="page">
      <div className="page-head">
        <h1>接続アプリ</h1>
        <span className="muted">
          実行端末ごとに、アプリ別のログオン回数（取得できたセッション数）を表示します。DB Link 経由の接続は、経由した DB サーバーの下に接続元の端末・アプリを表示します。Oracle 内部プロセス（ジョブ等）は含みません
        </span>
      </div>
      <form
        className="card filters"
        onSubmit={(e) => {
          e.preventDefault()
          setQ(input)
        }}
      >
        <input className="wide" placeholder="実行端末・アプリ・プール名・実行ファイルで絞り込み" value={input} onChange={(e) => setInput(e.target.value)} />
        <div className="actions">
          <button type="submit" className="primary">
            絞り込み
          </button>
        </div>
      </form>
      {error && <div className="alert error">{error}</div>}
      {audits.length > 0 && (
        <div className="card audit-note">
          <span className="audit-mark">※</span> の件数は、このシステムの履歴がまだ無い期間を DBA_AUDIT_SESSION（監査証跡）のログオン件数で補っています（端末の行のみ。アプリ別には分けられません）。
          {audits.map((a) => (
            <div key={a.targetName} className="muted">
              {a.targetName}：
              {a.from && a.lastDay ? `${fmtDate(a.from)} 〜 ${fmtDate(a.lastDay)} を補完` : '補完なし'}
              （取得 {fmtDateTime(a.fetchedAt)}）
              {a.error && <span className="error-text"> 取得できませんでした：{a.error}</span>}
            </div>
          ))}
        </div>
      )}
      <section className="card">
        <div className="table-head">
          <span>{data ? `${groups.length} 端末・${groups.reduce((a, g) => a + g.children.length, 0)} アプリ` : loading ? '読み込み中…' : ''}</span>
        </div>
        <div className="table-wrap">
          <table className="counts tree hover">
            <thead>
              <tr>
                <th>実行端末 / アプリ</th>
                <th>実行ユーザ</th>
                <th className="num">接続中</th>
                <th>最終確認</th>
                {columns.map((c) => (
                  <th key={c.key} className="num">
                    {c.label}
                    {data && c.key !== 'month1' && c.key !== 'month2' && (
                      <div className="th-sub">{fmtDate(data.periods[c.period]).slice(5)}</div>
                    )}
                  </th>
                ))}
              </tr>
            </thead>
            {groups.map((g) => (
              <tbody key={`${g.kind}|${g.machine}`} className="tree-group">
                <tr className="tree-parent" onClick={() => openGroup(g)} title="クリックでセッション履歴を表示">
                  <td>
                    {g.kind === 'dblink' ? (
                      <>
                        <span className="tree-kind">DB Link 経由</span>
                        {g.machine || '(不明)'}
                      </>
                    ) : (
                      g.machine || '(不明)'
                    )}
                  </td>
                  <td />
                  <td className={`num ${g.connected === 0 ? 'zero' : ''}`}>{g.connected}</td>
                  <td className="nowrap">{fmtDateTime(g.lastSeenAt)}</td>
                  {columns.map((c, i) => {
                    const v = g[c.key]
                    const audit = g.auditPeriods.includes(c.key)
                    return (
                      <td
                        key={c.key}
                        className={`num ${i >= 7 ? 'sep' : ''} ${v === 0 ? 'zero' : ''}`}
                        title={audit ? auditTitle : undefined}
                      >
                        {audit && <span className="audit-mark">※</span>}
                        {v.toLocaleString()}
                      </td>
                    )
                  })}
                </tr>
                {g.children.map((r, idx) => (
                  <tr key={`${r.machine}|${r.appName}`} className="tree-child" onClick={() => openRow(r)} title="クリックでセッション履歴を表示">
                    <td>
                      <div className="tree-cell">
                        <span className="tree-line">{idx === g.children.length - 1 ? '└' : '├'}</span>
                        <div>
                          {r.linkVia !== null && <div className="tree-origin">{r.machine || '(不明)'}</div>}
                          <AppCell appName={r.appName} appDetail={r.appDetail} />
                        </div>
                      </div>
                    </td>
                    <td className="nowrap">{r.owner}</td>
                    <td className={`num ${r.connected === 0 ? 'zero' : ''}`}>{r.connected}</td>
                    <td className="nowrap">{fmtDateTime(r.lastSeenAt)}</td>
                    {columns.map((c, i) => {
                      const v = r[c.key]
                      const heat = i < 7 && v > 0 ? 0.08 + (v / max) * 0.5 : 0
                      return (
                        <td
                          key={c.key}
                          className={`num ${i >= 7 ? 'sep' : ''} ${v === 0 ? 'zero' : ''}`}
                          style={heat ? { background: `rgb(var(--heat) / ${heat})` } : undefined}
                        >
                          {v.toLocaleString()}
                        </td>
                      )
                    })}
                  </tr>
                ))}
              </tbody>
            ))}
            {data && groups.length === 0 && (
              <tbody>
                <tr>
                  <td colSpan={13} className="empty">
                    データがありません
                  </td>
                </tr>
              </tbody>
            )}
            {groups.length > 0 && (
              <tfoot>
                <tr>
                  <td colSpan={2}>合計</td>
                  <td className="num">{groups.reduce((a, g) => a + g.connected, 0).toLocaleString()}</td>
                  <td />
                  {totals.map((t, i) => (
                    <td key={i} className={`num ${i >= 7 ? 'sep' : ''}`}>
                      {t.toLocaleString()}
                    </td>
                  ))}
                </tr>
              </tfoot>
            )}
          </table>
        </div>
      </section>
    </div>
  )
}
