import { Fragment, useEffect, useState } from 'react'
import { api, UNASSIGNED, type Assignee, type ConnectionCountNode, type PeriodCounts } from '../api'
import { useAsync } from '../hooks'
import { fmtDate, fmtDateTime } from '../format'
import { navigate } from '../router'
import { UserList } from './UserName'
import PersonPicker, { PersonChip } from './PersonPicker'

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

/** 先頭（実行端末 / アプリ・担当者・実行ユーザ・接続中・最終確認）と末尾（セッション履歴）の列数 */
const leadColumns = 5
const columnCount = leadColumns + columns.length + 1

const auditTitle = 'DBA_AUDIT_SESSION（監査証跡）のログオン件数'
const historyTitle = 'このシステムが取得したセッション数（監査証跡の件数が無い期間・絞り込みで端末名が一致しない端末）'

const description =
  '実行端末 → 実行ファイル → IIS のプール・sqlplus の実行元の順に、ログオン回数を表示します。' +
  '端末の行は DBA_AUDIT_SESSION（監査証跡）のログオン件数、下の段はこのシステムが取得したセッション数です（短いセッションは取りこぼすため、端末の行より少なくなることがあります）。' +
  'DB Link 経由の接続は、DB サーバーの ORACLE.EXE の下に DB Link 接続元 → 接続元の端末 → 接続元のアプリで表示します。' +
  'Oracle 内部プロセス（ジョブ等）は含みません。▾ / ▸ で展開・縮小（Shift+クリックで下の段もまとめて）、右上のボタンで段ごとに展開・縮小できます。✎ または担当者の欄をクリックすると、日本語名・メモ・担当者（メイン・サブ）を設定できます'

/** 下の段の無い行（日別の列に件数の背景色を付ける） */
function leaves(nodes: ConnectionCountNode[]): ConnectionCountNode[] {
  return nodes.flatMap((n) => (n.children.length === 0 && n.kind !== 'machine' ? [n] : leaves(n.children)))
}

/** 下の段を持つ行のキーと段（0 が端末） */
function branches(nodes: ConnectionCountNode[], depth = 0): { key: string; depth: number }[] {
  return nodes.flatMap((n) => (n.children.length === 0 ? [] : [{ key: n.key, depth }, ...branches(n.children, depth + 1)]))
}

/** 段の名前（展開ボタンの説明） */
const levelNames = ['端末', '実行ファイル', 'プール・実行元・DB Link', '接続元の端末', '接続元の実行ファイル', '接続元のプール・実行元']

