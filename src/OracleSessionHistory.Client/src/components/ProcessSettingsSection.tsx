import { useEffect, useState } from 'react'
import { api, type ProcessSettings, type ProcessSettingsInput } from '../api'
import { useAsync } from '../hooks'
import { fmtDateTime } from '../format'

const MASK = '********'

function toForm(s: ProcessSettings): ProcessSettingsInput {
  return {
    enabled: s.enabled,
    userName: s.userName ?? '',
    // 保存済みのパスワードは返ってこないため、マスクを表示して「変更なし」として扱う
    password: s.hasPassword ? MASK : '',
    dnsSuffix: s.dnsSuffix ?? '',
    timeoutSeconds: s.timeoutSeconds,
    excludeMachines: s.excludeMachines ?? '',
  }
}

/** 設定画面：接続元サーバーのプロセス情報（WMI/CIM）の取得設定 */
export default function ProcessSettingsSection() {
  const { data, error, reload } = useAsync(() => api.processSettings(), [])
  const [f, setF] = useState<ProcessSettingsInput>()
  const [host, setHost] = useState('')
  const [msg, setMsg] = useState<{ ok: boolean; text: string }>()
  const [busy, setBusy] = useState(false)
  const [clearPassword, setClearPassword] = useState(false)

  useEffect(() => {
    if (data) {
      setF(toForm(data))
      setClearPassword(false)
    }
  }, [data])

  if (!f) return error ? <div className="alert error">{error}</div> : null

  const set = <K extends keyof ProcessSettingsInput>(k: K, v: ProcessSettingsInput[K]) => setF({ ...f, [k]: v })

  const run = async (action: () => Promise<{ ok: boolean; text: string }>) => {
    setBusy(true)
    try {
      setMsg(await action())
    } catch (err) {
      setMsg({ ok: false, text: err instanceof Error ? err.message : String(err) })
    } finally {
      setBusy(false)
    }
  }

  const save = (e: React.FormEvent) => {
    e.preventDefault()
    void run(async () => {
      await api.updateProcessSettings(f)
      await reload()
      return { ok: true, text: '保存しました（Batch は次の取得から新しい設定を使います）' }
    })
  }

  const test = () =>
    run(async () => {
      setMsg({ ok: true, text: '接続中…' })
      const r = await api.testProcessSettings(host, f)
      return { ok: r.success, text: r.message }
    })

  return (
    <section className="card">
      <div className="table-head">
        <h2>プロセス情報の取得（接続アプリの特定）</h2>
        {data?.updatedAt && <span className="muted">最終更新 {fmtDateTime(data.updatedAt)}</span>}
      </div>
      <p className="muted">
        PROGRAM が w3wp.exe・sqlplus.exe のセッションについて、接続元サーバー（MACHINE）に WMI（DCOM）で問い合わせ、
        PROCESS の PID から実行ファイル・コマンドライン・実行ユーザ・IIS のアプリケーションプール名・実行元を取得します。
        それ以外のプログラムと、取得できない場合は PROGRAM でアプリを表示します。
      </p>
      <form className="stack-form" onSubmit={save}>
        <label className="check">
          <input type="checkbox" checked={f.enabled} onChange={(e) => set('enabled', e.target.checked)} />
          プロセス情報を取得する
        </label>
        <div className="row">
          <label>
            アカウント（DOMAIN\ユーザ または ユーザ@ドメイン）
            <input
              value={f.userName ?? ''}
              placeholder="空欄の場合は Batch の実行アカウントで接続"
              autoComplete="off"
              onChange={(e) => set('userName', e.target.value)}
            />
          </label>
          <label>
            パスワード
            <input
              type="password"
              value={f.password ?? ''}
              autoComplete="new-password"
              onFocus={() => f.password === MASK && set('password', '')}
              // 何も入力せずに離れた場合は「変更なし」に戻す（消去はボタンで行う）
              onBlur={() => f.password === '' && data?.hasPassword && !clearPassword && set('password', MASK)}
              onChange={(e) => {
                setClearPassword(false)
                set('password', e.target.value)
              }}
            />
            {data?.hasPassword && (
              <button
                type="button"
                className="link"
                onClick={() => {
                  setClearPassword(true)
                  set('password', '')
                }}
              >
                {clearPassword ? '保存するとパスワードを消去します' : 'パスワードを消去'}
              </button>
            )}
          </label>
        </div>
        <div className="row">
          <label>
            DNS サフィックス（端末名で名前解決できない場合に付ける）
            <input value={f.dnsSuffix ?? ''} placeholder="例：corp.example.local" onChange={(e) => set('dnsSuffix', e.target.value)} />
          </label>
          <label>
            タイムアウト（秒、1 サーバーあたり）
            <input
              type="number"
              min={1}
              max={120}
              value={f.timeoutSeconds}
              onChange={(e) => set('timeoutSeconds', Number(e.target.value))}
            />
          </label>
        </div>
        <label>
          対象外の端末（カンマ区切り、* はワイルドカード。利用者 PC など問い合わせない端末）
          <input value={f.excludeMachines ?? ''} placeholder="例：PC-*, KIOSK*" onChange={(e) => set('excludeMachines', e.target.value)} />
        </label>
        <small className="muted">
          パスワードは保存後に ******** で表示されます（変更しない場合はそのまま保存）。
        </small>
        <div className="actions">
          <input placeholder="接続テストする端末名" value={host} onChange={(e) => setHost(e.target.value)} />
          <button type="button" onClick={() => void test()} disabled={busy || !host.trim()}>
            接続テスト
          </button>
          <span className="spacer" />
          <button type="submit" className="primary" disabled={busy}>
            保存
          </button>
        </div>
        <small className="muted">
          接続テストは画面のサーバー（Server）から実行します。Batch とは実行アカウント・経路が異なる場合があります。
        </small>
        {msg && <div className={`alert ${msg.ok ? 'info' : 'error'}`}>{msg.text}</div>}
      </form>
    </section>
  )
}
