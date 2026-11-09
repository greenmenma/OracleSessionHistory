import { useState } from 'react'
import { api, type TargetSummary } from '../api'
import { useAsync } from '../hooks'
import { fmtDateTime } from '../format'
import { navigate } from '../router'
import ConnectionCountsSection from '../components/ConnectionCountsSection'

export default function SummaryPage({ targetId }: { targetId?: number }) {
  const { data, error } = useAsync(() => api.summary(targetId), [targetId], 5000)

  return (
    <div className="page">
      <div className="page-head">
        <h1>サマリー</h1>
        <span className="muted">5秒ごとに自動更新</span>
      </div>
      {error && <div className="alert error">{error}</div>}

      {/* 接続アプリ表をスクロールせずに見られるよう、サマリーは 1 行の数値と監視対象の状態にまとめ、内訳・ランキングは折りたたむ */}
      <div className="tiles compact">
        <Tile label="接続数" value={data?.connections} onClick={() => navigate('/sessions', { state: 'connected' })} />
        <Tile label="アプリ数" value={data?.apps} note="接続中のアプリ（IIS プール・exe 等）" onClick={() => document.getElementById('connection-counts')?.scrollIntoView({ behavior: 'smooth' })} />
        <Tile label="端末数" value={data?.terminals} note="接続中の実行端末" />
        <Tile label="履歴数" value={data?.history} note="切断済みセッション" onClick={() => navigate('/sessions', { state: 'disconnected' })} />
        <Tile label="本日のログオン" value={data?.todayLogons} />
        <Tile
          label="DB Linkの可能性"
          value={data?.dbLinkSuspects}
          onClick={() => navigate('/sessions', { state: 'connected', kind: 'DbLinkSuspect' })}
        />
      </div>

      <div className="target-states">
        <span className="muted">監視対象</span>
        {data?.targets.map((t) => (
          <span key={t.id} className="target-state" title={`最終取得成功 ${fmtDateTime(t.lastSuccessAt) || '–'}${t.lastError ? `\n${t.lastError}` : ''}`}>
            {t.name}
            <TargetStateBadge t={t} />
          </span>
        ))}
        {data && data.targets.length === 0 && (
          <span>
            監視対象がありません。<a href="#/settings">設定</a>から追加してください。
          </span>
        )}
        {data?.targets
          .filter((t) => t.enabled && t.lastError)
          .map((t) => (
            <span key={t.id} className="error-text ellipsis" title={t.lastError ?? ''}>
              {t.name}：{t.lastError}
            </span>
          ))}
      </div>

      <Fold id="targets" title="監視対象ごとの内訳">
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>監視対象</th>
                <th>状態</th>
                <th className="num">接続数</th>
                <th className="num">アプリ数</th>
                <th className="num">端末数</th>
                <th className="num">履歴数</th>
                <th className="num">DB Link?</th>
                <th>最終取得成功</th>
                <th>エラー</th>
              </tr>
            </thead>
            <tbody>
              {data?.targets.map((t) => (
                <tr key={t.id}>
                  <td>
                    {t.name}
                    {t.provider !== 'Oracle' && <span className="badge gray">{t.provider}</span>}
                  </td>
                  <td>
                    <TargetStateBadge t={t} />
                  </td>
                  <td className="num">{t.connections}</td>
                  <td className="num">{t.apps}</td>
                  <td className="num">{t.terminals}</td>
                  <td className="num">{t.history}</td>
                  <td className="num">{t.dbLinkSuspects}</td>
                  <td className="nowrap">{fmtDateTime(t.lastSuccessAt)}</td>
                  <td className="error-text ellipsis" title={t.lastError ?? ''}>
                    {t.lastError}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Fold>

      <Fold id="ranking" title="接続数の多いアプリ・端末">
        <div className="two-col">
          <Ranking title="接続数の多いアプリ" items={data?.topApps} param="app" />
          <Ranking title="接続数の多い端末" items={data?.topMachines} param="machine" />
        </div>
      </Fold>

      <ConnectionCountsSection targetId={targetId} />
    </div>
  )
}

function Tile({ label, value, note, onClick }: { label: string; value?: number; note?: string; onClick?: () => void }) {
  return (
    <div className={`tile ${onClick ? 'clickable' : ''}`} onClick={onClick} title={note}>
      <div className="tile-label">{label}</div>
      <div className="tile-value">{value ?? '–'}</div>
      {note && <div className="tile-note">{note}</div>}
    </div>
  )
}

function Ranking({ title, items, param }: { title: string; items?: { name: string; count: number }[]; param: string }) {
  const max = Math.max(1, ...(items ?? []).map((i) => i.count))
  return (
    <section className="card">
      <h2>{title}</h2>
      <ul className="ranking">
        {items?.map((i) => (
          // ランキングの名前と同じものだけを開く（端末なしは「(不明)」）
          <li key={i.name} onClick={() => navigate('/sessions', { state: 'connected', [param]: i.name, exact: 'true' })}>
            <span className="ranking-name" title={i.name}>
              {i.name}
            </span>
            <span className="ranking-bar">
              <span style={{ width: `${(i.count / max) * 100}%` }} />
            </span>
            <span className="num">{i.count}</span>
          </li>
        ))}
        {items && items.length === 0 && <li className="empty">接続中のセッションはありません</li>}
      </ul>
    </section>
  )
}

function TargetStateBadge({ t }: { t: TargetSummary }) {
  if (!t.enabled) return <span className="badge gray">停止</span>
  if (t.consecutiveFailures > 0) return <span className="badge red">取得失敗 {t.consecutiveFailures}回</span>
  if (t.lastSuccessAt) return <span className="badge green">収集中</span>
  return <span className="badge gray">待機</span>
}

const FOLD_KEY = 'summary.fold.'

/** 折りたたみ（開いたかどうかはブラウザに覚える。保存できない環境では閉じた状態から始める） */
function Fold({ id, title, children }: { id: string; title: string; children: React.ReactNode }) {
  const [open, setOpen] = useState(() => {
    try {
      return localStorage.getItem(FOLD_KEY + id) === 'open'
    } catch {
      return false
    }
  })
  return (
    <details
      className="card fold"
      open={open}
      onToggle={(e) => {
        const v = e.currentTarget.open
        setOpen(v)
        try {
          if (v) localStorage.setItem(FOLD_KEY + id, 'open')
          else localStorage.removeItem(FOLD_KEY + id)
        } catch {
          // 保存できなくても表示には影響しない
        }
      }}
    >
      <summary>{title}</summary>
      {open && <div className="fold-body">{children}</div>}
    </details>
  )
}
