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

## 監視対象の種類（Provider）を追加する

1. `OracleSessionHistory.Core/Collection` に `ISessionSource` の実装を追加（取得失敗時は例外を投げる）
2. 必要なら `IDbLinkInspector` を実装
3. `SourceProviders` に定数を追加し `All` に含める
4. `CoreServiceCollectionExtensions.AddOracleSessionHistoryCore` に登録

## 収集項目を追加する

`ObservedSession` → `OracleSessionSource` の SQL → `TrackedSession` → `SessionTracker.CopyObserved` → API（DTO）→ `Client/src/api.ts` → 画面 の順に追加します。

## スキーマ変更

`EnsureCreated` を使っているため、エンティティを変更した場合、開発環境では `.data/oraclesessionhistory.db*` を削除して再作成します。本番で履歴を残したまま変更する場合は、EF Core マイグレーションの導入（`dotnet ef migrations add`）を検討してください。

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
| GET | `/api/connection-counts?targetId=&q=` | 接続数表 |
| GET/POST | `/api/targets/{id}/link-sources` | DB Link 接続元の一覧・追加 |
| PUT/DELETE | `/api/link-sources/{id}` | DB Link 接続元の更新・削除 |
| POST | `/api/db-links` | 接続文字列から DB Link 一覧を取得 |
| GET | `/api/db-link-usages?...` | DB Link 利用元の一覧 |
