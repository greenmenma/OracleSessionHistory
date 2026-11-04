import { useCallback, useEffect, useRef, useState } from 'react'

/** 非同期データの取得（refreshMs を指定すると自動更新） */
export function useAsync<T>(fn: () => Promise<T>, deps: unknown[], refreshMs?: number) {
  const [data, setData] = useState<T>()
  const [error, setError] = useState<string>()
  const [loading, setLoading] = useState(true)
  const fnRef = useRef(fn)
  fnRef.current = fn

  const reload = useCallback(async () => {
    try {
      const d = await fnRef.current()
      setData(d)
      setError(undefined)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    setLoading(true)
    void reload()
    if (!refreshMs) return
    const t = window.setInterval(() => void reload(), refreshMs)
    return () => window.clearInterval(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, refreshMs])

  return { data, error, loading, reload }
}
