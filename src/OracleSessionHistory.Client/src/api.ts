// Server API の型定義と呼び出し

export interface Target {
  id: number
  name: string
  provider: string
  connectionString: string
  intervalSeconds: number
  linkIntervalSeconds: number
  includeBackground: boolean
  collectSqlText: boolean
  enabled: boolean
  description: string | null
  lastPollAt: string | null
  lastSuccessAt: string | null
  lastError: string | null
  consecutiveFailures: number
  lastSessionCount: number | null
}

export type TargetInput = Omit<
  Target,
  'id' | 'lastPollAt' | 'lastSuccessAt' | 'lastError' | 'consecutiveFailures' | 'lastSessionCount'
>

export interface TargetSummary {
  id: number
  name: string
  provider: string
  enabled: boolean
  connections: number
  apps: number
  terminals: number
  history: number
  dbLinkSuspects: number
  jobs: number
  lastPollAt: string | null
  lastSuccessAt: string | null
  lastError: string | null
  consecutiveFailures: number
}

export interface NameCount {
  name: string
  count: number
}

export interface Summary {
  connections: number
  apps: number
  terminals: number
  history: number
  todayLogons: number
  dbLinkSuspects: number
  jobs: number
  targets: TargetSummary[]
  topPrograms: NameCount[]
  topMachines: NameCount[]
}

export interface SessionListItem {
  id: number
  targetId: number
  targetName: string
  instId: number
  sid: number
  serial: number
  logonTime: string
  username: string | null
  schemaName: string | null
  osUser: string | null
  machine: string | null
  program: string | null
  module: string | null
  status: string | null
  processKind: string
  jobName: string | null
  isConnected: boolean
  firstSeenAt: string
  lastSeenAt: string
  disconnectedAt: string | null
  disconnectUncertain: boolean
  dbLinkOriginCount: number
}

export interface Paged<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

export interface TrackedSession {
  id: number
  targetId: number
  instId: number
  sid: number
  serial: number
  logonTime: string
  auditSid: number | null
  username: string | null
  schemaName: string | null
  osUser: string | null
  machine: string | null
  terminal: string | null
  program: string | null
  module: string | null
  action: string | null
  clientInfo: string | null
  clientIdentifier: string | null
  serviceName: string | null
  clientProcess: string | null
  serverProcess: string | null
  port: number | null
  clientDriver: string | null
  clientVersion: string | null
  sessionType: string | null
  status: string | null
  sqlId: string | null
  lastSqlText: string | null
  event: string | null
  processKind: string
  jobType: string | null
  jobOwner: string | null
  jobName: string | null
  jobAction: string | null
  parentInfo: string | null
  firstSeenAt: string
  lastSeenAt: string
  lastActiveAt: string | null
  isConnected: boolean
  disconnectedAt: string | null
  disconnectDetectedAt: string | null
  disconnectUncertain: boolean
}

export interface DbLinkUsage {
  id: number
  linkSourceId: number
  linkSourceName: string
  dbLinkName: string
  matchType: string
  instId: number
  sid: number
  serial: number
  logonTime: string
  username: string | null
  schemaName: string | null
  osUser: string | null
  machine: string | null
  terminal: string | null
  program: string | null
  module: string | null
  action: string | null
  serverProcess: string | null
  sqlId: string | null
  sqlText: string | null
  matchedSessionId: number | null
  firstSeenAt: string
  lastSeenAt: string
  hitCount: number
}

export interface SessionDetail {
  session: TrackedSession
  targetName: string
  origins: DbLinkUsage[]
  candidates: DbLinkUsage[]
  hasLinkSource: boolean
}

export interface ConnectionCountRow {
  machine: string | null
  program: string | null
  today: number
  d1: number
  d2: number
  d3: number
  d4: number
  d5: number
  d6: number
  month1: number
  month2: number
}

export interface ConnectionCounts {
  periods: Record<string, string>
  rows: ConnectionCountRow[]
}

export interface LinkSource {
  id: number
  targetId: number
  name: string
  provider: string
  connectionString: string
  enabled: boolean
  selectedLinks: string[]
  lastPollAt: string | null
  lastSuccessAt: string | null
  lastError: string | null
}

export type LinkSourceInput = Pick<LinkSource, 'name' | 'provider' | 'connectionString' | 'enabled' | 'selectedLinks'>

export interface DbLinkInfo {
  owner: string
  dbLink: string
  username: string | null
  host: string | null
  created: string | null
  fullName: string
}

export class ApiError extends Error {}

/**
 * API を呼び出す。url は 'api/...' の相対パスで指定する。
 * IIS のサブフォルダ（例：/OracleSessionHistory/）に配置しても、画面の URL を基準に解決されるようにするため。
 */
async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`
    try {
      const p = await res.json()
      if (p.errors) message = Object.values(p.errors).flat().join(' / ')
      else if (p.title) message = p.title
    } catch {
      /* JSON 以外のエラー */
    }
    throw new ApiError(message)
  }
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}

/** 値が空のパラメータを除いてクエリ文字列を作る */
export function qs(params: Record<string, string | number | boolean | null | undefined>): string {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') p.set(k, String(v))
  }
  const s = p.toString()
  return s ? `?${s}` : ''
}

export const api = {
  providers: () => request<string[]>('GET', 'api/providers'),
  targets: () => request<Target[]>('GET', 'api/targets'),
  createTarget: (t: TargetInput) => request<Target>('POST', 'api/targets', t),
  updateTarget: (id: number, t: TargetInput) => request<Target>('PUT', `api/targets/${id}`, t),
  deleteTarget: (id: number) => request<void>('DELETE', `api/targets/${id}`),
  testConnection: (provider: string, connectionString: string, targetId?: number) =>
    request<{ success: boolean; message: string }>('POST', 'api/targets/test', { provider, connectionString, targetId }),

  summary: (targetId?: number) => request<Summary>('GET', `api/summary${qs({ targetId })}`),
  sessions: (params: Record<string, string | number | undefined>) =>
    request<Paged<SessionListItem>>('GET', `api/sessions${qs(params)}`),
  session: (id: number) => request<SessionDetail>('GET', `api/sessions/${id}`),
  connectionCounts: (targetId?: number, q?: string) =>
    request<ConnectionCounts>('GET', `api/connection-counts${qs({ targetId, q })}`),

  linkSources: (targetId: number) => request<LinkSource[]>('GET', `api/targets/${targetId}/link-sources`),
  createLinkSource: (targetId: number, l: LinkSourceInput) =>
    request<LinkSource>('POST', `api/targets/${targetId}/link-sources`, l),
  updateLinkSource: (id: number, l: LinkSourceInput) => request<LinkSource>('PUT', `api/link-sources/${id}`, l),
  deleteLinkSource: (id: number) => request<void>('DELETE', `api/link-sources/${id}`),
  listDbLinks: (provider: string, connectionString: string, linkSourceId?: number) =>
    request<{ success: boolean; links: DbLinkInfo[]; message: string | null }>('POST', 'api/db-links', {
      provider,
      connectionString,
      linkSourceId,
    }),
  dbLinkUsages: (params: Record<string, string | number | undefined>) =>
    request<Paged<DbLinkUsage>>('GET', `api/db-link-usages${qs(params)}`),
}
