import { useEffect, useState } from 'react'

// ハッシュベースの簡易ルーター（#/sessions?machine=xxx 形式）

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
    return () => window.removeEventListener('hashchange', onChange)
  }, [])
  return route
}

export function navigate(path: string, query?: Record<string, string | number | undefined>) {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(query ?? {})) if (v !== undefined && v !== '') p.set(k, String(v))
  const s = p.toString()
  window.location.hash = s ? `${path}?${s}` : path
}
