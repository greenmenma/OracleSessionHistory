# セットアップ・デバッグ・配置

## 必要なもの

- .NET SDK 10.0 以上
- Node.js 20 以上（npm）
- （任意）Visual Studio 2022 17.14 以降 / Visual Studio 2026、VS Code + C# Dev Kit、JetBrains Rider
- Oracle Client のインストールは **不要**（ODP.NET Managed Driver を使用）

## Windows でのデバッグ

### Visual Studio

1. `src/OpenDbSession.sln` を開く
2. 初回のみ `src/OpenDbSession.Client` で `npm install` を実行（開発者 PowerShell 等）
3. スタートアッププロジェクトを `OpenDbSession.AppHost` にして F5
4. ブラウザで Aspire ダッシュボードが開くので、`client` のエンドポイントをクリック

Batch・Server にはブレークポイントがそのまま効きます（AppHost から子プロセスとしてデバッグ接続されます）。

### VS Code

1. C# Dev Kit 拡張をインストール
2. `src/OpenDbSession.AppHost/AppHost.cs` を開いて「実行とデバッグ」→ C# → `OpenDbSession.AppHost`

### コマンドライン

```powershell
cd src\OpenDbSession.Client; npm install; cd ..\..
dotnet run --project src\OpenDbSession.AppHost
```

### Aspire を使わずに個別に起動する

```powershell
$env:ConnectionStrings__AppDb = "Data Source=C:\work\opendbsession\opendbsession.db"
dotnet run --project src\OpenDbSession.Server     # http://localhost:5292
dotnet run --project src\OpenDbSession.Batch      # 別ターミナルで同じ環境変数を設定
cd src\OpenDbSession.Client; npm run dev           # http://localhost:5173
```

`ConnectionStrings__AppDb` を指定しない場合、Batch・Server とも `%LOCALAPPDATA%\OpenDbSession\opendbsession.db` を使います。

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
cd src\OpenDbSession.Client; npm ci; npm run build   # Server\wwwroot に出力
dotnet publish src\OpenDbSession.Server -c Release -o C:\apps\OpenDbSession.Server
```

`appsettings.json` の `ConnectionStrings:AppDb` に履歴 DB のパスを指定します。

### Batch（Windows サービス）

```powershell
dotnet publish src\OpenDbSession.Batch -c Release -o C:\apps\OpenDbSession.Batch
sc.exe create OpenDbSession.Batch binPath= "C:\apps\OpenDbSession.Batch\OpenDbSession.Batch.exe" start= auto
sc.exe start OpenDbSession.Batch
```

Server と **同じ** `ConnectionStrings:AppDb` を設定してください。Batch は Linux の systemd でも動作します。

### 設定項目（Batch の `Collector` セクション）

| 項目 | 既定値 | 内容 |
|---|---|---|
| `ReloadSeconds` | 10 | 監視対象の設定変更を確認する間隔 |
| `RetentionDays` | 100 | 切断済み履歴の保持日数（接続数表で 2 ヶ月分使うため 70 以上推奨） |
| `SeedDemoTarget` | false（Development は true） | 監視対象が無いときにデモ用を登録 |

### セキュリティ

- 接続文字列は履歴 DB に平文で保存されます。履歴 DB ファイルのアクセス権を Batch / Server の実行ユーザに限定してください。
- API はパスワードを `********` にマスクして返します。
- 画面には認証機能がありません。社内ネットワークに限定するか、IIS の Windows 認証等で保護してください。
