import { api } from '../api'
import { useAsync } from '../hooks'
import { fmtDateTime } from '../format'
import { navigate } from '../router'

export default function SummaryPage({ targetId }: { targetId?: number }) {
  const { data, error } = useAsync(() => api.summary(targetId), [targetId], 5000)

  return (
    <div className="page">
      <div className="page-head">
        <h1>サマリー</h1>
        <span className="muted">5秒ごとに自動更新</span>
      </div>
      {error && <div className="alert error">{error}</div>}

      <div className="tiles">
        <Tile label="接続数" value={data?.connections} onClick={() => navigate('/sessions', { state: 'connected' })} />
        <Tile label="アプリ数" value={data?.apps} note="接続中のアプリ（IIS プール・exe 等）" onClick={() => navigate('/counts')} />
        <Tile label="端末数" value={data?.terminals} note="接続中の実行端末" />
        <Tile label="履歴数" value={data?.history} note="切断済みセッション" onClick={() => navigate('/sessions', { state: 'disconnected' })} />
      </div>
      <div className="tiles small">
        <Tile label="本日のログオン" value={data?.todayLogons} />
        <Tile
          label="DB Linkの可能性"
          value={data?.dbLinkSuspects}
          onClick={() => navigate('/sessions', { state: 'connected', kind: 'DbLinkSuspect' })}
        />
      </div>

      <section className="card">
        <h2>監視対象の状態</h2>
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
                    {!t.enabled ? (
                      <span className="badge gray">停止</span>
                    ) : t.consecutiveFailures > 0 ? (
                      <span className="badge red">取得失敗 {t.consecutiveFailures}回</span>
                    ) : t.lastSuccessAt ? (
                      <span className="badge green">収集中</span>
                    ) : (
                      <span className="badge gray">待機</span>
                    )}
                  </td>
                  <td className="num">{t.connections}</td>
                  <td className="num">{t.apps}</td>
                  <td className="num">{t.terminals}</td>
                  <td className="num">{t.history}</td>
                  <td className="num">{t.dbLinkSuspects}</td>
                  <td>{fmtDateTime(t.lastSuccessAt)}</td>
                  <td className="error-text ellipsis" title={t.lastError ?? ''}>
                    {t.lastError}
                  </td>
                </tr>
              ))}
              {data && data.targets.length === 0 && (
                <tr>
                  <td colSpan={9} className="empty">
                    監視対象がありません。<a href="#/settings">設定</a>から追加してください。
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>

      <div className="two-col">
        <Ranking title="接続数の多いアプリ" items={data?.topApps} param="app" />
        <Ranking title="接続数の多い端末" items={data?.topMachines} param="machine" />
      </div>
    </div>
  )
}

function Tile({ label, value, note, onClick }: { label: string; value?: number; note?: string; onClick?: () => void }) {
  return (
    <div className={`tile ${onClick ? 'clickable' : ''}`} onClick={onClick}>
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
