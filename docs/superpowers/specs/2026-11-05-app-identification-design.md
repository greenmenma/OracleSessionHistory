# 接続アプリの特定（クライアントプロセス情報の取得）設計

## 目的・背景

このシステムの今回の目的は **「対象 DB に接続しているアプリを特定する」** こと。

現状は `gv$session` の PROGRAM / MACHINE / OSUSER / MODULE だけで判断しているが、

- `w3wp.exe`（特に .NET Framework 系の IIS アプリ）はどのアプリケーションプール（＝どのアプリ）か分からないことが多い
- PROGRAM はクライアントの申告値で、同じ exe 名の別アプリを区別できない
- アプリの特定に詳細ダイアログを開く必要がある
- `ORACLE.EXE (J000)` などの Oracle 内部プロセスが大量に表示され、目的のアプリが埋もれる

という問題がある。

## 前提（ユーザ確認済み）

- 接続元のアプリサーバー・Batch を動かすサーバーはすべて Windows で、同じドメインに参加している。
- 接続元サーバーへのアクセスには **全サーバー共通のドメインアカウント 1 つ** を使う。
- 「接続数表」の画面を **端末 × アプリ単位** の一覧にする（アプリ単位の一覧はこの画面で兼ねる）。
- `ORACLE.EXE (*)` などの Oracle 内部プロセスは基本的に表示しない。ただし DB Link の可能性がある `ORACLE.EXE` / `oracle@host`（括弧なし・SHAD）は表示する。
- 詳細ダイアログを開かなくても、一覧で実行プログラム（アプリ）が分かるようにする。
- 既存設計をガッツリ見直してよい。

## ゴール

1. Batch が、接続元サーバー（MACHINE）の WMI/CIM に登録アカウントで問い合わせ、`gv$session.process` の PID から次を取得してセッション行に保存する。
   - プロセス名、実行ファイルのフルパス、コマンドライン、実行ユーザ（プロセスのオーナー）、プロセス開始時刻、親プロセス名
   - `w3wp.exe` は **アプリケーションプール名**（コマンドライン `-ap "名前"`）。取れれば IIS のサイト名・アプリのパス（`root\WebAdministration`）。
2. 取得結果から **アプリ名**（一覧・集計のキー）を決め、セッション行に保存する。取得できない場合は従来の情報（PROGRAM）から決める。
3. セッション履歴・接続数表・サマリーで、アプリ名を主表示にする。
4. Oracle 内部プロセスを既定で非表示にする（切り替えで表示可能）。

## 非ゴール

- 接続元サーバー上での常駐エージェントの導入（エージェントレスで行う）。
- Linux など Windows 以外の接続元サーバーからのプロセス情報取得（取得不可として扱う）。
- 過去に記録済みのセッション（プロセスが既に終了しているもの）のプロセス情報の遡及取得。
- 利用者個人（アプリのログインユーザ）の特定。

## アンチゴール（動いても失敗とみなすもの）

- プロセス情報の取得の遅延・失敗が **セッション収集（gv$session のポーリング）を遅らせる・止める・切断扱いを誤らせる** こと。
- 応答しないサーバーへ数秒おきに問い合わせ続ける（タイムアウト待ちで詰まる、ログが埋まる）こと。
- PID の再利用などで **別のプロセスの情報を誤って紐付ける** こと。
- 資格情報のパスワードが API・画面・ログに出ること。

## 制約

- CLAUDE.md の「必ず守るルール」に従う（取得失敗時に切断扱いにしない、1 セッション 1 行、`ILogger` で Warning 以上、ローカル時刻、Windows でデバッグ可能、依存パッケージの脆弱性確認 など）。
- 開発は macOS でも行うため、Windows 専用 API は Windows 以外ではビルドでき、実行時に「未対応」として扱えること（`OperatingSystem.IsWindows()`）。
- 履歴 DB は SQLite / SQL Server 両対応。マイグレーションを両方に追加する。
- Client は外部 UI ライブラリ・ルーターを使わない。

## 設計

### 1. プロセス情報の取得（Core）

#### 取得方式

