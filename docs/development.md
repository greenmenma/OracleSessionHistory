# 開発ガイド

ルールの要約はリポジトリ直下の `CLAUDE.md` を参照してください。

## ビルド・テスト

```bash
dotnet build src/OracleSessionHistory.sln
dotnet test src/OracleSessionHistory.sln
cd src/OracleSessionHistory.Client && npm run typecheck
```

テストは主に次を確認しています。

- `SessionTrackerTests`：同一セッションの更新、切断検知、インスタンス無応答時に切断しない、推定フラグ、復帰
- `CollectionServiceTests`：SQLite インメモリでの取得失敗時の非切断、1 セッション 1 行、DB Link のプロセス一致
- `ProgramClassifierTests`：ORACLE.EXE / Jnnn / Pnnn 等の分類
- `AppIdentifierTests` / `ClientProcessRulesTests`：アプリ名の決定、ホスト名・PID・プール名の抽出、誤紐付けの判定
- `ProcessInfoServiceTests` / `HostBackoffTests`：プロセス情報の取得・失敗時のバックオフ・対象外の判定
- `ApiQueryTests`：Oracle 内部プロセスの既定除外、接続アプリ表の集計
- `AppDbMigrationTests`：マイグレーションの追加漏れ、新規 DB への適用、`EnsureCreated` で作成した DB の引き継ぎ

## 監視対象の種類（Provider）を追加する

1. `OracleSessionHistory.Core/Collection` に `ISessionSource` の実装を追加（取得失敗時は例外を投げる）
2. 必要なら `IDbLinkInspector` を実装
3. `SourceProviders` に定数を追加し `All` に含める
4. `CoreServiceCollectionExtensions.AddOracleSessionHistoryCore` に登録

## 収集項目を追加する

`ObservedSession` → `OracleSessionSource` の SQL → `TrackedSession` → `SessionTracker.CopyObserved` → API（DTO）→ `Client/src/api.ts` → 画面 の順に追加します。

## スキーマ変更

SQLite と SQL Server の両方で動くこと。`AppDatabaseTests` が SQL Server 向けのモデルを検証します（サーバー不要）。

- インデックスを張る文字列プロパティには `HasMaxLength` を指定する（`nvarchar(max)` はインデックスにできない）。
- 監視対象から同じテーブルへの削除の連鎖経路（CASCADE / SET NULL）が 2 本以上にならないようにする。DB 側で外せない参照は `ClientSetNull` にし、削除前に `AppDbMaintenance` で外す。
- LINQ は両方のプロバイダーで変換できるものを使う。プロバイダー固有の SQL（`PRAGMA` 等）は `db.Database.IsSqlite()` などで分岐する。

### マイグレーション

スキーマは EF Core マイグレーションで管理しています。マイグレーションはプロバイダーごとに別プロジェクトです。

| プロジェクト | 対象 |
|---|---|
| `OracleSessionHistory.Migrations.Sqlite` | SQLite（デバッグ） |
| `OracleSessionHistory.Migrations.SqlServer` | SQL Server（リリース） |

Batch・Server は起動時に未適用のマイグレーションを自動で適用します（`AppDatabase.MigrateAsync`。同時に起動しても EF Core のロックで 1 回だけ適用されます）。

エンティティや `AppDbContext` を変更したら、**両方** に同じ名前でマイグレーションを追加します（`dotnet-ef` はリポジトリの `dotnet-tools.json` で固定。初回は `dotnet tool restore`）。

```bash
dotnet tool restore
dotnet ef migrations add <名前> --project src/OracleSessionHistory.Migrations.Sqlite --startup-project src/OracleSessionHistory.Migrations.Sqlite --output-dir Migrations
dotnet ef migrations add <名前> --project src/OracleSessionHistory.Migrations.SqlServer --startup-project src/OracleSessionHistory.Migrations.SqlServer --output-dir Migrations
```

- 生成されたマイグレーションを確認し、`dotnet test` を通す（`AppDbMigrationTests` がモデルとマイグレーションの不一致を検出します）。
- SQLite は列の変更・削除をテーブルの再作成で行います。データ量が多い場合は時間がかかることがあります。
- 取り消す場合は `dotnet ef migrations remove`（同じオプション）。
- 本番の SQL Server へ適用する前に SQL を確認したい場合は、`dotnet ef migrations script --idempotent --project src/OracleSessionHistory.Migrations.SqlServer --startup-project src/OracleSessionHistory.Migrations.SqlServer -o migrate.sql` でスクリプトを出力できます。

マイグレーション導入前に `EnsureCreated` で作成した DB は、初回起動時に `Initial` を適用済みとして記録し、データをそのまま引き継ぎます。

## API 一覧

| メソッド | パス | 内容 |
|---|---|---|
| GET | `/api/providers` | Provider 一覧 |
| GET/POST | `/api/targets` | 監視対象の一覧・追加 |
| PUT/DELETE | `/api/targets/{id}` | 監視対象の更新・削除 |
| POST | `/api/targets/test` | 接続テスト |
| GET | `/api/summary?targetId=` | サマリー |
| GET | `/api/sessions?...` | セッション履歴の検索 |
| GET | `/api/sessions/{id}` | セッション詳細（DB Link 接続元を含む） |
| GET | `/api/connection-counts?targetId=&q=` | 接続アプリ表（端末 × アプリ） |
| GET/PUT | `/api/process-settings` | プロセス情報取得の設定（パスワードは返さない） |
| POST | `/api/process-settings/test` | プロセス情報取得の接続テスト |
| GET/POST | `/api/targets/{id}/link-sources` | DB Link 接続元の一覧・追加 |
| PUT/DELETE | `/api/link-sources/{id}` | DB Link 接続元の更新・削除 |
| POST | `/api/db-links` | 接続文字列から DB Link 一覧を取得 |
| GET | `/api/db-link-usages?...` | DB Link 利用元の一覧 |