/** 接続アプリ表（サマリーのメイン。表の中でスクロールし、見出し・合計・スクロールバーは常に見える） */
export default function ConnectionCountsSection({ targetId }: { targetId?: number }) {
  const [input, setInput] = useState('')
  const [q, setQ] = useState('')
  const [assignee, setAssignee] = useState('')
  const { data, error, loading, reload } = useAsync(
    () => api.connectionCounts(targetId, q, assignee || undefined),
    [targetId, q, assignee],
    60000,
  )
  const suggestions = useAsync(() => api.assignees(), [], undefined)
  const [editing, setEditing] = useState<ConnectionCountNode>()
  // 縮小している行のキー（既定は全展開）
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set())

  const nodes = data?.nodes ?? []
  const totals = columns.map((c) => nodes.reduce((a, n) => a + n[c.key], 0))
  const max = Math.max(1, ...leaves(nodes).flatMap((n) => columns.slice(0, 7).map((c) => n[c.key])))
  const audits = data?.audit ?? []
  const names = suggestions.data ?? []
  const allBranches = branches(nodes)
  const levels = allBranches.length === 0 ? 0 : Math.max(...allBranches.map((b) => b.depth)) + 1

  /** level 段目まで表示する（それより下の段を持つ行を縮小する。0 は端末だけ） */
  const showLevel = (level: number) => setCollapsed(new Set(allBranches.filter((b) => b.depth >= level).map((b) => b.key)))

  /** 行の展開・縮小。deep のときはその行の下の段もまとめて同じ状態にする */
  const toggle = (n: ConnectionCountNode, deep: boolean) => {
    const next = new Set(collapsed)
    const close = !collapsed.has(n.key)
    for (const k of deep ? [n.key, ...branches(n.children).map((b) => b.key)] : [n.key]) {
      if (close) next.add(k)
      else next.delete(k)
    }
    setCollapsed(next)
  }

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
            {n.children.length > 0 ? (
              <button
                type="button"
                className="tree-toggle"
                onClick={(e) => toggle(n, e.shiftKey)}
                title={`${collapsed.has(n.key) ? '展開' : '縮小'}（Shift+クリックで下の段もまとめて）`}
                aria-expanded={!collapsed.has(n.key)}
              >
                {collapsed.has(n.key) ? '▸' : '▾'}
              </button>
            ) : (
              <span className="tree-toggle-space" />
            )}
            <div className="tree-label">
              {n.kind === 'dblink' && <span className="tree-kind">DB Link</span>}
              {n.note?.displayName && <span className="tree-display-name">{n.note.displayName}</span>}
              <span className={`tree-name ${n.note?.displayName ? 'sub-name' : ''}`} title={n.label}>
                {n.label}
              </span>
              <button type="button" className="note-edit" onClick={() => setEditing(n)} title="日本語名・メモ・担当者を設定">
                ✎
              </button>
              {n.detail && (
                <div className="app-detail" title={n.detail}>
                  {n.detail}
                </div>
              )}
              {n.note?.memo && (
                <div className="tree-memo" title={n.note.memo}>
                  {n.note.memo}
                </div>
              )}
            </div>
          </div>
        </td>
        <td className="assignee-cell">
          <button type="button" className={`assignee ${n.note?.mainAssignee || n.note?.subAssignees.length ? '' : 'empty'}`} onClick={() => setEditing(n)} title="クリックして担当者を設定">
            {n.note?.mainAssignee || n.note?.subAssignees.length ? (
              <>
                {n.note.mainAssignee && (
                  <span className="assignee-main" title={n.note.mainAssignee.account}>
                    {n.note.mainAssignee.displayName}
                  </span>
                )}
                {n.note.subAssignees.length > 0 && (
                  <span className="assignee-sub" title={n.note.subAssignees.map((a) => a.account).join('\n')}>
                    サブ {n.note.subAssignees.map((a) => a.displayName).join('、')}
                  </span>
                )}
              </>
            ) : (
              '＋'
            )}
          </button>
        </td>
        <td className="nowrap">
          <UserList accounts={n.owners} />
        </td>
        <td className={`num ${n.connected === 0 ? 'zero' : ''}`}>{n.connected}</td>
        <td className="nowrap">{fmtDateTime(n.lastSeenAt)}</td>
        {columns.map((c, i) => {
          const v = n[c.key]
          const audit = n.auditPeriods.includes(c.key)
          // 端末の行で監査の件数を使えなかった列（履歴の件数）には ※ を付ける
          const fromHistory = n.kind === 'machine' && guides.length === 0 && !audit
          const leaf = n.children.length === 0 && n.kind !== 'machine'
          const heat = leaf && i < 7 && v > 0 ? 0.08 + (v / max) * 0.5 : 0
          return (
            <td
              key={c.key}
              className={`num ${i >= 7 ? 'sep' : ''} ${v === 0 ? 'zero' : ''}`}
              style={heat ? { background: `rgb(var(--heat) / ${heat})` } : undefined}
              title={audit ? auditTitle : fromHistory ? historyTitle : undefined}
            >
              {fromHistory && audits.length > 0 && <span className="audit-mark">※</span>}
              {v.toLocaleString()}
            </td>
          )
        })}
        <td className="nowrap sep sticky-right">
          {n.open && (
            <button type="button" className="small" onClick={() => navigate('/sessions', n.open!)} title="この行のセッション履歴を表示">
              履歴 →
            </button>
          )}
        </td>
      </tr>
      {!collapsed.has(n.key) && n.children.map((c, i) => renderNode(c, [...guides, !last], i === n.children.length - 1))}
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
          <select value={assignee} onChange={(e) => setAssignee(e.target.value)} title="担当者（メイン・サブ）で絞り込み">
            <option value="">担当者：すべて</option>
            {names.map((a) => (
              <option key={a.account} value={a.account}>
                {a.displayName}
              </option>
            ))}
            <option value={UNASSIGNED}>担当者：未設定</option>
          </select>
          <input placeholder="実行端末・アプリ・プール名・実行ファイル・DB Link 接続元で絞り込み" value={input} onChange={(e) => setInput(e.target.value)} />
          <button type="submit" className="primary">
            絞り込み
          </button>
        </form>
      </div>
      {error && <div className="alert error">{error}</div>}
      {audits.length > 0 && (
        <div className="audit-note muted">
          端末の行の件数は DBA_AUDIT_SESSION（監査証跡）のログオン件数です（下の段はこのシステムが取得したセッション数）。
          <span className="audit-mark">※</span> は監査証跡の件数が無く、このシステムの履歴の件数を表示している列です。
          {audits.map((a) => (
            <span key={a.targetName}>
              {' '}
              {a.targetName}：{a.from && a.lastDay ? `${fmtDate(a.from)} 〜 ${fmtDate(a.lastDay)}` : '監査の件数なし'}（取得 {fmtDateTime(a.fetchedAt)}）
              {a.error && <span className="error-text"> 取得できませんでした：{a.error}</span>}
            </span>
          ))}
        </div>
      )}
      <section className="card counts-card">
        <div className="table-head">
          <span>
            {data ? `${nodes.length} 端末` : loading ? '読み込み中…' : ''}
            {assignee && (
              <span className="muted">
                （担当者 {assignee === UNASSIGNED ? '未設定' : (names.find((a) => a.account === assignee)?.displayName ?? assignee)} の行と、その上の段。合計は表示している端末の件数）
              </span>
            )}
          </span>
          {levels > 0 && (
            <span className="tree-levels">
              <button type="button" className="small" onClick={() => setCollapsed(new Set())} title="すべての段を表示">
                全展開
              </button>
              {Array.from({ length: levels }, (_, i) => (
                <button
                  key={i}
                  type="button"
                  className="small"
                  onClick={() => showLevel(i)}
                  title={i === 0 ? '端末だけを表示（全縮小）' : `${levelNames[i] ?? `${i + 1} 段目`}までを表示`}
                >
                  {i === 0 ? '全縮小' : `${i + 1} 段目まで`}
                </button>
              ))}
            </span>
          )}
        </div>
        <div className="table-wrap">
          <table className="counts tree">
            <thead>
              <tr>
                <th>実行端末 / アプリ</th>
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
                <th className="sep sticky-right">セッション履歴</th>
              </tr>
            </thead>
            <tbody>
              {nodes.map((n, i) => renderNode(n, [], i === nodes.length - 1))}
              {data && nodes.length === 0 && (
                <tr>
                  <td colSpan={columnCount} className="empty">
                    データがありません
                  </td>
                </tr>
              )}
            </tbody>
            {nodes.length > 0 && (
              <tfoot>
                <tr>
                  <td colSpan={3}>合計</td>
                  <td className="num">{nodes.reduce((a, n) => a + n.connected, 0).toLocaleString()}</td>
                  <td />
                  {totals.map((t, i) => (
                    <td key={i} className={`num ${i >= 7 ? 'sep' : ''}`}>
                      {t.toLocaleString()}
                    </td>
                  ))}
                  <td className="sep sticky-right" />
                </tr>
              </tfoot>
            )}
          </table>
        </div>
      </section>

      {editing && (
        <NodeNoteEditor
          node={editing}
          onClose={() => setEditing(undefined)}
          onSaved={() => {
            setEditing(undefined)
            void reload()
            void suggestions.reload()
          }}
        />
      )}
    </div>
  )
}

