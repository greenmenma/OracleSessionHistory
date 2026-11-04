import { useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { fmtDateTime, matchTypeLabels } from '../format'
import Pager from '../components/Pager'
import SessionDetailPanel from '../components/SessionDetailPanel'

export default function DbLinkPage({ targetId }: { targetId?: number }) {
  const [input, setInput] = useState('')
  const [q, setQ] = useState('')
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<number>()
  const { data, error } = useAsync(() => api.dbLinkUsages({ targetId, q, page, pageSize: 50 }), [targetId, q, page], 15000)

  return (
    <div className="page">
      <div className="page-head">
        <h1>DB Link利用元</h1>
        <span className="muted">接続元 DB 上で DB Link を使っていたセッション（設定 → DB Link 接続元 で登録）</span>
      </div>
      <form
        className="card filters"
        onSubmit={(e) => {
          e.preventDefault()
          setQ(input)
          setPage(1)
        }}
      >
        <input className="wide" placeholder="端末・プログラム・ユーザ・DB Link・SQL で絞り込み" value={input} onChange={(e) => setInput(e.target.value)} />
        <div className="actions">
          <button type="submit" className="primary">
            絞り込み
          </button>
        </div>
      </form>
      {error && <div className="alert error">{error}</div>}
      <section className="card">
        <div className="table-head">
          <span>{data ? `${data.total.toLocaleString()} 件` : ''}</span>
          {data && <Pager page={data.page} pageSize={data.pageSize} total={data.total} onChange={setPage} />}
        </div>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>最終確認</th>
                <th>接続元DB</th>
                <th>DB Link</th>
                <th>特定方法</th>
                <th>実行端末</th>
                <th>実行プログラム</th>
                <th>接続ユーザ</th>
                <th>OSユーザ</th>
                <th>ログオン</th>
                <th>SQL</th>
                <th>監視対象側</th>
              </tr>
            </thead>
            <tbody>
              {data?.items.map((u) => (
                <tr key={u.id}>
                  <td className="nowrap">{fmtDateTime(u.lastSeenAt)}</td>
                  <td>{u.linkSourceName}</td>
                  <td>{u.dbLinkName}</td>
                  <td>
                    <span className={`badge ${u.matchType === 'PROCESS' ? 'green' : 'amber'}`}>
                      {matchTypeLabels[u.matchType] ?? u.matchType}
                    </span>
                  </td>
                  <td>{u.machine}</td>
                  <td>{u.program}</td>
                  <td>{u.username}</td>
                  <td>{u.osUser}</td>
                  <td className="nowrap">{fmtDateTime(u.logonTime)}</td>
                  <td className="ellipsis mono" title={u.sqlText ?? ''}>
                    {u.sqlText}
                  </td>
                  <td>
                    {u.matchedSessionId && (
                      <button className="link" onClick={() => setSelected(u.matchedSessionId!)}>
                        セッション #{u.matchedSessionId}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
              {data && data.items.length === 0 && (
                <tr>
                  <td colSpan={11} className="empty">
                    データがありません
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
