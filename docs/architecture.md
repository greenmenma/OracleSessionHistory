# アーキテクチャ

## 全体構成

```
┌────────────┐  gv$session 等   ┌──────────────────────┐
│ 監視対象    │ ◀──────────────── │ OracleSessionHistory.Batch  │
│ Oracle      │   （数秒間隔）    │  CollectorHost       │
│ (基幹公開DB)│                   │   └ 監視対象ごとのループ│
└────────────┘                   │  CollectionService   │
┌────────────┐  DB Link 利用元   │                      │
│ 接続元      │ ◀──────────────── │                      │
│ Oracle      │   （30秒間隔）    └──────────┬───────────┘
└────────────┘                              │ 書き込み
                                   ┌─────────▼─────────┐
                                   │ 履歴 DB            │
                                   │ (SQLite/SQL Server)│
                                   └─────────▲─────────┘
                                              │ 読み取り / 設定の書き込み
┌──────────────────────┐   /api     ┌────────┴──────────┐
│ OracleSessionHistory.Client │ ─────────▶ │ OracleSessionHistory.Server│
│ React + TypeScript   │            │ Minimal API         │
└──────────────────────┘            └─────────────────────┘
```

- **Batch** と **Server** は同じ履歴 DB（既定は SQLite の WAL モード。`AppDb:Provider=SqlServer` で SQL Server）を共有します。
- 監視対象・DB Link 接続元の設定は画面（Server）から履歴 DB に保存し、Batch は 10 秒ごとに設定を読み直して収集ループを起動・停止します（`MonitorTarget.UpdatedAt` の変化で再起動）。
- **AppHost**（Aspire）はデバッグ用で、3 つをまとめて起動し、ログ・トレースをダッシュボードに集約します。

## データモデル

| テーブル | 内容 |
|---|---|
| `Targets` | 監視対象（名前・Provider・接続文字列・取得間隔・収集状況） |
| `Sessions` | 追跡しているセッション。1 Oracle セッション = 1 行。`IsConnected=false` が履歴 |
| `LinkSources` | DB Link 接続元 DB（接続文字列・監視する DB Link） |
| `DbLinkUsages` | 接続元 DB で DB Link を使っていたセッション |
| `ProcessInspectionSettings` | 接続元サーバーのプロセス情報取得の設定（1 行） |

`Sessions` の一意キーは `(TargetId, InstId, Sid, Serial, LogonTime)` です。SID と SERIAL# は再利用されるため、ログオン時刻もキーに含めています。

## 収集と切断検知

1 回のポーリングの流れ（`CollectionService.PollSessionsAsync`）：

1. `ISessionSource.CaptureAsync` で `gv$instance` と `gv$session`（+ `gv$process`）を取得
2. 付加情報（SQL テキスト、クライアントドライバ、ジョブ、Data Pump、パラレル実行）を取得。失敗しても警告のみ
3. `SessionTracker.Apply` でスナップショットを反映
   - 既存行があれば更新（`LastSeenAt` 等）、無ければ追加
   - 接続中の行のうち、今回 **応答したインスタンス** に存在しないものを切断にする
   - 切断時刻は「最後に存在を確認した時刻」（`DisconnectedAt`）。検知時刻は `DisconnectDetectedAt`
   - 前回成功から取得間隔の 3 倍以上空いた場合は `DisconnectUncertain=true`（画面に「推定」と表示）
4. 保存し、監視対象の収集状況（最終成功時刻・連続失敗回数）を更新

### 誤って切断扱いにしないための仕組み

| 状況 | 動作 |
|---|---|
| Oracle に接続できない・SQL エラー | 例外として扱い、セッションには一切触れない。`ConsecutiveFailures` を加算 |
| RAC の一部インスタンスが応答しない | `gv$instance` に無いインスタンスのセッションは判定を保留 |
| 権限不足でジョブ情報等が取れない | 警告として記録し、セッションの取得・判定は継続 |
| Batch が停止していた | 再開後の初回で切断を検知するが、切断時刻は「推定」とする |
| 切断扱いにした行が再び見えた | 1 時間以内なら同じ行を接続中に戻す |

## 接続アプリの特定

`Sessions` の `AppName`（一覧・集計のキー）は、Batch の `ProcessInfoCollector` → `ProcessInfoService` が接続元サーバーの WMI/CIM（`CimProcessInspector`）から取得したプロセス情報で確定します。セッション収集とは独立したループで、失敗してもセッションの記録には影響しません。詳細は [process-info.md](process-info.md)。

## Provider（データソースの種類）

| Provider | 実装 | 用途 |
|---|---|---|
| `Oracle` | `OracleSessionSource`, `OracleDbLinkInspector` | 本番 |
| `Demo` | `DemoSessionSource`, `DemoDbLinkInspector` | Oracle 無しでの動作確認 |

監視対象は何件でも追加でき、それぞれ独立したループで収集します。Oracle 以外の DB を監視したい場合は `ISessionSource` を実装して登録します（[development.md](development.md)）。

## 主なクラス

| クラス | 役割 |
|---|---|
| `CollectorHost` (Batch) | 監視対象ごとの収集ループ管理・古い履歴の削除 |
| `CollectionService` (Core) | 取得 → 追跡 → 保存、DB Link 利用元の取得 |
| `SessionTracker` (Core) | 追跡ロジック（DB 非依存） |
| `ProgramClassifier` (Core) | PROGRAM からプロセス種別を判定 |
| `OracleSessionSource` (Core) | Oracle の SQL |
| `OracleDbLinkInspector` (Core) | DB Link 一覧・利用元の検索 |
| `ProcessInfoCollector` (Batch) / `ProcessInfoService` (Core) | 接続元プロセス情報の定期取得・ホスト単位のバックオフ |
| `CimProcessInspector` / `DemoProcessInspector` (Core) | WMI/CIM でのプロセス情報取得（Windows のみ）／デモ用 |
| `AppIdentifier` / `ClientProcessRules` (Core) | アプリ名の決定、ホスト名・PID・プール名の抽出、誤紐付けの判定（純粋関数） |
