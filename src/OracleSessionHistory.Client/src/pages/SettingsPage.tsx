import { useState } from 'react'
import { api, type DbLinkInfo, type LinkSource, type LinkSourceInput, type Target, type TargetInput } from '../api'
import { useAsync } from '../hooks'
import { fmtDateTime } from '../format'

const ORACLE_SAMPLE =
  'User Id=MONITOR;Password=xxxx;Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=dbhost)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=KOUKAI)))'

const emptyTarget: TargetInput = {
  name: '',
  provider: 'Oracle',
  connectionString: '',
  intervalSeconds: 5,
  linkIntervalSeconds: 30,
  includeBackground: false,
  collectSqlText: true,
  enabled: true,
  description: '',
}

export default function SettingsPage({ onChanged }: { onChanged: () => void }) {
  const { data: targets, error, reload } = useAsync(() => api.targets(), [], 5000)
  const { data: providers } = useAsync(() => api.providers(), [])
  const [editing, setEditing] = useState<{ id?: number; input: TargetInput }>()
  const [linkTarget, setLinkTarget] = useState<Target>()

  const changed = async () => {
    await reload()
    onChanged()
  }

  const remove = async (t: Target) => {
    if (!window.confirm(`「${t.name}」を削除しますか？収集した履歴もすべて削除されます。`)) return
    await api.deleteTarget(t.id)
    if (linkTarget?.id === t.id) setLinkTarget(undefined)
    await changed()
  }

  return (
    <div className="page">
      <div className="page-head">
        <h1>設定</h1>
      </div>
      {error && <div className="alert error">{error}</div>}

      <section className="card">
        <div className="table-head">
          <h2>監視対象</h2>
          <button className="primary" onClick={() => setEditing({ input: { ...emptyTarget } })}>
            ＋ 監視対象を追加
          </button>
        </div>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>名前</th>
                <th>Provider</th>
                <th>接続文字列</th>
                <th className="num">間隔</th>
                <th>状態</th>
                <th>最終取得成功</th>
                <th>エラー</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {targets?.map((t) => (
                <tr key={t.id} className={linkTarget?.id === t.id ? 'selected' : ''}>
                  <td>{t.name}</td>
                  <td>{t.provider}</td>
                  <td className="ellipsis mono" title={t.connectionString}>
                    {t.connectionString}
                  </td>
                  <td className="num">{t.intervalSeconds}秒</td>
                  <td>
                    {!t.enabled ? (
                      <span className="badge gray">停止</span>
                    ) : t.consecutiveFailures > 0 ? (
                      <span className="badge red">失敗 {t.consecutiveFailures}回</span>
                    ) : (
                      <span className="badge green">有効</span>
                    )}
                  </td>
                  <td className="nowrap">{fmtDateTime(t.lastSuccessAt)}</td>
                  <td className="ellipsis error-text" title={t.lastError ?? ''}>
                    {t.lastError}
                  </td>
                  <td className="nowrap">
                    <button onClick={() => setEditing({ id: t.id, input: toInput(t) })}>編集</button>
                    <button onClick={() => setLinkTarget(t)}>DB Link接続元</button>
                    <button className="danger" onClick={() => void remove(t)}>
                      削除
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {linkTarget && <LinkSourcesSection target={linkTarget} providers={providers ?? ['Oracle']} onClose={() => setLinkTarget(undefined)} />}

      {editing && (
        <TargetForm
          id={editing.id}
          initial={editing.input}
          providers={providers ?? ['Oracle']}
          onClose={() => setEditing(undefined)}
          onSaved={async () => {
            setEditing(undefined)
            await changed()
          }}
        />
      )}
    </div>
  )
}

function toInput(t: Target): TargetInput {
  return {
    name: t.name,
    provider: t.provider,
    connectionString: t.connectionString,
    intervalSeconds: t.intervalSeconds,
    linkIntervalSeconds: t.linkIntervalSeconds,
    includeBackground: t.includeBackground,
    collectSqlText: t.collectSqlText,
    enabled: t.enabled,
    description: t.description ?? '',
  }
}

function TargetForm({
  id,
  initial,
  providers,
  onClose,
  onSaved,
}: {
  id?: number
  initial: TargetInput
  providers: string[]
  onClose: () => void
  onSaved: () => void
}) {
  const [f, setF] = useState(initial)
  const [msg, setMsg] = useState<{ ok: boolean; text: string }>()
  const [busy, setBusy] = useState(false)

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    try {
      if (id) await api.updateTarget(id, f)
      else await api.createTarget(f)
      onSaved()
    } catch (err) {
      setMsg({ ok: false, text: err instanceof Error ? err.message : String(err) })
    } finally {
      setBusy(false)
    }
  }

  const test = async () => {
    setBusy(true)
    setMsg({ ok: true, text: '接続中…' })
    try {
      const r = await api.testConnection(f.provider, f.connectionString, id)
      setMsg({ ok: r.success, text: r.message })
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="modal-backdrop">
      <form className="modal" onSubmit={save}>
        <h2>{id ? '監視対象の編集' : '監視対象の追加'}</h2>
        <label>
          名前
          <input required value={f.name} placeholder="例：基幹公開DB" onChange={(e) => setF({ ...f, name: e.target.value })} />
        </label>
        <label>
          Provider
          <select value={f.provider} onChange={(e) => setF({ ...f, provider: e.target.value })}>
            {providers.map((p) => (
              <option key={p}>{p}</option>
            ))}
          </select>
        </label>
        <label>
          接続文字列（ODP.NET 形式）
          <textarea
            required
            rows={3}
            value={f.connectionString}
            placeholder={ORACLE_SAMPLE}
            onChange={(e) => setF({ ...f, connectionString: e.target.value })}
          />
          <small className="muted">パスワードは保存後に ******** で表示されます（変更しない場合はそのまま保存）。</small>
        </label>
        <div className="row">
          <label>
            取得間隔（秒）
            <input type="number" min={1} max={3600} value={f.intervalSeconds} onChange={(e) => setF({ ...f, intervalSeconds: Number(e.target.value) })} />
          </label>
          <label>
            DB Link 接続元の取得間隔（秒）
            <input
              type="number"
              min={5}
              max={3600}
              value={f.linkIntervalSeconds}
              onChange={(e) => setF({ ...f, linkIntervalSeconds: Number(e.target.value) })}
            />
          </label>
        </div>
        <label className="check">
          <input type="checkbox" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.target.checked })} />
          収集を有効にする
        </label>
        <label className="check">
          <input type="checkbox" checked={f.collectSqlText} onChange={(e) => setF({ ...f, collectSqlText: e.target.checked })} />
          SQL テキストを取得する
        </label>
        <label className="check">
          <input type="checkbox" checked={f.includeBackground} onChange={(e) => setF({ ...f, includeBackground: e.target.checked })} />
          BACKGROUND セッション（PMON 等）も記録する
        </label>
        <label>
          説明
          <input value={f.description ?? ''} onChange={(e) => setF({ ...f, description: e.target.value })} />
        </label>
        {msg && <div className={`alert ${msg.ok ? 'info' : 'error'}`}>{msg.text}</div>}
        <div className="actions">
          <button type="button" onClick={() => void test()} disabled={busy}>
            接続テスト
          </button>
          <span className="spacer" />
          <button type="button" onClick={onClose}>
            キャンセル
          </button>
          <button type="submit" className="primary" disabled={busy}>
            保存
          </button>
        </div>
      </form>
    </div>
  )
}

