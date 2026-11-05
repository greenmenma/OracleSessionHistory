# OracleSessionHistory 開発ルール

Oracle の接続履歴（gv$session）を収集・閲覧するシステム。

## 構成

| プロジェクト | 役割 |
|---|---|
| `src/OracleSessionHistory.Batch` | Worker Service。監視対象ごとに数秒間隔でセッションを取得し、履歴 DB に保存する |
| `src/OracleSessionHistory.Server` | ASP.NET Core Minimal API。画面用の API と Client の配信 |
| `src/OracleSessionHistory.Client` | React + TypeScript（Vite）。サマリー・セッション履歴・接続数表・DB Link 利用元・設定 |
| `src/OracleSessionHistory.Core` | Batch/Server 共通。エンティティ、DbContext、Oracle 取得、追跡ロジック |
| `src/OracleSessionHistory.ServiceDefaults` | Aspire 共通設定（OpenTelemetry・ヘルスチェック） |
| `src/OracleSessionHistory.AppHost` | Aspire AppHost（デバッグ用。3 プロジェクトをまとめて起動） |
| `src/OracleSessionHistory.Tests` | xUnit テスト |

詳細は `docs/` を参照（`docs/architecture.md` から読むこと）。

## コマンド

```bash
# 全体をデバッグ起動（Aspire ダッシュボードが開く）
dotnet run --project src/OracleSessionHistory.AppHost

# ビルド・テスト
dotnet build src/OracleSessionHistory.sln
dotnet test src/OracleSessionHistory.sln

# Client の型チェック・ビルド
cd src/OracleSessionHistory.Client && npm run typecheck && npm run build
```

## 必ず守るルール

- **取得失敗時に切断扱いにしない。** `ISessionSource.CaptureAsync` は失敗時に例外を投げ、`CollectionService` は例外時にセッションへ一切触れない。RAC で応答の無いインスタンスのセッションも切断しない（`SessionSnapshot.RespondedInstances`）。
- **同じ Oracle セッションは 1 行。** キーは `(TargetId, InstId, Sid, Serial, LogonTime)`。新規行を作る前に既存行を必ず探す（`SessionTracker`）。
- 追跡ロジックは `SessionTracker`（DB 非依存の純粋関数）に置き、変更時は `SessionTrackerTests` にテストを追加する。
- Oracle の SQL は `OracleSessionSource` / `OracleDbLinkInspector` に集約する。付加情報（ジョブ・SQL テキスト等）の取得は `TryQueryAsync` で失敗を警告にとどめ、セッション取得自体を失敗させない。
- 監視対象の種類を増やすときは `ISessionSource`（と必要なら `IDbLinkInspector`）を実装し、`CoreServiceCollectionExtensions` に登録する。`SourceProviders` に定数を追加する。
- API は接続文字列のパスワードを返さない（`ConnectionStringMasker`）。エンティティをそのまま返す場合は `MonitorTarget` / `LinkSource` を含めないこと。
- 監視対象の設定を変更したら `UpdatedAt` を更新する（Batch はこの値で再起動を判断する）。
- 日時はアプリ・DB ともにローカル時刻（`DateTime.Now`）で扱う。Oracle の `logon_time` は DB サーバーの時刻。
- Windows でもデバッグできること。パスは `Path.Combine` を使い、OS 依存のコマンドを npm scripts に書かない。
- 依存パッケージを追加する場合は脆弱性（`dotnet list package --vulnerable` / `npm audit`）を確認する。

## コーディング規約

- コメント・ドキュメント・画面文言は日本語。識別子は英語。
- C#: nullable 有効、ファイルスコープ namespace、プライマリコンストラクタ可。
- TypeScript: `strict`。API の型は `src/api.ts` に集約する。外部 UI ライブラリ・ルーターは使わない（`router.ts` の簡易ハッシュルーター）。
- 履歴 DB はデバッグ時 SQLite、リリース時 SQL Server（`AppDb:Provider`。`appsettings.json` はリリース用の値で、デバッグ用の指定は AppHost と `launchSettings.json`。リリース環境は 1 つのため `appsettings.Development.json` は作らない）。LINQ は両方で変換できるものを使い、スキーマを変えたら `AppDatabaseTests` を通す。セッション削除は `AppDbMaintenance` 経由で行う。
- スキーマは `EnsureCreated` で作成している。エンティティにカラムを追加した場合は既存 DB を削除するか、`docs/development.md` の手順に従う。

## スキル

- `.claude/skills/oraclesessionhistory-dev` … 起動・デバッグ・検証手順
- `.claude/skills/oracle-session-collection` … Oracle 収集 SQL・分類・DB Link 特定の変更手順
