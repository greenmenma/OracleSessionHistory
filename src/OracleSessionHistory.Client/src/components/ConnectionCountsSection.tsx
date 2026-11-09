import { Fragment, useState } from 'react'
import { api, type ConnectionCountNode, type PeriodCounts } from '../api'
import { useAsync } from '../hooks'
import { fmtDate, fmtDateTime } from '../format'
import { navigate } from '../router'
import { UserList } from './UserName'

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

const description =
  '実行端末 → 実行ファイル → IIS のプール・sqlplus の実行元の順に、ログオン回数（取得できたセッション数）を表示します。' +
  'DB Link 経由の接続は、DB サーバーの ORACLE.EXE の下に DB Link 接続元 → 接続元の端末 → 接続元のアプリで表示します。' +
  'Oracle 内部プロセス（ジョブ等）は含みません。担当者の欄をクリックすると対応担当者を設定できます'

/** 接続アプリ表（サマリーのメイン。表の中でスクロールし、見出し・合計・スクロールバーは常に見える） */
export default function ConnectionCountsSection({ targetId }: { targetId?: number }) {
  const [input, setInput] = useState('')
  const [q, setQ] = useState('')
  const { data, error, loading } = useAsync(() => api.connectionCounts(targetId, q), [targetId, q], 60000)
  const suggestions = useAsync(() => api.assignees(), [], undefined)
  // 保存した担当者（次の自動更新までの間も画面に反映する）
  const [saved, setSaved] = useState<Record<string, string | null>>({})
  const [saveError, setSaveError] = useState<string>()

  const saveAssignee = async (key: string, value: string) => {
    try {
      const r = await api.saveAssignee(key, value)
      setSaved((m) => ({ ...m, [key]: r.assignee }))
      setSaveError(undefined)
      void suggestions.reload()
    } catch (e) {
      setSaveError(`担当者を保存できませんでした：${e instanceof Error ? e.message : String(e)}`)
    }
  }

  const nodes = data?.nodes ?? []
  const totals = columns.map((c) => nodes.reduce((a, n) => a + n[c.key], 0))
  const max = Math.max(1, ...leaves(nodes).flatMap((n) => columns.slice(0, 7).map((c) => n[c.key])))
  const audits = data?.audit ?? []

  /**
   * 1 行と、その下の段を再帰的に描く。
   * guides は上の段ごとに「その段がまだ続くか」（続く段は │ を引く）
   */
  const renderNode = (n: ConnectionCountNode, guides: boolean[], last: boolean): React.ReactNode => (
    <Fragment key={n.key}>
      <tr className={`tree-${n.kind} depth-${Math.min(guides.length, 4)}`}>
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
          {n.open && (
            <button type="button" className="small" onClick={() => navigate('/sessions', n.open!)} title="この行のセッション履歴を表示">
              履歴 →
            </button>
          )}
        </td>
        <td className="assignee-cell">
          <AssigneeCell
            value={n.key in saved ? saved[n.key] : n.assignee}
            onSave={(v) => saveAssignee(n.key, v)}
          />
        </td>
        <td className="nowrap">
          <UserList accounts={n.owners} />
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
    <div id="connection-counts" className="counts-section">
      <div className="page-head">
        <h2>接続アプリ</h2>
        <span className="muted help" title={description}>
          ログオン回数（端末 → 実行ファイル → アプリ）ⓘ
        </span>
        <form
          className="inline-search"
          onSubmit={(e) => {
            e.preventDefault()
            setQ(input)
          }}
        >
          <input placeholder="実行端末・アプリ・プール名・実行ファイル・DB Link 接続元で絞り込み" value={input} onChange={(e) => setInput(e.target.value)} />
          <button type="submit" className="primary">
            絞り込み
          </button>
        </form>
      </div>
      {error && <div className="alert error">{error}</div>}
      {saveError && <div className="alert error">{saveError}</div>}
      {audits.length > 0 && (
        <div className="audit-note muted">
          <span className="audit-mark">※</span> の件数は、このシステムの履歴がまだ無い期間を DBA_AUDIT_SESSION（監査証跡）のログオン件数で補っています（端末の行のみ）。
          {audits.map((a) => (
            <span key={a.targetName}>
              {' '}
              {a.targetName}：{a.from && a.lastDay ? `${fmtDate(a.from)} 〜 ${fmtDate(a.lastDay)} を補完` : '補完なし'}（取得 {fmtDateTime(a.fetchedAt)}）
              {a.error && <span className="error-text"> 取得できませんでした：{a.error}</span>}
            </span>
          ))}
        </div>
      )}
      <datalist id="assignee-suggestions">
        {suggestions.data?.map((a) => (
          <option key={a} value={a} />
        ))}
      </datalist>
      <section className="card counts-card">
        <div className="table-head">
          <span>{data ? `${nodes.length} 端末` : loading ? '読み込み中…' : ''}</span>
        </div>
        <div className="table-wrap">
          <table className="counts tree">
            <thead>
              <tr>
                <th>実行端末 / アプリ</th>
                <th>セッション履歴</th>
                <th>担当者</th>
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
                  <td colSpan={15} className="empty">
                    データがありません
                  </td>
                </tr>
              )}
            </tbody>
            {nodes.length > 0 && (
              <tfoot>
                <tr>
                  <td colSpan={4}>合計</td>
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

/** 対応担当者の欄。クリックで入力し、Enter・フォーカスを外すと保存、Esc で取り消す（空にすると外す） */
function AssigneeCell({ value, onSave }: { value: string | null; onSave: (v: string) => Promise<void> }) {
  const [editing, setEditing] = useState(false)
  const [text, setText] = useState('')
  const [saving, setSaving] = useState(false)

  const start = () => {
    setText(value ?? '')
    setEditing(true)
  }
  const commit = async () => {
    if (!editing || saving) return
    if (text.trim() === (value ?? '')) {
      setEditing(false)
      return
    }
    setSaving(true)
    await onSave(text)
    setSaving(false)
    setEditing(false)
  }

  if (editing)
    return (
      <input
        className="assignee-input"
        autoFocus
        list="assignee-suggestions"
        maxLength={100}
        value={text}
        disabled={saving}
        placeholder="担当者（空で解除）"
        onChange={(e) => setText(e.target.value)}
        onBlur={() => void commit()}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault()
            void commit()
          } else if (e.key === 'Escape') setEditing(false)
        }}
      />
    )
  return (
    <button type="button" className={`assignee ${value ? '' : 'empty'}`} onClick={start} title="クリックして対応担当者を設定">
      {value ?? '＋'}
    </button>
  )
}