- `Microsoft.Management.Infrastructure`（CIM）を使い、`CimSession` で接続先サーバーの `root\cimv2` に問い合わせる。
  - プロトコルは **WinRM（WS-Man）を先に試し、失敗したら DCOM**。成功したプロトコルはサーバーごとにキャッシュする。
  - 資格情報：設定画面で登録したドメインアカウント。**アカウントが空の場合は Batch の実行アカウント**（サービスのログオンアカウント）で接続する。
  - タイムアウト：既定 10 秒（設定可能）。
- クエリは必要な PID に絞る：`SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE ProcessId = 1 OR ProcessId = 2 ...`
- オーナーは `Win32_Process.GetOwner` メソッド（Domain\User）。失敗しても他の項目は保存する。
- 親プロセス名は、親 PID を同じクエリに含めて取得する（取れなければ空）。
- `w3wp.exe` の場合：
  - コマンドラインの `-ap "PoolName"` からプール名を取り出す（純粋関数、テストあり）。
  - `root\WebAdministration` の `Application` クラスから `ApplicationPool = 'PoolName'` のサイト名・パスを取得する（失敗・名前空間なしは無視）。結果はサーバー×プールで 1 時間キャッシュ。

#### 抽象化

```csharp
public interface IProcessInspector
{
    // machine に対して PID 群の情報を取得する。サーバー単位の失敗は例外
    Task<IReadOnlyList<ClientProcessInfo>> InspectAsync(string host, IReadOnlyCollection<int> pids,
        ProcessInspectorCredential? credential, CancellationToken ct);
}
```

- 実装：`CimProcessInspector`（Windows）、`DemoProcessInspector`（Demo Provider の監視対象用。疑似データを返す）。
- Windows 以外で `CimProcessInspector` が呼ばれた場合は「この OS では取得できません」として扱う。

#### 対象とするセッション・ホスト名

- 対象：`ProcessKind == Client` かつ接続中で、プロセス情報が未取得（`ProcInfoState = Pending`）のセッション。
  - DB Link（ORACLE.EXE）やジョブ等は対象外（接続元は DB サーバー自身で、取っても oracle.exe しか分からない）。
- PID：`gv$session.process` から数値部分を取り出す（`pid:tid` の場合は先頭）。数値でなければ `NotApplicable`。
- ホスト名：`gv$session.machine` から取り出す。`DOMAIN\HOST` の場合は `HOST`。空なら `NotApplicable`。
- 対象外にする端末（例：利用者 PC の端末名パターン）を設定でカンマ区切り指定できるようにする（ワイルドカード `*`）。

#### ホスト名の解決（判断：ビルダーチェック後に決定）

- `DOMAIN\HOST` は `HOST` にする。まずそのまま接続し、名前解決に失敗した場合は設定の「DNS サフィックス」（任意、例 `corp.example.local`）を付けて再試行する。
- 名前解決できない・WinRM/DCOM とも接続できない端末は `Failed`（理由付き）でバックオフ再試行。Windows 以外の端末などは「対象外端末パターン」で `NotApplicable` にできる。

#### PID 再利用・誤紐付けの防止

次のどちらかに当たる場合は紐付けず `Mismatch` にする。

- プロセス開始時刻が Oracle の `logon_time` より **5 分以上後**（クライアントと DB の時計差を許容するため 5 分）。CIM の `CreationDate` はタイムゾーン付きなのでローカル時刻に変換して比較する（DB サーバーと Batch が同じタイムゾーンという既存の前提を使う）。
- `gv$session.program` が `*.exe` の形で、取得したプロセス名と一致しない（大文字小文字無視）。

#### 状態（`ProcInfoState`）

| 値 | 意味 | 再試行 |
|---|---|---|
| `Pending` | 未取得 | する |
| `Resolved` | 取得済み | しない |
| `NotFound` | 問い合わせ成功したが PID が存在しない（終了済み） | しない |
| `Mismatch` | 別プロセスと判断 | しない |
| `Failed` | サーバーに接続できない・アクセス拒否など | 一定時間後に再試行（後述） |
| `NotApplicable` | 対象外（PID・ホスト名なし、対象外端末、DB Link 等） | しない |
| `Disabled` | 機能が無効 | 有効化後に `Pending` として扱う |