const emptyLink: LinkSourceInput = { name: '', provider: 'Oracle', connectionString: '', enabled: true, selectedLinks: [] }

function LinkSourcesSection({ target, providers, onClose }: { target: Target; providers: string[]; onClose: () => void }) {
  const { data, reload } = useAsync(() => api.linkSources(target.id), [target.id], 10000)
  const [editing, setEditing] = useState<{ id?: number; input: LinkSourceInput }>()

  const remove = async (l: LinkSource) => {
    if (!window.confirm(`「${l.name}」を削除しますか？`)) return
    await api.deleteLinkSource(l.id)
    await reload()
  }

  return (
    <section className="card">
      <div className="table-head">
        <h2>DB Link 接続元 ― {target.name}</h2>
        <div>
          <button className="primary" onClick={() => setEditing({ input: { ...emptyLink, provider: target.provider } })}>
            ＋ 接続元DBを追加
          </button>
          <button onClick={onClose}>閉じる</button>
        </div>
      </div>
      <p className="muted">
        「{target.name}」に DB Link で接続している側（接続元）の Oracle を登録し、使用している DB Link を選択します。
        接続元 DB 上で、その DB Link を使っているセッションの端末・プログラム・ユーザを収集します。
      </p>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>名前</th>
              <th>接続文字列</th>
              <th>監視する DB Link</th>
              <th>最終取得成功</th>
              <th>エラー</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {data?.map((l) => (
              <tr key={l.id}>
                <td>
                  {l.name}
                  {!l.enabled && <span className="badge gray">停止</span>}
                </td>
                <td className="ellipsis mono" title={l.connectionString}>
                  {l.connectionString}
                </td>
                <td>{l.selectedLinks.join(', ') || <span className="muted">（プロセス一致のみ）</span>}</td>
                <td className="nowrap">{fmtDateTime(l.lastSuccessAt)}</td>
                <td className="ellipsis error-text" title={l.lastError ?? ''}>
                  {l.lastError}
                </td>
                <td className="nowrap">
                  <button
                    onClick={() =>
                      setEditing({
                        id: l.id,
                        input: { name: l.name, provider: l.provider, connectionString: l.connectionString, enabled: l.enabled, selectedLinks: l.selectedLinks },
                      })
                    }
                  >
                    編集
                  </button>
                  <button className="danger" onClick={() => void remove(l)}>
                    削除
                  </button>
                </td>
              </tr>
            ))}
            {data && data.length === 0 && (
              <tr>
                <td colSpan={6} className="empty">
                  接続元DBは登録されていません
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      {editing && (
        <LinkSourceForm
          targetId={target.id}
          id={editing.id}
          initial={editing.input}
          providers={providers}
          onClose={() => setEditing(undefined)}
          onSaved={async () => {
            setEditing(undefined)
            await reload()
          }}
        />
      )}
    </section>
  )
}

function LinkSourceForm({
  targetId,
  id,
  initial,
  providers,
  onClose,
  onSaved,
}: {
  targetId: number
  id?: number
  initial: LinkSourceInput
  providers: string[]
  onClose: () => void
  onSaved: () => void
}) {
  const [f, setF] = useState(initial)
  const [links, setLinks] = useState<DbLinkInfo[]>()
  const [msg, setMsg] = useState<{ ok: boolean; text: string }>()
  const [busy, setBusy] = useState(false)

  const loadLinks = async () => {
    setBusy(true)
    setMsg({ ok: true, text: 'DB Link を取得中…' })
    try {
      const r = await api.listDbLinks(f.provider, f.connectionString, id)
      if (r.success) {
        setLinks(r.links)
        setMsg({ ok: true, text: `${r.links.length} 件の DB Link が見つかりました` })
      } else setMsg({ ok: false, text: r.message ?? '取得に失敗しました' })
    } finally {
      setBusy(false)
    }
  }

  const toggle = (name: string) =>
    setF({
      ...f,
      selectedLinks: f.selectedLinks.includes(name) ? f.selectedLinks.filter((x) => x !== name) : [...f.selectedLinks, name],
    })

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    try {
      if (id) await api.updateLinkSource(id, f)
      else await api.createLinkSource(targetId, f)
      onSaved()
    } catch (err) {
      setMsg({ ok: false, text: err instanceof Error ? err.message : String(err) })
    } finally {
      setBusy(false)
    }
  }

  // 一覧に無い選択済みリンク（以前に選択したもの）も表示する
  const allNames = [...new Set([...(links ?? []).map((l) => l.fullName), ...f.selectedLinks])]

  return (
    <div className="modal-backdrop">
      <form className="modal" onSubmit={save}>
        <h2>{id ? 'DB Link 接続元の編集' : 'DB Link 接続元の追加'}</h2>
        <label>
          名前
          <input required value={f.name} placeholder="例：基幹DB" onChange={(e) => setF({ ...f, name: e.target.value })} />
        </label>
        <label>
          Provider
          <select value={f.provider} onChange={(e) => setF({ ...f, provider: e.target.value })}>
            {providers.map((p) => (
              <option key={p}>{p}</option>
            ))}
          </select>
        </label>
        <label>
          接続文字列（接続元 DB）
          <textarea required rows={3} value={f.connectionString} placeholder={ORACLE_SAMPLE} onChange={(e) => setF({ ...f, connectionString: e.target.value })} />
        </label>
        <div className="actions">
          <button type="button" onClick={() => void loadLinks()} disabled={busy || !f.connectionString}>
            DB Link 一覧を取得
          </button>
        </div>
        {allNames.length > 0 && (
          <div className="link-list">
            <div className="muted">監視する DB Link を選択してください</div>
            {allNames.map((name) => {
              const info = links?.find((l) => l.fullName === name)
              return (
                <label key={name} className="check">
                  <input type="checkbox" checked={f.selectedLinks.includes(name)} onChange={() => toggle(name)} />
                  <span className="mono">{name}</span>
                  {info && (
                    <span className="muted">
                      （接続ユーザ {info.username ?? '-'} / HOST {info.host ?? '-'}）
                    </span>
                  )}
                </label>
              )
            })}
          </div>
        )}
        <label className="check">
          <input type="checkbox" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.target.checked })} />
          収集を有効にする
        </label>
        {msg && <div className={`alert ${msg.ok ? 'info' : 'error'}`}>{msg.text}</div>}
        <div className="actions">
          <span className="spacer" />
          <button type="button" onClick={onClose}>
            キャンセル
          </button>
          <button type="submit" className="primary" disabled={busy}>
            保存
          </button>
        </div>
      </form>
    </div>
  )
}
