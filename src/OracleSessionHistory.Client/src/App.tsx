import { useEffect, useState } from 'react'
import { api, type Target } from './api'
import { useRoute, navigate } from './router'
import SummaryPage from './pages/SummaryPage'
import SessionsPage from './pages/SessionsPage'
import SettingsPage from './pages/SettingsPage'

const menus = [
  { path: '/', label: 'サマリー' },
  { path: '/sessions', label: 'セッション履歴' },
  { path: '/settings', label: '設定' },
]

const TARGET_KEY = 'oraclesessionhistory.targetId'

export default function App() {
  const route = useRoute()
  const [targets, setTargets] = useState<Target[]>([])
  const [targetId, setTargetIdState] = useState<number | undefined>(() => {
    try {
      const v = localStorage.getItem(TARGET_KEY)
      return v ? Number(v) : undefined
    } catch {
      return undefined
    }
  })

  const loadTargets = () => api.targets().then(setTargets).catch(() => setTargets([]))
  useEffect(() => {
    void loadTargets()
  }, [])

  const setTargetId = (id: number | undefined) => {
    setTargetIdState(id)
    try {
      if (id === undefined) localStorage.removeItem(TARGET_KEY)
      else localStorage.setItem(TARGET_KEY, String(id))
    } catch {
      /* 保存できなくても動作には影響しない */
    }
  }

  // 削除された監視対象が選択されたままにならないようにする
  const effectiveTargetId = targets.some((t) => t.id === targetId) ? targetId : undefined

  let page
  switch (route.path) {
    case '/sessions':
      page = <SessionsPage targetId={effectiveTargetId} query={route.query} />
      break
    case '/settings':
      page = <SettingsPage onChanged={loadTargets} />
      break
    default:
      page = <SummaryPage targetId={effectiveTargetId} />
  }

  return (
    <div className="layout">
      <aside className="sidebar">
        <div className="brand" onClick={() => navigate('/')}>
          <svg viewBox="0 0 32 32" width="22" height="22" aria-hidden>
            <ellipse cx="16" cy="7" rx="11" ry="4" fill="currentColor" opacity=".7" />
            <path d="M5 7v18c0 2.2 4.9 4 11 4s11-1.8 11-4V7c0 2.2-4.9 4-11 4S5 9.2 5 7z" fill="currentColor" />
          </svg>
          <span>Oracle Session History</span>
        </div>
        <label className="target-select">
          <span>監視対象</span>
          <select
            value={effectiveTargetId ?? ''}
            onChange={(e) => setTargetId(e.target.value ? Number(e.target.value) : undefined)}
          >
            <option value="">すべて</option>
            {targets.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </select>
        </label>
        <nav className="nav">
          {menus.map((m) => (
            <a key={m.path} href={`#${m.path}`} className={route.path === m.path ? 'active' : ''}>
              {m.label}
            </a>
          ))}
        </nav>
      </aside>
      <main className="main">{page}</main>
    </div>
  )
}
