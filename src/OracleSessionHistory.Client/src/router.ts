import { useEffect, useState } from 'react'

// ハッシュベースの簡易ルーター（#/sessions?machine=xxx 形式）

/** replaceQuery で URL を置き換えたことを useRoute に知らせるイベント（replaceState では hashchange が発生しない） */
const ROUTE_REPLACED = 'route-replaced'

export interface Route {
  path: string
  query: URLSearchParams
}

function parse(): Route {
  const hash = window.location.hash.replace(/^#/, '') || '/'
  const [path, query = ''] = hash.split('?')
  return { path: path || '/', query: new URLSearchParams(query) }
}

export function useRoute(): Route {
  const [route, setRoute] = useState(parse)
  useEffect(() => {
    const onChange = () => setRoute(parse())
    window.addEventListener('hashchange', onChange)
    window.addEventListener(ROUTE_REPLACED, onChange)
    return () => {
      window.removeEventListener('hashchange', onChange)
      window.removeEventListener(ROUTE_REPLACED, onChange)
    }
  }, [])
  return route
}

/**
 * 画面遷移せずに現在の URL のクエリを置き換える（検索条件をリロード・リンク共有で残すため）。
 * 画面の状態はそのままで、useRoute のクエリだけを更新する（メニューから同じ画面を開き直したときに条件が戻るように）。
 */
export function replaceQuery(path: string, query: Record<string, string | number | undefined>) {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(query)) if (v !== undefined && v !== '') p.set(k, String(v))
  const s = p.toString()
  const hash = `#${s ? `${path}?${s}` : path}`
  if (window.location.hash === hash) return
  window.history.replaceState(window.history.state, '', hash)
  window.dispatchEvent(new Event(ROUTE_REPLACED))
}

export function navigate(path: string, query?: Record<string, string | number | undefined>) {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(query ?? {})) if (v !== undefined && v !== '') p.set(k, String(v))
  const s = p.toString()
  window.location.hash = s ? `${path}?${s}` : path
}
