# セットアップ・デバッグ・配置

## 必要なもの

- .NET SDK 10.0 以上
- Node.js 20 以上（npm）
- （任意）Visual Studio 2022 17.14 以降 / Visual Studio 2026、VS Code + C# Dev Kit、JetBrains Rider
- Oracle Client のインストールは **不要**（ODP.NET Managed Driver を使用）

## Windows でのデバッグ

### Visual Studio

1. `src/OracleSessionHistory.sln` を開く
2. 初回のみ `src/OracleSessionHistory.Client` で `npm install` を実行（開発者 PowerShell 等）
3. スタートアッププロジェクトを `OracleSessionHistory.AppHost` にして F5
4. ブラウザで Aspire ダッシュボードが開くので、`client` のエンドポイントをクリック

Batch・Server にはブレークポイントがそのまま効きます（AppHost から子プロセスとしてデバッグ接続されます）。

### VS Code

1. C# Dev Kit 拡張をインストール
2. `src/OracleSessionHistory.AppHost/AppHost.cs` を開いて「実行とデバッグ」→ C# → `OracleSessionHistory.AppHost`

### コマンドライン

```powershell
cd src\OracleSessionHistory.Client; npm install; cd ..\..
dotnet run --project src\OracleSessionHistory.AppHost
```

### Aspire を使わずに個別に起動する

```powershell
$env:AppDb__Provider = "Sqlite"   # launchSettings.json の http プロファイルでも設定済み
$env:ConnectionStrings__AppDb = "Data Source=C:\work\oraclesessionhistory\oraclesessionhistory.db"
dotnet run --project src\OracleSessionHistory.Server     # http://localhost:5292
dotnet run --project src\OracleSessionHistory.Batch      # 別ターミナルで同じ環境変数を設定
cd src\OracleSessionHistory.Client; npm run dev           # http://localhost:5173
```

デバッグ時は SQLite を使います（AppHost と各プロジェクトの `launchSettings.json` で `AppDb__Provider=Sqlite` を指定）。`appsettings.json` はリリース環境（SQL Server）用の設定のため、`dotnet run --no-launch-profile` で起動すると SQL Server に接続しようとします。

SQLite で `ConnectionStrings__AppDb` を指定しない場合、Batch・Server とも `%LOCALAPPDATA%\OracleSessionHistory\oraclesessionhistory.db` を使います。

## 履歴 DB の種類（SQLite / SQL Server）

`AppDb:Provider` で切り替えます。Batch と Server には **同じ** 値を設定してください。デバッグ時は SQLite、リリース環境は SQL Server とし、`appsettings.json` にはリリース環境用の仮の値を入れています（リリース環境は 1 つのため `appsettings.Development.json` 等の環境別ファイルは使いません）。

| `AppDb:Provider` | `ConnectionStrings:AppDb` | 用途 |
|---|---|---|
| `Sqlite`（未指定時。デバッグ用） | `Data Source=<ファイルパス>`。省略時は上記の既定パス | Batch と Server を同じマシンに置く場合 |
| `SqlServer`（`appsettings.json` の値。リリース用） | SQL Server の接続文字列（必須） | リリース環境 |

`appsettings.json` の仮の値（`SQLSERVER_HOST` / `OSH_USER` / `CHANGE_ME` を配置先で書き換える）:

```json
{
  "ConnectionStrings": {
    "AppDb": "Server=SQLSERVER_HOST;Database=OracleSessionHistory;User Id=OSH_USER;Password=CHANGE_ME;TrustServerCertificate=True"
  },
  "AppDb": { "Provider": "SqlServer" }
}
```

環境変数の場合は `AppDb__Provider=SqlServer` と `ConnectionStrings__AppDb=...` を指定します。

- 起動時にマイグレーションでテーブルを作成・更新します。データベースが無い場合は作成するため、初回だけ `dbcreator` 権限が必要です。事前に空のデータベースを作成しておけば、そのデータベースの `db_owner` で動作します（バージョンアップでスキーマが変わるため、`db_owner` のままにしてください）。
- 専用のデータベースを用意してください。
- マイグレーション導入前のバージョンで作成したデータベースは、そのまま引き継がれます。
- Batch の書き込み中に画面の参照が待たされないよう、`ALTER DATABASE OracleSessionHistory SET READ_COMMITTED_SNAPSHOT ON;` を推奨します。
- SQLite の既存データは移行されません。切り替えると履歴は空から始まります。

## 監視対象の登録

1. 画面の「設定」→「＋ 監視対象を追加」
2. 名前（例：基幹公開DB）、Provider=`Oracle`、接続文字列を入力

```
User Id=MONITOR;Password=xxxx;Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=dbhost)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=KOUKAI)))
```

   EZCONNECT 形式（`Data Source=dbhost:1521/KOUKAI`）も使えます。
3. 「接続テスト」で `gv$session` が読めることを確認して保存
4. 10 秒以内に Batch が収集を開始します

