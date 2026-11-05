import { useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { fmtDate, fmtDateTime } from '../format'
import AppCell from '../components/AppCell'
import { navigate } from '../router'

type CountKey = 'today' | 'd1' | 'd2' | 'd3' | 'd4' | 'd5' | 'd6' | 'month1' | 'month2'

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

export default function CountsPage({ targetId }: { targetId?: number }) {
  const [input, setInput] = useState('')
  const [q, setQ] = useState('')
  const { data, error, loading } = useAsync(() => api.connectionCounts(targetId, q), [targetId, q], 60000)

  const totals = columns.map((c) => (data?.rows ?? []).reduce((a, r) => a + r[c.key], 0))
  const max = Math.max(1, ...(data?.rows ?? []).flatMap((r) => columns.slice(0, 7).map((c) => r[c.key])))

  return (
    <div className="page">
      <div className="page-head">
        <h1>接続アプリ</h1>
        <span className="muted">
          実行端末 × アプリごとのログオン回数（取得できたセッション数）。Oracle 内部プロセス（ジョブ等）は含みません
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
      <section className="card">
        <div className="table-head">
          <span>{data ? `${data.rows.length} 行` : loading ? '読み込み中…' : ''}</span>
        </div>
        <div className="table-wrap">
          <table className="counts hover">
            <thead>
              <tr>
                <th>実行端末</th>
                <th>アプリ</th>
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
              {data?.rows.map((r) => (
                <tr
                  key={`${r.machine}|${r.appName}`}
                  onClick={() => navigate('/sessions', { machine: r.machine ?? '', app: r.appName })}
                  title="クリックでセッション履歴を表示"
                >
                  <td>{r.machine ?? '(不明)'}</td>
                  <td>
                    <AppCell appName={r.appName} appDetail={r.appDetail} />
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
              {data && data.rows.length === 0 && (
                <tr>
                  <td colSpan={14} className="empty">
                    データがありません
                  </td>
                </tr>
              )}
            </tbody>
            {data && data.rows.length > 0 && (
              <tfoot>
                <tr>
                  <td colSpan={3}>合計</td>
                  <td className="num">{(data?.rows ?? []).reduce((a, r) => a + r.connected, 0).toLocaleString()}</td>
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
