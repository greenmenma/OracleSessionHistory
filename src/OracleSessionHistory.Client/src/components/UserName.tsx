import { useEffect, useState } from 'react'
import { api } from '../api'

/** 表示名の対応表（アカウントを大文字にした値 → AD の displayName）。全画面で共有し、10 分ごとに取り直す */
let names: Record<string, string> = {}
let loadedAt = 0
let loading: Promise<void> | null = null
const listeners = new Set<() => void>()
const RefreshMs = 10 * 60 * 1000

function load(): Promise<void> {
  if (loading) return loading
  loading = api
    .userNames()
    .then((n) => {
      names = n
      loadedAt = Date.now()
      listeners.forEach((l) => l())
    })
    // 取得できなくてもアカウントのまま表示する（次の描画で取り直す）
    .catch(() => {})
    .finally(() => {
      loading = null
    })
  return loading
}

/** 表示名の対応表を使う（必要なら取得する） */
export function useUserNames(): Record<string, string> {
  const [, setTick] = useState(0)
  useEffect(() => {
    const l = () => setTick((t) => t + 1)
    listeners.add(l)
    if (Date.now() - loadedAt > RefreshMs) void load()
    return () => {
      listeners.delete(l)
    }
  }, [])
  return names
}

/** アカウントの表示名（無ければ null） */
export function displayNameOf(map: Record<string, string>, account: string | null | undefined): string | null {
  const key = account?.trim().toUpperCase()
  return key ? (map[key] ?? null) : null
}

/** 「表示名（アカウント）」の文字列。表示名が無ければアカウントのまま */
export function userLabel(map: Record<string, string>, account: string | null | undefined): string | null {
  if (!account) return null
  const name = displayNameOf(map, account)
  return name ? `${name}（${account}）` : account
}

/** 実行ユーザを AD の表示名で表示する（アカウントはツールチップ） */
export default function UserName({ account }: { account: string | null | undefined }) {
  const map = useUserNames()
  if (!account) return null
  const name = displayNameOf(map, account)
  return name ? <span title={account}>{name}</span> : <>{account}</>
}

/**
 * 実行ユーザ（複数）を表示名とアカウントの 2 行で表示する。複数の場合は先頭（最も多いユーザ）と「他 N 名」、
 * ツールチップに全員を出す
 */
export function UserList({ accounts }: { accounts: string[] }) {
  const map = useUserNames()
  if (accounts.length === 0) return null
  const first = accounts[0]
  const name = displayNameOf(map, first)
  const all = accounts.map((a) => userLabel(map, a)).join('\n')
  return (
    <div className="user-cell" title={all}>
      <div>
        {name ?? first}
        {accounts.length > 1 && <span className="muted"> 他 {accounts.length - 1} 名</span>}
      </div>
      {name && <div className="app-detail">{first}</div>}
    </div>
  )
}
