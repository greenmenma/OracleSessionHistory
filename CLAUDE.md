# OracleSessionHistory 開発ルール

Oracle の接続履歴（gv$session）を収集・閲覧するシステム。

## 構成

| プロジェクト | 役割 |
|---|---|
| `src/OracleSessionHistory.Batch` | Worker Service。監視対象ごとに数秒間隔でセッションを取得し、履歴 DB に保存する |
| `src/OracleSessionHistory.Server` | ASP.NET Core Minimal API。画面用の API と Client の配信 |
| `src/OracleSessionHistory.Client` | React + TypeScript（Vite）。サマリー・セッション履歴・接続アプリ・DB Link 利用元・設定。sln には `.esproj` で含める（`dotnet build` では npm を実行しない）。ビルド結果は `dist` に出力し、Server の `dotnet publish` 時に発行先の `wwwroot` へ配置する |
| `src/OracleSessionHistory.Core` | Batch/Server 共通。エンティティ、DbContext、Oracle 取得、追跡ロジック、接続元プロセス情報（`Processes/`） |
| `src/OracleSessionHistory.Migrations.Sqlite` / `.SqlServer` | 履歴 DB の EF Core マイグレーション（プロバイダーごと） |
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
- **接続アプリの特定**（接続元サーバーの WMI/CIM）は `ProcessInfoService` に置き、セッション収集と独立させる（失敗しても `Sessions` の接続・切断に触れない）。アプリ名の規則は `AppIdentifier`（純粋関数）に置き、`AppIdentifierTests` にテストを追加する。`SessionTracker` は `ProcInfoState=Resolved` の `AppName` を上書きしない（`docs/process-info.md`）。
- 画面・集計は既定で Oracle 内部プロセスとこのシステム自身の接続（`ProcessKinds.Self`。`OracleDb.OpenAsync` が MODULE に設定する `ProgramClassifier.SelfModuleName` で判定）を除く（`SessionQueries.WhereVisible`。DB Link の可能性は表示する）。
- プロセス情報は、接続中のクライアントセッションと DB Link 利用元（`DbLinkUsage`）について取得する。両者は `IProcessInfoRecord` として同じ規則で扱う。実行元（親プロセス）でアプリを区別する exe（sqlplus.exe 等）は `AppIdentifier.LaunchedTools` に追加する。
- コマンドラインは保存前に `ClientProcessRules.MaskCredentials` でパスワードを隠す（`ProcessInfoService.Apply`）。
- DB Link 経由（ORACLE.EXE）で接続元を特定したセッションは `TrackedSession.LinkOriginMachine` / `LinkOriginApp`（`LinkOriginSync` が更新）を持ち、接続アプリ表・ランキング・完全一致の検索は接続元の端末・アプリとして扱う。
- 監視対象の種類を増やすときは `ISessionSource`（と必要なら `IDbLinkInspector`）を実装し、`CoreServiceCollectionExtensions` に登録する。`SourceProviders` に定数を追加する。
- API は接続文字列・プロセス情報取得アカウントのパスワードを返さない（`ConnectionStringMasker`、`ProcessSettingsDto.HasPassword`）。エンティティをそのまま返す場合は `MonitorTarget` / `LinkSource` を含めないこと。
- 監視対象の設定を変更したら `UpdatedAt` を更新する（Batch はこの値で再起動を判断する）。
- エラーは `ILogger` で Warning 以上を出す（ファイル `logs/` に残る。ServiceDefaults の `AddFileLogging`）。例外を握りつぶす場合も必ずログに残す。
- 日時はアプリ・DB ともにローカル時刻（`DateTime.Now`）で扱う。Oracle の `logon_time` は DB サーバーの時刻。
- Windows でもデバッグできること。パスは `Path.Combine` を使い、OS 依存のコマンドを npm scripts に書かない。
- 依存パッケージを追加する場合は脆弱性（`dotnet list package --vulnerable` / `npm audit`）を確認する。

## コーディング規約

- コメント・ドキュメント・画面文言は日本語。識別子は英語。
- C#: nullable 有効、ファイルスコープ namespace、プライマリコンストラクタ可。
- TypeScript: `strict`。API の型は `src/api.ts` に集約する。外部 UI ライブラリ・ルーターは使わない（`router.ts` の簡易ハッシュルーター）。
- 履歴 DB はデバッグ時 SQLite、リリース時 SQL Server（`AppDb:Provider`。`appsettings.json` はリリース用の値で、デバッグ用の指定は AppHost と `launchSettings.json`。リリース環境は 1 つのため `appsettings.Development.json` は作らない）。LINQ は両方で変換できるものを使い、スキーマを変えたら `AppDatabaseTests` を通す。セッション削除は `AppDbMaintenance` 経由で行う。
- スキーマは EF Core マイグレーションで管理する（起動時に Batch・Server が `AppDatabase.MigrateAsync` で適用）。エンティティを変更したら **SQLite・SQL Server の両方** に同じ名前のマイグレーションを追加する（`docs/development.md`）。`AppDbMigrationTests` が追加漏れを検出する。
- IIS のサブフォルダ（仮想アプリケーション）に配置できること。Client は assets・API を相対パス（`api/...`）で参照し、先頭 `/` の絶対パスを書かない。

## スキル

- `.claude/skills/oraclesessionhistory-dev` … 起動・デバッグ・検証手順
- `.claude/skills/oracle-session-collection` … Oracle 収集 SQL・分類・DB Link 特定の変更手順
