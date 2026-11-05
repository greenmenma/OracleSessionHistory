import type { ProcInfoState } from '../api'
import { procInfoLabels } from '../format'

/** プロセス情報が確定していない状態のバッジ（取得済み・対象外は表示しない） */
const stateBadges: Partial<Record<ProcInfoState, string>> = {
  Pending: 'gray',
  Failed: 'red',
  NotFound: 'gray',
  Mismatch: 'amber',
}

/**
 * アプリ名（太字）と補足（IIS のサイト・フォルダ等）を 2 行で表示する。
 * プロセス情報が取れていない場合は、PROGRAM から推定した名前であることをバッジで示す。
 */
export default function AppCell({
  appName,
  appDetail,
  state,
  error,
}: {
  appName: string
  appDetail?: string | null
  state?: ProcInfoState
  error?: string | null
}) {
  const badge = state ? stateBadges[state] : undefined
  return (
    <div className="app-cell">
      <div className="app-name">
        <span title={appName}>{appName || '(不明)'}</span>
        {badge && state && (
          <span className={`badge ${badge}`} title={error ?? procInfoLabels[state]}>
            {procInfoLabels[state]}
          </span>
        )}
      </div>
      {appDetail && (
        <div className="app-detail" title={appDetail}>
          {appDetail}
        </div>
      )}
    </div>
  )
}
