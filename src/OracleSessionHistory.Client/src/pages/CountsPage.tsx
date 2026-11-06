import { Fragment, useState } from 'react'
import { api, type ConnectionCountNode, type PeriodCounts } from '../api'
import { useAsync } from '../hooks'
import { fmtDate, fmtDateTime } from '../format'
import { navigate } from '../router'
import UserName from '../components/UserName'

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

const auditTitle = 'DBA_AUDIT_SESSION（監査証跡）のログオン件数を含みます（このシステムの履歴がまだ無い期間）'

/** 下の段の無い行（日別の列に件数の背景色を付ける） */
function leaves(nodes: ConnectionCountNode[]): ConnectionCountNode[] {
  return nodes.flatMap((n) => (n.children.length === 0 && n.kind !== 'machine' ? [n] : leaves(n.children)))
}

export default function CountsPage({ targetId }: { targetId?: number }) {
  const [input, setInput] = useState('')
  const [q, setQ] = useState('')
  const { data, error, loading } = useAsync(() => api.connectionCounts(targetId, q), [targetId, q], 60000)

  const nodes = data?.nodes ?? []
  const totals = columns.map((c) => nodes.reduce((a, n) => a + n[c.key], 0))
  const max = Math.max(1, ...leaves(nodes).flatMap((n) => columns.slice(0, 7).map((c) => n[c.key])))
  const audits = data?.audit ?? []

  /**
   * 1 行と、その下の段を再帰的に描く。
   * guides は上の段ごとに「その段がまだ続くか」（続く段は │ を引く）
   */
  const renderNode = (n: ConnectionCountNode, guides: boolean[], last: boolean): React.ReactNode => (
    <Fragment key={`${guides.length}|${n.kind}|${n.label}`}>
      <tr
        className={`tree-${n.kind} depth-${Math.min(guides.length, 4)}`}
        onClick={n.open ? () => navigate('/sessions', n.open!) : undefined}
        title={n.open ? 'クリックでセッション履歴を表示' : undefined}
      >
        <td>
          <div className="tree-cell">
            {guides.length > 0 && (
              <span className="tree-line">
                {guides.slice(1).map((g) => (g ? '│ ' : '  ')).join('')}
                {last ? '└ ' : '├ '}
              </span>
            )}
            <div className="tree-label">
              {n.kind === 'dblink' && <span className="tree-kind">DB Link</span>}
              <span className="tree-name" title={n.label}>
                {n.label}
              </span>
              {n.detail && (
                <div className="app-detail" title={n.detail}>
                  {n.detail}
                </div>
              )}
            </div>
          </div>
        </td>
        <td className="nowrap">
          <UserName account={n.owner} />
        </td>
        <td className={`num ${n.connected === 0 ? 'zero' : ''}`}>{n.connected}</td>
        <td className="nowrap">{fmtDateTime(n.lastSeenAt)}</td>
        {columns.map((c, i) => {
          const v = n[c.key]
          const audit = n.auditPeriods.includes(c.key)
          const leaf = n.children.length === 0 && n.kind !== 'machine'
          const heat = leaf && i < 7 && v > 0 ? 0.08 + (v / max) * 0.5 : 0
          return (
            <td
              key={c.key}
              className={`num ${i >= 7 ? 'sep' : ''} ${v === 0 ? 'zero' : ''}`}
              style={heat ? { background: `rgb(var(--heat) / ${heat})` } : undefined}
              title={audit ? auditTitle : undefined}
            >
              {audit && <span className="audit-mark">※</span>}
              {v.toLocaleString()}
            </td>
          )
        })}
      </tr>
      {n.children.map((c, i) => renderNode(c, [...guides, !last], i === n.children.length - 1))}
    </Fragment>
  )

  return (
    <div className="page">
      <div className="page-head">
        <h1>接続アプリ</h1>
        <span className="muted">
          実行端末 → 実行ファイル → IIS のプール・sqlplus の実行元の順に、ログオン回数（取得できたセッション数）を表示します。DB Link 経由の接続は、DB サーバーの ORACLE.EXE の下に DB Link 接続元 → 接続元の端末 → 接続元のアプリで表示します。Oracle 内部プロセス（ジョブ等）は含みません
        </span>
      </div>
      <form
        className="card filters"
        onSubmit={(e) => {
          e.preventDefault()
          setQ(input)
        }}
      >
        <input className="wide" placeholder="実行端末・アプリ・プール名・実行ファイル・DB Link 接続元で絞り込み" value={input} onChange={(e) => setInput(e.target.value)} />
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
              {a.targetName}：{a.from && a.lastDay ? `${fmtDate(a.from)} 〜 ${fmtDate(a.lastDay)} を補完` : '補完なし'}（取得 {fmtDateTime(a.fetchedAt)}）
              {a.error && <span className="error-text"> 取得できませんでした：{a.error}</span>}
            </div>
          ))}
        </div>
      )}
      <section className="card">
        <div className="table-head">
          <span>{data ? `${nodes.length} 端末` : loading ? '読み込み中…' : ''}</span>
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
            <tbody>
              {nodes.map((n, i) => renderNode(n, [], i === nodes.length - 1))}
              {data && nodes.length === 0 && (
                <tr>
                  <td colSpan={13} className="empty">
                    データがありません
                  </td>
                </tr>
              )}
            </tbody>
            {nodes.length > 0 && (
              <tfoot>
                <tr>
                  <td colSpan={2}>合計</td>
                  <td className="num">{nodes.reduce((a, n) => a + n.connected, 0).toLocaleString()}</td>
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
