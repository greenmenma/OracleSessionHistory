# OpenDbSession

Oracle の接続履歴（`gv$session`）を数秒間隔で収集し、**どの端末・プログラム・ユーザ（＝どのシステム）が DB を使っているか**を閲覧するシステムです。

- 監視対象の Oracle（例：基幹公開DB）を画面から登録すると、数秒間隔でセッションを取得
- 同じ Oracle セッションは 1 行を更新し、切断を検知したら履歴として保存（取得失敗時は切断扱いにしない）
- 実行端末・実行プログラム・実行スキーマ・接続ユーザ・OS ユーザ・モジュール・クライアントドライバ等を記録
- `ORACLE.EXE` / `oracle@host` からの接続を **DB Link の可能性** として表示し、接続元 DB を登録すると利用元のシステムを特定
- `ORACLE.EXE (J000)` などのジョブスレーブにスケジューラジョブ / DBMS_JOB / Data Pump の情報を紐付け、パラレルスレーブにはコーディネータの情報を紐付け
- 監視対象は複数登録可能（Provider を追加すれば Oracle 以外にも拡張可能）
- 画面：サマリー / セッション履歴 / 接続数表 / DB Link 利用元 / 設定

## 構成

```
src/
  OpenDbSession.Batch          Worker Service（収集）
  OpenDbSession.Server         ASP.NET Core API
  OpenDbSession.Client         React + TypeScript（Vite）
  OpenDbSession.AppHost        .NET Aspire（デバッグ用）
  OpenDbSession.Core           共通ライブラリ（収集・追跡ロジック、EF Core）
  OpenDbSession.ServiceDefaults
  OpenDbSession.Tests
docs/                          ドキュメント
```

## クイックスタート

必要なもの：.NET SDK 10、Node.js 20 以上

```bash
cd src/OpenDbSession.Client && npm install && cd ../..
dotnet run --project src/OpenDbSession.AppHost
```

Aspire ダッシュボードから `client` の URL を開きます。Development 環境では Oracle が無くても動作確認できるよう「デモDB」が自動登録されます。

Windows（Visual Studio / VS Code / Rider）でのデバッグ方法は [docs/setup.md](docs/setup.md) を参照してください。

## ドキュメント

| ドキュメント | 内容 |
|---|---|
| [docs/architecture.md](docs/architecture.md) | 全体構成・データモデル・収集と切断検知の仕組み |
| [docs/setup.md](docs/setup.md) | 開発環境・Windows でのデバッグ・本番配置 |
| [docs/oracle-privileges.md](docs/oracle-privileges.md) | 監視用 Oracle ユーザに必要な権限 |
| [docs/screens.md](docs/screens.md) | 画面仕様 |
| [docs/dblink-and-jobs.md](docs/dblink-and-jobs.md) | DB Link 利用元の特定・ジョブ等の紐付け |
| [docs/limitations.md](docs/limitations.md) | できないこと・制約事項 |
| [docs/development.md](docs/development.md) | 開発ルール・拡張方法 |

## ライセンス

MIT