- `ProcInfoError` に失敗理由（短いメッセージ）を保存し、画面で表示する。

#### 実行タイミングとバックオフ

- Batch に専用の `BackgroundService`（`ProcessInfoCollector`）を追加し、**セッション収集ループとは独立** に動かす（既定 10 秒間隔）。
- 1 回の処理：`Pending`（と再試行時刻を過ぎた `Failed`）の接続中セッションをホストごとにまとめ、ホスト単位で並列（最大 4 並列）に問い合わせ、結果を保存する。
- ホスト単位の失敗はメモリ上のバックオフに記録する：失敗するたびに 1 分 → 5 分 → 15 分 → 60 分（上限）で再試行。成功でリセット。
  - バックオフ中のホストのセッションは `Failed` のまま（`ProcInfoError` に「再試行待ち」）。
  - ホスト単位の失敗は **最初の失敗と、成功に戻ったとき** に Warning/Information ログ（同じ失敗を毎回ログに出さない）。
- 切断済みになったセッションは対象外（プロセスが無いはず）。

### 2. アプリ名の決定（Core、純粋関数 `AppIdentifier`）

セッション行に `AppName`（集計キー・主表示）と `AppDetail`（補足表示）を保存する。

| 条件 | AppName | AppDetail |
|---|---|---|
| w3wp.exe でプール名あり | `IIS: プール名` | サイト名＋パス（例：`Default Web Site/Foo`）、なければ実行ファイルパス |
| dotnet.exe でコマンドラインに `.dll` | dll のファイル名（例：`Foo.Api.dll`） | dll のフルパス |
| java.exe / javaw.exe で `-jar x.jar` | jar のファイル名 | jar のパス |
| その他で実行ファイルパスあり | 実行ファイル名（例：`Foo.exe`） | 実行ファイルのフォルダ |
| プロセス情報なし・DbLinkSuspect | `gv$session.program`（空なら `(不明)`） | MODULE（PROGRAM と異なる場合） |

- `w3wp.exe` でプロセス情報が取れず、OSUSER が `IIS APPPOOL\名前` の場合は `IIS: 名前` とする。
- 新規セッション作成時に PROGRAM から仮の AppName を設定し、プロセス情報取得後に上書きする。`SessionTracker.CopyObserved` はプロセス情報で確定した AppName を上書きしない。
- 既存データはマイグレーションで `AppName = Program`（null は `(不明)`）を設定する。

### 3. 表示対象（Oracle 内部プロセスの非表示）

- 「表示対象の種別」= `Client`, `DbLinkSuspect`。それ以外（`JobSlave`, `ParallelSlave`, `AqSlave`, `DataPump`, `OracleInternal`, `Background`）は既定で非表示。
- セッション履歴：検索条件に「Oracle 内部プロセスも表示」チェック（既定オフ）。種別で内部プロセスを明示選択した場合はそれを優先。
- 接続数表・サマリー（接続数・アプリ数・端末数・ランキング）：内部プロセスを既定で除外。
- 収集自体は従来どおり行う（ジョブ情報等の調査用に残す）。

### 4. 画面

#### セッション履歴（一覧）

列を次に見直す：

| 列 | 内容 |
|---|---|
| 状態 | 実行中／接続中／切断 |
| 監視対象 | （すべて表示時のみ） |
| ログオン / 切断 / 接続時間 | 従来どおり |
| 端末 | MACHINE |
| **アプリ** | 1 行目 AppName（太字）、2 行目 AppDetail（小さく・省略表示）。プロセス情報の状態をアイコン/バッジで表示（取得済み＝なし、未取得・失敗＝「推定」等の灰色バッジ、ツールチップに理由） |
| 実行ユーザ | プロセスのオーナー（取れなければ OSUSER） |
| 接続ユーザ | USERNAME（スキーマが異なれば併記） |
| モジュール | MODULE |
| 種別 | DB Link の可能性・接続元特定 等のバッジ |