必要な権限は [oracle-privileges.md](oracle-privileges.md) を参照してください。

## 本番配置

### Server（IIS または Kestrel）

```powershell
dotnet publish src\OracleSessionHistory.Server -c Release -o C:\apps\OracleSessionHistory.Server
```

発行時に Client のビルド（`npm ci` → `npm run build`）も行い、結果を発行先の `wwwroot` に配置します。発行するマシンには Node.js が必要です。Client のビルドを省略する場合は `-p:SkipClientBuild=true` を指定します。

#### IIS の Default Web Site のサブフォルダに配置する

`http://サーバー/OracleSessionHistory/` のように、既存サイトの配下に配置できます。ビルド時にサブフォルダ名を指定する必要はありません（Client は相対パスで assets・API を参照します）。

1. [ASP.NET Core Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0)（.NET 10）をインストールする
2. IIS マネージャーでアプリケーションプールを作成する（.NET CLR バージョン：「マネージド コードなし」）
3. 「Default Web Site」を右クリック →「アプリケーションの追加」
   - エイリアス：`OracleSessionHistory`
   - アプリケーションプール：2 で作成したもの
   - 物理パス：`C:\apps\OracleSessionHistory.Server`（`dotnet publish` の出力先。`web.config` は publish で生成されます）
4. `http://サーバー/OracleSessionHistory/` を開く（末尾の `/` が無い場合は自動で付けます）

IIS がアプリケーションのパス（`/OracleSessionHistory`）を ASP.NET Core に渡すため、追加の設定は不要です。IIS 以外（リバースプロキシ配下の Kestrel 等）でサブフォルダに配置する場合は、Server の設定 `PathBase`（例：`/OracleSessionHistory`、環境変数 `PathBase`）を指定してください。

`appsettings.json` の `ConnectionStrings:AppDb` を SQL Server の実際の接続文字列に書き換えます。

### Batch（Windows サービス）

```powershell
dotnet publish src\OracleSessionHistory.Batch -c Release -o C:\apps\OracleSessionHistory.Batch
sc.exe create OracleSessionHistory.Batch binPath= "C:\apps\OracleSessionHistory.Batch\OracleSessionHistory.Batch.exe" start= auto
sc.exe start OracleSessionHistory.Batch
```

Server と **同じ** `ConnectionStrings:AppDb`・`AppDb:Provider` を設定してください。Batch は Linux の systemd でも動作します。

### 設定項目（Batch の `Collector` セクション）

| 項目 | 既定値 | 内容 |
|---|---|---|
| `ReloadSeconds` | 10 | 監視対象の設定変更を確認する間隔 |
| `RetentionDays` | 100 | 切断済み履歴の保持日数（接続数表で 2 ヶ月分使うため 70 以上推奨） |
| `SeedDemoTarget` | false（デバッグ起動時は true） | 監視対象が無いときにデモ用を登録 |

### ログ（エラーの記録）

Batch・Server とも、Warning 以上のログ（取得失敗・DB Link 接続元のエラー・API の例外・起動失敗など）をファイルに残します。

| 項目 | 内容 |
|---|---|
| 出力先 | アプリのフォルダの `logs`（例：`C:\apps\OracleSessionHistory.Server\logs\OracleSessionHistory.Server-20261105.log`） |
| ファイル | 1 日 1 ファイル。`RetainedDays` 日分（既定 31）を超えた古いファイルは自動で削除 |

設定は `appsettings.json` の `FileLog` セクションです。

| 項目 | 既定値 | 内容 |
|---|---|---|
| `Path` | `logs` | 出力フォルダ。相対パスはアプリのフォルダ基準 |
| `MinimumLevel` | `Warning` | 記録するレベル（`Information` にすると収集件数なども残る） |
| `RetainedDays` | 31 | 保持するファイル数（日数） |
| `Enabled` | true | false でファイル出力を止める |

- **IIS の場合**：アプリケーションプールの ID（`IIS AppPool\<プール名>`）に、`logs` フォルダの「変更」権限を付与してください。権限が無いとログは出力されません（アプリは動作します）。
- IIS で Server がまったく起動しない（HTTP 500.30 等）場合は、`web.config` の `stdoutLogEnabled` を `true` にすると、ASP.NET Core Module の起動ログが `.\logs\stdout` に出力されます。
- Batch を Windows サービスで動かしている場合は、Warning 以上が Windows のイベントログ（アプリケーション）にも出力されます。
- デバッグ時は Aspire ダッシュボードの各リソースのログでも確認できます。

### セキュリティ

- 接続文字列は履歴 DB に平文で保存されます。履歴 DB ファイルのアクセス権を Batch / Server の実行ユーザに限定してください。
- API はパスワードを `********` にマスクして返します。
- 画面には認証機能がありません。社内ネットワークに限定するか、IIS の Windows 認証等で保護してください。
