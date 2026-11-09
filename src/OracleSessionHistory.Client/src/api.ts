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
  topApps: NameCount[]
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
  appName: string
  appDetail: string | null
  procOwner: string | null
  procInfoState: ProcInfoState
  procInfoError: string | null
  /** DB Link の可能性があるセッションで、接続元 DB 上で特定できた利用元のアプリ・端末 */
  linkOriginApp: string | null
  linkOriginMachine: string | null
  /** DB Link 経由で特定した接続元の実行ユーザ */
  linkOriginUser: string | null
}

/** セッション履歴の 1 行：監視対象のセッション、または DB Link 接続元 DB 上の DB Link 利用元 */
export type SessionRow =
  | { type: 'session'; targetName: string; logonTime: string; session: SessionListItem; usage: null }
  | { type: 'dblink'; targetName: string; logonTime: string; session: null; usage: DbLinkUsage }

/** プロセス情報の取得状態 */
export type ProcInfoState = 'Pending' | 'Resolved' | 'NotFound' | 'Mismatch' | 'Failed' | 'NotApplicable'

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
  appName: string
  appDetail: string | null
  procInfoState: ProcInfoState
  procInfoError: string | null
  procInfoAt: string | null
  procName: string | null
  procPath: string | null
  procCommandLine: string | null
  procOwner: string | null
  procStartedAt: string | null
  procParentName: string | null
  /** 実行元（親から順に 1 行ずつ） */
  procAncestry: string | null
  iisAppPool: string | null
  iisSite: string | null
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
  /** 接続元端末のクライアントプロセス（gv$session.process） */
  clientProcess: string | null
  appName: string
  appDetail: string | null
  procInfoState: ProcInfoState
  procInfoError: string | null
  procInfoAt: string | null
  procName: string | null
  procPath: string | null
  procCommandLine: string | null
  procOwner: string | null
  procStartedAt: string | null
  procParentName: string | null
  /** 実行元（親から順に 1 行ずつ） */
  procAncestry: string | null
  iisAppPool: string | null
  iisSite: string | null
}

export interface SessionDetail {
  session: TrackedSession
  targetName: string
  origins: DbLinkUsage[]
  candidates: DbLinkUsage[]
  hasLinkSource: boolean
}

/** 接続アプリ表の期間別のログオン数 */
export interface PeriodCounts {
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

/**
 * 接続アプリ表の 1 行（木構造）。端末 → 実行ファイル → IIS のプール・sqlplus の実行元、
 * ORACLE.EXE → DB Link（接続元 DB）→ 接続元のアプリ（実行ファイル → プール・実行元）
 */
export interface ConnectionCountNode extends PeriodCounts {
  /** machine（端末。DB Link の下は接続元の端末）・exe（実行ファイル）・app（プール・実行元等）・dblink（DB Link 接続元）・other（区別できないもの） */
  kind: 'machine' | 'exe' | 'app' | 'dblink' | 'other'
  label: string
  /** 補足（IIS のサイト・フォルダ・DB Link の接続元の端末等） */
  detail: string | null
  /** 実行ユーザ（ログオンの多い順。DB Link 経由は接続元の実行ユーザ） */
  owners: string[]
  connected: number
  lastSeenAt: string | null
  /** 監査証跡（DBA_AUDIT_SESSION）の件数を使った列（端末の行のみ） */
  auditPeriods: (keyof PeriodCounts)[]
  /** クリックで開くセッション履歴の検索条件 */
  open: Record<string, string> | null
  children: ConnectionCountNode[]
  /** 行のキー（上の段からの経路。対応担当者の保存に使う） */
  key: string
  /** 対応担当者（未設定は null） */
  assignee: string | null
}

/** 監視対象ごとの監査証跡の取得状況 */
export interface AuditCoverage {
  targetName: string
  /** 監査の件数で補う期間（両端を含む） */
  from: string | null
  lastDay: string | null
  fetchedAt: string | null
  error: string | null
}

export interface ConnectionCounts {
  periods: Record<string, string>
  nodes: ConnectionCountNode[]
  audit: AuditCoverage[]
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

export interface ProcessSettings {
  enabled: boolean
  userName: string | null
  hasPassword: boolean
  dnsSuffix: string | null
  timeoutSeconds: number
  excludeMachines: string | null
  updatedAt: string | null
}

/** password は null（変更なし）・''（クリア）・新しい値 */
export interface ProcessSettingsInput {
  enabled: boolean
  userName: string | null
  password: string | null
  dnsSuffix: string | null
  timeoutSeconds: number
  excludeMachines: string | null
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
  sessions: (params: Record<string, string | number | boolean | undefined>) =>
    request<Paged<SessionRow>>('GET', `api/sessions${qs(params)}`),
  session: (id: number) => request<SessionDetail>('GET', `api/sessions/${id}`),
  /** 実行ユーザの表示名（アカウントを大文字にした値 → AD の displayName） */
  userNames: () => request<Record<string, string>>('GET', 'api/user-names'),
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
  processSettings: () => request<ProcessSettings>('GET', 'api/process-settings'),
  updateProcessSettings: (s: ProcessSettingsInput) => request<ProcessSettings>('PUT', 'api/process-settings', s),
  testProcessSettings: (host: string, settings: ProcessSettingsInput) =>
    request<{ success: boolean; message: string }>('POST', 'api/process-settings/test', { host, settings }),
  /** 接続アプリ表の対応担当者の入力候補（設定済みの担当者） */
  assignees: () => request<string[]>('GET', 'api/assignees'),
  /** 接続アプリ表の行の対応担当者を保存する（空は設定を外す） */
  saveAssignee: (nodeKey: string, assignee: string) =>
    request<{ assignee: string | null }>('PUT', 'api/assignees', { nodeKey, assignee }),
}
