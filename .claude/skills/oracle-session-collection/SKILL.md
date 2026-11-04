---
name: oracle-session-collection
description: Oracle からのセッション収集（gv$session の SQL、プロセス種別の分類、ジョブ紐付け、DB Link 利用元の特定、切断検知）を変更・拡張するときに使う。新しい監視対象の種類（Provider）を追加するときにも使う。
---

# Oracle セッション収集の変更手順

## 関係するファイル

| ファイル | 内容 |
|---|---|
| `src/OracleSessionHistory.Core/Collection/Oracle/OracleSessionSource.cs` | gv$session 等の SQL と ObservedSession への変換 |
| `src/OracleSessionHistory.Core/Tracking/ProgramClassifier.cs` | PROGRAM から種別（DB Link の可能性・Jnnn 等）を判定 |
| `src/OracleSessionHistory.Core/Tracking/SessionTracker.cs` | 既存行の更新・新規追加・切断検知（純粋関数） |
| `src/OracleSessionHistory.Core/DbLinks/OracleDbLinkInspector.cs` | 接続元 DB の DB Link 一覧・利用セッションの検索 |
| `src/OracleSessionHistory.Core/Services/CollectionService.cs` | 取得 → 追跡 → 保存 |
| `docs/oracle-privileges.md` | 必要な権限 |

## 守ること

1. **必須の SQL（gv$instance, gv$session）が失敗したら例外にする。** 付加情報は `OracleDb.TryQueryAsync` で取得し、権限不足（ORA-00942）でも収集を止めない。
2. gv$ ビューへの相関スカラー副問い合わせは RAC で遅いので使わない。別クエリで取得して C# 側で結合する。
3. 自分自身（監視用セッション）は `sys_context('USERENV','SID')` で除外する。
4. SQL テキストは `gv$sqlarea` を **セッションが参照している sql_id に絞って** 取得する（共有プール全体を走査しない）。
5. 新しい列を収集したら `ObservedSession` → `TrackedSession` → `SessionTracker.CopyObserved` → API DTO → `src/api.ts` → 画面 の順に追加する。
6. 必要な権限が増えたら `docs/oracle-privileges.md` を更新する。

## プロセス種別を追加する

`ProgramClassifier.Classify` に正規表現を追加し、`ProcessKinds` に定数、`format.ts` の `kindLabels` と `KindBadge` の色を追加する。`ProgramClassifierTests` にケースを追加する。

## DB Link 利用元の特定方法

1. **プロセス一致（確定）**: 監視対象側の DB Link セッションの `gv$session.process` は接続元サーバープロセスの SPID（Windows は `pid:tid`）。接続元 DB の `gv$process.spid` と一致するセッションが利用元。
2. **SQL 一致**: 接続元 DB で、セッションが実行中/直前の SQL に `@DBLINK` を含むもの。
3. **シノニム一致**: DB Link を参照するシノニム名を SQL に含むもの（誤検知の可能性あり）。

## 新しい Provider（Oracle 以外）を追加する

1. `ISessionSource` を実装（失敗時は例外）。必要なら `IDbLinkInspector` も実装。
2. `SourceProviders` に定数を追加し `All` に含める。
3. `CoreServiceCollectionExtensions.AddOracleSessionHistoryCore` に登録。
4. `docs/architecture.md` の Provider 一覧を更新。