- 検索条件：キーワード対象に AppName・AppDetail・コマンドラインを追加。「アプリ」条件を追加（実行プログラム条件を置き換え。PROGRAM もキーワードで検索可能）。
- 詳細パネル：「接続元プロセス」節を追加（プロセス名、実行ファイル、コマンドライン、実行ユーザ、開始時刻、親プロセス、プール名、IIS サイト／パス、取得状態と理由、取得時刻）。

#### 接続数表 → 「接続アプリ」

- 集計キーを **端末 × アプリ（AppName）** に変更。列に「端末」「アプリ（AppName＋代表的な AppDetail）」「実行ユーザ（代表値）」「接続中」「最終確認」と期間別回数。
- 行クリックで、その端末・アプリで絞り込んだセッション履歴へ遷移。
- 画面名を「接続アプリ」に変更（メニューも）。

#### サマリー

- 「アプリ数」は AppName の種類数、ランキングも AppName 単位（「接続数の多いアプリ」）。

#### 設定

- 「プロセス情報の取得」節を追加：有効/無効、アカウント（`DOMAIN\user`。空なら Batch の実行アカウント）、パスワード（`********` 表示、変更しなければ保存済みの値）、タイムアウト秒、対象外端末パターン、**接続テスト**（ホスト名を入力して自分自身のプロセス一覧が取れるか確認）。
- 接続テストは Server から実行するため、Server（IIS）側からも接続先へ WinRM/DCOM で到達できる必要がある旨を表示する。

### 5. データモデル

- `TrackedSession` に追加：`AppName`（max 256、インデックス）、`AppDetail`、`ProcInfoState`（max 20、インデックス）、`ProcInfoError`、`ProcInfoAt`、`ProcName`、`ProcPath`、`ProcCommandLine`、`ProcOwner`、`ProcStartedAt`、`ProcParentName`、`IisAppPool`、`IisSite`。
- 単一行の `ProcessInspectionSettings` エンティティ：`Enabled`, `UserName`, `Password`, `DnsSuffix`, `TimeoutSeconds`, `ExcludeMachines`, `UpdatedAt`。
  - **パスワードは接続文字列と同じ扱い（ユーザ決定）**：履歴 DB に平文で保存し、API は返さない（`hasPassword` のみ返す）。画面は `********` 表示で、変更しなければ保存済みの値を使う。ログにも出さない。
- インデックス：`(TargetId, IsConnected, ProcInfoState)`、`(TargetId, Machine, AppName)`。

### 6. 権限・ドキュメント

- 接続先サーバー側の必要権限を `docs/` に追記：WinRM の場合は「Remote Management Users」＋ `root\cimv2`（`root\WebAdministration`）の Remote Enable、または Administrators。DCOM の場合は「Distributed COM Users」＋ WMI 名前空間の権限、ファイアウォール「Windows Management Instrumentation (WMI-In)」。`GetOwner` / コマンドラインは他ユーザのプロセスについては管理者権限が必要な場合がある。
- IIS のサイト/パスは「IIS 管理スクリプトおよびツール」（WMI プロバイダー）が入っている場合のみ。
- `docs/architecture.md`, `docs/screens.md`, `docs/limitations.md`, `docs/setup.md` を更新。

### 7. テスト

- `AppIdentifier`（w3wp/dotnet/java/その他/フォールバック、`IIS APPPOOL\`）、プール名抽出、ホスト名・PID 抽出、誤紐付け判定の単体テスト。
- `SessionTracker`：プロセス情報で確定した AppName を CopyObserved が上書きしないこと（`SessionTrackerTests`）。
- `ProcessInfoCollector` の処理本体（Core 側サービス）を偽の `IProcessInspector` で：成功・NotFound・Mismatch・ホスト失敗時のバックオフ・切断済み除外・セッション収集に影響しないこと。
- 表示対象フィルタ・接続数表の端末×アプリ集計（SQLite インメモリ）。
- マイグレーション（`AppDbMigrationTests`）。

## ビルダーに任せること

- DTO・API の具体的な形、Client のコンポーネント分割、スタイル。
- Demo Provider 用の疑似プロセス情報の内容。
- CIM 呼び出しの細部（オプション、例外の分類）。
- インデックスの最終形。
