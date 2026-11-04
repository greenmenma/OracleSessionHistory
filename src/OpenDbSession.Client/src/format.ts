// 表示用の整形

/** Server の日時（タイムゾーン無し・7 桁小数）をローカル時刻として解釈する */
export function parseDate(v: string): Date {
  return new Date(v.replace(/(\.\d{3})\d+/, '$1'))
}

export function fmtDateTime(v: string | null | undefined): string {
  if (!v) return ''
  const d = parseDate(v)
  if (Number.isNaN(d.getTime())) return v
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}/${p(d.getMonth() + 1)}/${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

export function fmtDate(v: string | null | undefined): string {
  return fmtDateTime(v).slice(0, 10)
}

/** 経過時間（例：2時間15分） */
export function fmtDuration(from: string, to: string | null | undefined): string {
  const ms = (to ? parseDate(to) : new Date()).getTime() - parseDate(from).getTime()
  if (!(ms >= 0)) return ''
  const m = Math.floor(ms / 60000)
  if (m < 1) return '1分未満'
  if (m < 60) return `${m}分`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}時間${m % 60}分`
  return `${Math.floor(h / 24)}日${h % 24}時間`
}

export const kindLabels: Record<string, string> = {
  Client: 'クライアント',
  DbLinkSuspect: 'DB Linkの可能性',
  JobSlave: 'ジョブ',
  ParallelSlave: 'パラレル実行',
  AqSlave: 'AQ',
  DataPump: 'Data Pump',
  OracleInternal: 'Oracle内部',
  Background: 'バックグラウンド',
}

export const matchTypeLabels: Record<string, string> = {
  PROCESS: 'プロセス一致（確定）',
  SQL: 'SQLの@DBLINK',
  SYNONYM: 'シノニム経由',
}
