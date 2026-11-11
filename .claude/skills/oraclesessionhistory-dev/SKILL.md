---
name: oraclesessionhistory-dev
description: OracleSessionHistory の起動・デバッグ・動作確認の手順。アプリを起動したい、画面を確認したい、変更後に検証したい、Windows でのデバッグ方法を知りたいときに使う。
---

# OracleSessionHistory の開発・検証手順

## 1. ビルドとテスト

```bash
dotnet build src/OracleSessionHistory.sln
dotnet test src/OracleSessionHistory.sln
cd src/OracleSessionHistory.Client && npm install && npm run typecheck
```

すべて成功してから次に進む。

## 2. 起動（Aspire）

```bash
dotnet run --project src/OracleSessionHistory.AppHost
```

- ログに出る Login URL で Aspire ダッシュボードを開き、`client` の URL から画面を開く。
- 履歴 DB はリポジトリ直下の `.data/oraclesessionhistory.db`（SQLite）。`appsettings.json` はリリース用（SQL Server の仮の値）で、デバッグ時は AppHost / `launchSettings.json` が `AppDb__Provider=Sqlite` を指定している。`appsettings.Development.json` は使わない（リリース環境が 1 つのため）。
- デバッグ起動時（`Collector__SeedDemoTarget=true`）は監視対象が無いと Batch が「デモDB」（Provider=Demo）と過去 3 週間分の履歴を作成する（端末の行の件数は監査証跡の疑似データ）。Oracle 無しで全画面を確認できる。
- デモ登録時はプロセス情報の取得も有効になり、Demo の監視対象は疑似プロセス情報（`DemoProcessInspector`）を返す。
- デモは 20 回に 1 回取得失敗を発生させる。失敗時に接続数が減らない（切断扱いにならない）ことを確認できる。

### Aspire を使わない場合

ターミナルを 3 つ開き、同じ `ConnectionStrings__AppDb` を指定して起動する（`AppDb__Provider=Sqlite` とデモ登録は `launchSettings.json` で設定済み。省略時の DB は `%LOCALAPPDATA%/OracleSessionHistory/oraclesessionhistory.db`）。

```bash
# Windows (PowerShell) の例: $env:ConnectionStrings__AppDb="Data Source=C:\work\oraclesessionhistory.db"
dotnet run --project src/OracleSessionHistory.Server   # http://localhost:5292
dotnet run --project src/OracleSessionHistory.Batch
cd src/OracleSessionHistory.Client && npm run dev       # http://localhost:5173 （/api は 5292 へプロキシ）
```

## 3. 動作確認のチェックリスト

- サマリー：接続数・アプリ数・端末数・履歴数が表示され、5 秒ごとに更新される
- セッション履歴：検索条件なしで新しいログオン順に表示される。行クリックで詳細
- ORACLE.EXE のセッションに「DB Linkの可能性」バッジ、詳細に接続元の説明
- 接続アプリ（サマリーの下）：端末 → 実行ファイル（w3wp.exe・sqlplus.exe 等）→ プール・実行元の木で、本日〜6日前・本日〜1ヶ月・1ヶ月〜2ヶ月の列。Oracle 内部プロセス（ジョブ等）が出ないこと
- 接続アプリ：端末の行は監査証跡の疑似データの件数（下の段の合計とは一致しない）。上部に監査の件数の期間が出る
- 接続アプリ：初期表示は全展開。▾ / ▸（Shift+クリックで下の段もまとめて）と、右上の「全展開」「全縮小」「N 段目まで」で展開・縮小できる
- セッション履歴：「アプリ」列にアプリ名と補足。デモでは `DEV-PC07` が「取得失敗」、`ORACLE.EXE (J000)` は「Oracle 内部プロセスも表示」で出る
- 設定：「プロセス情報の取得」の保存（パスワード欄を触らずに保存しても保持される）。macOS では接続テストが「Windows のみ」になる
- sqlplus.exe：デモでは `BATCHSV02` が `sqlplus.exe: 月次集計.bat`、`OPE-PC01` が `sqlplus.exe: cmd.exe`。詳細に「実行元」、コマンドラインのパスワードは `********`
- 接続アプリ：DB Link 接続元（Provider=Demo）を登録すると、`KIKAN-DB01` → `ORACLE.EXE` → DB Link 接続元 → 接続元の端末（`KIKAN-AP01` 等）→ 接続元のアプリ（`w3wp.exe` → `JuchuWeb` 等）が出る
- 実行ユーザ：macOS ではデモの疑似ディレクトリにより `yamada` が「山田 太郎」等の表示名で出る。セッション履歴の「実行ユーザ」に「山田」と入れて検索できる
- セッション履歴：このシステム自身の接続（デモでは `OracleSessionHistory.Batch.exe`）は既定で出ない
- セッション履歴の「対象：DB Link利用元」：DB Link 接続元（Provider=Demo）を登録すると、「アプリ」に `IIS: JuchuWeb` 等が出る。セッション履歴の `ORACLE.EXE` の行に「DB Link 経由: アプリ（端末）」
- 設定：監視対象の追加・接続テスト、DB Link 接続元の追加 → 「DB Link 一覧を取得」→ 選択 → 保存
- API の確認: `curl http://localhost:<client-port>/api/summary`
- サブフォルダ配置の確認（IIS の代わり）: `dotnet publish src/OracleSessionHistory.Server -c Release -o <出力先>`（Client も wwwroot に配置される）後、出力先で `PathBase=/OracleSessionHistory` を指定して `dotnet OracleSessionHistory.Server.dll` を起動し、`/OracleSessionHistory/` で画面と API が動くこと

## 4. トラブルシューティング

- `NU1301`（ローカルソースが無い）: リポジトリ直下の `nuget.config` で nuget.org のみを使うようにしてある。別ディレクトリから実行していないか確認。
- 起動時にマイグレーションのエラー（`PendingModelChangesWarning` 等）: エンティティを変更してマイグレーションを追加していない。`docs/development.md` の「マイグレーション」の手順で SQLite・SQL Server の両方に追加する。
- Server だけ 500 になる: Aspire ダッシュボードの `server` のログ（構造化ログ）で例外を確認。EF Core の LINQ が SQLite で変換できないケースが多い。
- Oracle 接続エラー: 設定画面の「接続テスト」でメッセージを確認。権限は `docs/oracle-privileges.md`。
