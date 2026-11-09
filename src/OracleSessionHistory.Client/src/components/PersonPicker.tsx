import { useEffect, useRef, useState } from 'react'
import { api, type Assignee, type DirectoryPerson } from '../api'

/**
 * AD から人を検索して選ぶ入力欄。入力が止まってから検索し、候補をクリック（または ↑↓ と Enter）で選ぶ。
 * 自由入力の文字列は選べない（AD の人だけ）
 */
export default function PersonPicker({
  onSelect,
  exclude = [],
  placeholder = '名前・アカウント・メールで AD を検索',
}: {
  onSelect: (p: Assignee) => void
  /** 候補から除く人（選択済みの人）のアカウント */
  exclude?: string[]
  placeholder?: string
}) {
  const [text, setText] = useState('')
  const [results, setResults] = useState<DirectoryPerson[]>([])
  const [searching, setSearching] = useState(false)
  const [error, setError] = useState<string>()
  const [active, setActive] = useState(0)
  const [open, setOpen] = useState(false)
  // 古い検索の結果で新しい結果を上書きしないよう、最後に出した検索だけを採る
  const seq = useRef(0)

  useEffect(() => {
    const q = text.trim()
    if (!q) {
      setResults([])
      setError(undefined)
      return
    }
    const id = ++seq.current
    const t = window.setTimeout(async () => {
      setSearching(true)
      try {
        const r = await api.searchPeople(q)
        if (id !== seq.current) return
        setResults(r)
        setError(undefined)
        setActive(0)
      } catch (e) {
        if (id !== seq.current) return
        setResults([])
        setError(e instanceof Error ? e.message : String(e))
      } finally {
        if (id === seq.current) setSearching(false)
      }
    }, 300)
    return () => window.clearTimeout(t)
  }, [text])

  const excluded = new Set(exclude.map((a) => a.toUpperCase()))
  const candidates = results.filter((p) => !excluded.has(p.account.toUpperCase()))

  const select = (p: DirectoryPerson) => {
    onSelect({ account: p.account, displayName: p.displayName })
    setText('')
    setResults([])
    setOpen(false)
  }

  return (
    <div className="person-picker">
      <input
        value={text}
        placeholder={placeholder}
        onChange={(e) => {
          setText(e.target.value)
          setOpen(true)
        }}
        onFocus={() => setOpen(true)}
        // 候補のクリックより先に閉じないよう、少し待ってから閉じる
        onBlur={() => window.setTimeout(() => setOpen(false), 150)}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') {
            e.preventDefault()
            setActive((a) => Math.min(a + 1, candidates.length - 1))
          } else if (e.key === 'ArrowUp') {
            e.preventDefault()
            setActive((a) => Math.max(a - 1, 0))
          } else if (e.key === 'Enter') {
            // フォームを送信せず、候補を選ぶ
            e.preventDefault()
            if (candidates[active]) select(candidates[active])
          } else if (e.key === 'Escape' && text) {
            e.stopPropagation()
            setText('')
          }
        }}
      />
      {open && text.trim() && (
        <ul className="person-results">
          {searching && candidates.length === 0 && <li className="muted">検索中…</li>}
          {error && <li className="error-text">{error}</li>}
          {!searching && !error && candidates.length === 0 && <li className="muted">見つかりません</li>}
          {candidates.map((p, i) => (
            <li
              key={p.account}
              className={i === active ? 'active' : ''}
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => select(p)}
              onMouseEnter={() => setActive(i)}
            >
              <span className="person-name">{p.displayName}</span>
              <span className="muted">
                {[p.department, p.account].filter(Boolean).join('・')}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/** 選んだ人の表示（✕ で外す） */
export function PersonChip({ person, onRemove }: { person: Assignee; onRemove: () => void }) {
  return (
    <span className="person-chip" title={person.account}>
      {person.displayName}
      <button type="button" onClick={onRemove} title="外す" aria-label={`${person.displayName} を外す`}>
        ✕
      </button>
    </span>
  )
}