/** 行の日本語名・メモ・担当者（AD から選ぶ。メイン 1 人・サブ複数）の編集。すべて空にして保存すると設定を外す */
function NodeNoteEditor({ node, onClose, onSaved }: { node: ConnectionCountNode; onClose: () => void; onSaved: () => void }) {
  const [displayName, setDisplayName] = useState(node.note?.displayName ?? '')
  const [memo, setMemo] = useState(node.note?.memo ?? '')
  const [main, setMain] = useState<Assignee | null>(node.note?.mainAssignee ?? null)
  const [subs, setSubs] = useState<Assignee[]>(node.note?.subAssignees ?? [])
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string>()

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const chosen = [...(main ? [main.account] : []), ...subs.map((s) => s.account)]

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setSaving(true)
    try {
      await api.saveNodeNote(node.key, {
        displayName: displayName.trim() || null,
        memo: memo.trim() || null,
        mainAssignee: main,
        subAssignees: subs,
      })
      onSaved()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setSaving(false)
    }
  }

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <form className="modal" onSubmit={save} onClick={(e) => e.stopPropagation()}>
        <h2>行の情報</h2>
        <div className="muted node-path">{node.key.split('\n').map((p) => p.replace(/^[a-z]+:/, '')).join(' → ')}</div>
        {error && <div className="alert error">{error}</div>}
        <label>
          日本語名
          <input autoFocus maxLength={100} value={displayName} placeholder="例：受注管理システム" onChange={(e) => setDisplayName(e.target.value)} />
        </label>
        <label>
          メモ
          <textarea
            className="memo-input"
            rows={3}
            maxLength={1000}
            value={memo}
            placeholder="例：営業部の受注入力画面。問い合わせは情報システム課"
            onChange={(e) => setMemo(e.target.value)}
          />
        </label>
        <div className="row">
          <div className="field">
            担当者（メイン）
            {main ? <PersonChip person={main} onRemove={() => setMain(null)} /> : <PersonPicker exclude={chosen} onSelect={setMain} />}
          </div>
          <div className="field">
            担当者（サブ・複数可）
            {subs.length > 0 && (
              <div className="person-chips">
                {subs.map((p) => (
                  <PersonChip key={p.account} person={p} onRemove={() => setSubs(subs.filter((s) => s.account !== p.account))} />
                ))}
              </div>
            )}
            {subs.length < 10 && <PersonPicker exclude={chosen} onSelect={(p) => setSubs([...subs, p])} />}
          </div>
        </div>
        <div className="muted">担当者は AD から検索して選びます。すべて空にして保存すると、この行の設定を外します。</div>
        <div className="actions">
          <button type="submit" className="primary" disabled={saving}>
            保存
          </button>
          <button type="button" onClick={onClose}>
            キャンセル
          </button>
        </div>
      </form>
    </div>
  )
}
