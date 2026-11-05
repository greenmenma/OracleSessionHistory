# 接続アプリの特定 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 接続元サーバーの WMI/CIM からクライアントプロセス情報（w3wp.exe のプール名等）を取得してアプリ名を確定し、一覧・接続アプリ表で詳細を開かずにアプリが分かるようにする。Oracle 内部プロセスは既定で非表示にする。

**Architecture:** Core に純粋関数（`ClientProcessRules`・`AppIdentifier`）、取得抽象 `IProcessInspector`（CIM 実装・Demo 実装）、処理本体 `ProcessInfoService`（DB 読み書き・ホスト単位バックオフ）を置く。Batch の `ProcessInfoCollector`（BackgroundService）がセッション収集とは独立に呼び出す。Server は設定 API・一覧/集計 API を変更し、Client は一覧・接続アプリ・設定画面を見直す。

**Tech Stack:** .NET 10 / EF Core 10（SQLite・SQL Server）/ Microsoft.Management.Infrastructure 3.0.0 / React + TypeScript（Vite）/ xUnit

**Spec:** `docs/superpowers/specs/2026-11-05-app-identification-design.md`

## Global Constraints

- 取得失敗時に切断扱いにしない。プロセス情報の取得はセッション収集ループと独立し、遅延・失敗が収集に影響しない。
- 同じ Oracle セッションは 1 行。`SessionTracker.CopyObserved` は `ProcInfoState == Resolved` の行の `AppName`/`AppDetail` を上書きしない。
- パスワードは API・画面・ログに出さない（API は `hasPassword` のみ）。保存は接続文字列と同じく平文。
- 設定を変更したら `UpdatedAt` を更新する。
- エラーは `ILogger` で Warning 以上。ホスト単位の失敗は「最初の失敗」と「回復」時のみログ。
- 日時はローカル時刻（`DateTime.Now`）。CIM の `CreationDate` はローカル時刻に変換して比較。
- Windows 以外でもビルド可能。CIM 呼び出しは `OperatingSystem.IsWindows()` で守る。
- エンティティ変更時は SQLite・SQL Server 両方に同名マイグレーション（`AddProcessInfo`）。インデックス対象文字列は `HasMaxLength`。
- 依存追加時は `dotnet list package --vulnerable` を確認。
- コメント・画面文言は日本語、識別子は英語。Client は外部 UI ライブラリなし、API 型は `src/api.ts`、相対パス `api/...`。
- 誤紐付け許容：プロセス開始が `logon_time` より 5 分以上後なら `Mismatch`。
- バックオフ：1 分 → 5 分 → 15 分 → 60 分（上限）。収集間隔既定 10 秒、ホスト並列最大 4、タイムアウト既定 10 秒。
- 表示対象の種別：`Client`, `DbLinkSuspect`。

## Review Focus

1. `gv$session.process` が `1234:5678`（アンマネージド ODP.NET）・`1234`・`` 空 ``・`1234`（JDBC 既定の偽値）の場合 → 先頭の数値を使い、JDBC 偽値は PROGRAM 不一致等で `Mismatch` になり誤紐付けしない。（Task 1 のテスト）
2. MACHINE が `DOMAIN\HOST`・FQDN・空・利用者 PC パターンの場合 → `HOST` / FQDN のまま / `NotApplicable` / 除外。（Task 1）
3. 1 台のサーバーが応答しない状態が続く場合 → そのホストだけ再試行待ちになり、他ホストと次回ループは進む。ログは最初の 1 回だけ。（Task 4）
4. 取得中にセッションが切断・プロセス終了した場合 → `NotFound` で再試行しない、切断済み行は対象外。（Task 4）
5. 設定画面でパスワード欄を触らずに保存した場合 → 保存済みパスワードが維持され、GET で返らない。（Task 5）

---

### Task 1: 純粋関数（ホスト名・PID・プール名・誤紐付け・アプリ名）

**Files:**
- Create: `src/OracleSessionHistory.Core/Processes/ClientProcessRules.cs`
- Create: `src/OracleSessionHistory.Core/Processes/AppIdentifier.cs`
- Create: `src/OracleSessionHistory.Core/Processes/ClientProcessInfo.cs`（取得結果 record）
- Test: `src/OracleSessionHistory.Tests/ClientProcessRulesTests.cs`, `AppIdentifierTests.cs`

**Interfaces (Produces):**
- `record ClientProcessInfo(int ProcessId, string? Name, string? ExecutablePath, string? CommandLine, string? Owner, DateTime? StartedAt, string? ParentName, string? IisAppPool, string? IisSite)`
- `ClientProcessRules.HostOf(string? machine) -> string?`（`DOMAIN\HOST`→`HOST`、空→null）
- `ClientProcessRules.ProcessIdOf(string? clientProcess) -> int?`（`pid:tid` 先頭、数値以外 null）
- `ClientProcessRules.AppPoolOf(string? commandLine) -> string?`（`-ap "X"` / `-ap X`）
- `ClientProcessRules.IsExcluded(string host, string? patterns) -> bool`（カンマ区切り、`*` ワイルドカード、大文字小文字無視）
- `ClientProcessRules.IsMismatch(ClientProcessInfo p, string? program, DateTime logonTime) -> bool`
- `AppIdentifier.Identify(ClientProcessInfo? p, string? program, string? module, string? osUser) -> (string AppName, string? AppDetail)`（規則は spec §2 の表）

- [ ] Step 1: テストを書く（主な期待値）
  - `HostOf(@"CORP\WEB01")=="WEB01"`, `HostOf("web01.corp.local")=="web01.corp.local"`, `HostOf("")==null`
  - `ProcessIdOf("1234:5678")==1234`, `ProcessIdOf("1234")==1234`, `ProcessIdOf("abc")==null`, `ProcessIdOf(null)==null`
  - `AppPoolOf("c:\\windows\\system32\\inetsrv\\w3wp.exe -ap \"Foo Pool\" -v \"v4.0\"")=="Foo Pool"`, 引用符なし `-ap Foo`→`"Foo"`, `-ap` なし→null
  - `IsExcluded("PC-123","PC-*,KIOSK*")==true`, `IsExcluded("WEB01","PC-*")==false`
  - `IsMismatch`: 開始が logon+6 分→true、logon+4 分→false、program `w3wp.exe` と名前 `java.exe`→true、program `JDBC Thin Client`（exe 形式でない）と名前 `java.exe`→false
  - `Identify`: w3wp＋プール `Foo`＋サイト `Default Web Site/foo`→(`IIS: Foo`, `Default Web Site/foo`)；dotnet `dotnet.exe D:\a\Foo.Api.dll`→(`Foo.Api.dll`, `D:\a\Foo.Api.dll`)；java `-jar D:\b\x.jar`→(`x.jar`, `D:\b\x.jar`)；`D:\Apps\Foo\Foo.exe`→(`Foo.exe`, `D:\Apps\Foo`)；null＋program `sqlplus.exe`→(`sqlplus.exe`, null)；null＋program null→(`(不明)`, null)；null＋program `w3wp.exe`＋osUser `IIS APPPOOL\Bar`→(`IIS: Bar`, null)；module が program と異なれば AppDetail=module
- [ ] Step 2: `dotnet test --filter "FullyQualifiedName~ClientProcessRulesTests|FullyQualifiedName~AppIdentifierTests"` → コンパイルエラーで FAIL
- [ ] Step 3: 実装（正規表現は `GeneratedRegex`）
- [ ] Step 4: 同コマンドで PASS
- [ ] Step 5: コミット

### Task 2: エンティティ・設定エンティティ・追跡・マイグレーション

**Files:**
- Modify: `src/OracleSessionHistory.Core/Entities/TrackedSession.cs`（spec §5 の列、`ProcInfoStates` 定数クラス）
- Create: `src/OracleSessionHistory.Core/Entities/ProcessInspectionSettings.cs`（`Id`, `Enabled`(既定 false), `UserName`, `Password`, `DnsSuffix`, `TimeoutSeconds`(10), `ExcludeMachines`, `UpdatedAt`）
- Modify: `src/OracleSessionHistory.Core/Data/AppDbContext.cs`（`ProcessInspectionSettings` DbSet、`AppName` 256＋インデックス `(TargetId, Machine, AppName)`、`ProcInfoState` 20＋インデックス `(IsConnected, ProcInfoState)`、`IisAppPool` 256）
- Modify: `src/OracleSessionHistory.Core/Tracking/SessionTracker.cs`（`CopyObserved` で `ProcInfoState != Resolved` のとき `AppIdentifier.Identify(null, …)` で仮の AppName/AppDetail。新規行は `ProcInfoState=Pending`（Client 以外は `NotApplicable`））
- Create: マイグレーション `AddProcessInfo`（SQLite・SQL Server。Up で既存行 `AppName = COALESCE(Program,'(不明)')`、`ProcInfoState = 'NotApplicable'`）
- Test: `SessionTrackerTests` に追加

**Interfaces (Produces):** `ProcInfoStates.{Pending, Resolved, NotFound, Mismatch, Failed, NotApplicable}`；`ProcessKinds.Visible`（`IReadOnlyList<string>` = Client, DbLinkSuspect）

- [ ] Step 1: テスト：新規 Client 行は `Pending` で `AppName==Program`；`Resolved` の行は再観測で AppName が変わらない；DbLinkSuspect 新規行は `NotApplicable`
- [ ] Step 2: FAIL 確認
- [ ] Step 3: 実装・`dotnet ef migrations add AddProcessInfo`（両プロジェクト）。生成物に既存行更新 SQL を追記
- [ ] Step 4: `dotnet test` 全体 PASS（`AppDbMigrationTests`・`AppDatabaseTests` 含む）
- [ ] Step 5: コミット

### Task 3: IProcessInspector と CIM / Demo 実装

**Files:**
- Create: `src/OracleSessionHistory.Core/Processes/IProcessInspector.cs`
- Create: `src/OracleSessionHistory.Core/Processes/CimProcessInspector.cs`
- Create: `src/OracleSessionHistory.Core/Processes/DemoProcessInspector.cs`
- Modify: `src/OracleSessionHistory.Core/OracleSessionHistory.Core.csproj`（`Microsoft.Management.Infrastructure` 3.0.0）
- Modify: `src/OracleSessionHistory.Core/CoreServiceCollectionExtensions.cs`

**Interfaces (Produces):**
```csharp
public sealed record ProcessInspectorOptions(string? UserName, string? Password, string? DnsSuffix, TimeSpan Timeout);
public interface IProcessInspector
{
    // ホスト単位の失敗（接続不可・アクセス拒否）は ProcessInspectionException
    Task<IReadOnlyList<ClientProcessInfo>> InspectAsync(string host, IReadOnlyCollection<int> pids, ProcessInspectorOptions options, CancellationToken ct);
}
public sealed class ProcessInspectionException(string message, Exception? inner = null) : Exception(message, inner);
// Demo 監視対象のセッションか判定して振り分ける
public sealed class ProcessInspectorResolver(CimProcessInspector cim, DemoProcessInspector demo) { IProcessInspector For(string provider); }
```
- CIM：WSMan → 失敗時 DCOM。ホスト名は `host`、名前解決失敗（`Dns.GetHostEntryAsync`）時に `host + "." + DnsSuffix` を試す。成功プロトコルをホストごとにキャッシュ。`GetOwner` と `root\WebAdministration` の `Application WHERE ApplicationPool='X'`（`SiteName`+`Path`、1 時間キャッシュ）は失敗を無視。Windows 以外は `ProcessInspectionException("この OS ではプロセス情報を取得できません（Windows のみ）")`。
- Demo：`DemoWorld` に依存せず、PID からプロファイル（w3wp 2 プール・dotnet・exe）を決定的に返す。
- [ ] Step 1: 実装（自動テストは Demo のみ：`DemoProcessInspector` が要求 PID 分を返す）
- [ ] Step 2: `dotnet build` / `dotnet list package --vulnerable` で脆弱性なし
- [ ] Step 3: コミット

### Task 4: ProcessInfoService（処理本体）と Batch の収集ループ

**Files:**
- Create: `src/OracleSessionHistory.Core/Services/ProcessInfoService.cs`
- Create: `src/OracleSessionHistory.Core/Processes/HostBackoff.cs`（純粋：`RecordFailure(host, now) -> bool isFirst`, `RecordSuccess(host) -> bool recovered`, `IsWaiting(host, now)`, 段階 1/5/15/60 分）
- Create: `src/OracleSessionHistory.Batch/ProcessInfoCollector.cs`（10 秒ごとに `RunOnceAsync`、例外はログして継続）
- Modify: `src/OracleSessionHistory.Batch/Program.cs`
- Test: `src/OracleSessionHistory.Tests/ProcessInfoServiceTests.cs`, `HostBackoffTests.cs`

**Interfaces:**
- Consumes: Task 1〜3
- Produces: `ProcessInfoService.RunOnceAsync(CancellationToken) -> Task<ProcessInfoOutcome>`（`record ProcessInfoOutcome(int Resolved, int Failed, int Skipped)`）；`ProcessInfoService.TestAsync(string host, ProcessInspectorOptions, CancellationToken) -> Task<string>`
- 処理：設定無効なら何もしない。対象 = `IsConnected && ProcessKind==Client && (Pending || (Failed && バックオフ明け))`、最大 500 行。行ごとに host/PID/除外判定（不可→`NotApplicable`）。ホスト単位で並列 4、各ホストの結果を反映：一致→`Resolved`＋Proc*＋`AppIdentifier.Identify`；`IsMismatch`→`Mismatch`；PID なし→`NotFound`；例外→`Failed`＋`ProcInfoError`。`ProcInfoAt=now`。保存直前に再読込せず、更新は対象列のみ（`ExecuteUpdate` は使わず追跡エンティティで可）。

- [ ] Step 1: テスト（SQLite インメモリ、偽 inspector）：解決で AppName=`IIS: Foo`；PID 不在で NotFound；開始時刻後ろ倒しで Mismatch；ホスト例外で Failed かつ同ホストは次回呼ばれない（バックオフ中）・他ホストは解決；切断済み行は対象外；無効設定では inspector 未呼び出し；除外パターンで NotApplicable。`HostBackoffTests`：1→5→15→60→60 分、初回のみ isFirst、回復で recovered。
- [ ] Step 2: FAIL 確認
- [ ] Step 3: 実装
- [ ] Step 4: PASS
- [ ] Step 5: コミット

### Task 5: Server API

**Files:**
- Create: `src/OracleSessionHistory.Server/Api/ProcessSettingsEndpoints.cs`（`GET/PUT /api/process-settings`、`POST /api/process-settings/test`）
- Modify: `SessionEndpoints.cs`（一覧 DTO に `AppName, AppDetail, ProcOwner, ProcInfoState, ProcInfoError`；検索に `App`、`IncludeInternal`；キーワード対象に AppName/AppDetail/ProcCommandLine/ProcPath/IisAppPool；`Kind` 指定時は IncludeInternal を無視）
- Modify: `ReportEndpoints.cs`（サマリーと接続数表を表示対象種別のみ・AppName 単位。接続数表は `(Machine, AppName)` で集計し `AppDetail`（代表値 Max）・`Owner`（代表値 Max(ProcOwner ?? OsUser)）・`Connected`・`LastSeenAt` を追加。`/api/connection-counts` の q は Machine/AppName/AppDetail）
- Modify: `Program.cs`
- Test: `src/OracleSessionHistory.Tests/ApiQueryTests.cs`（`SessionEndpoints.Filter` の内部プロセス除外・Kind 優先、設定の Password マージ関数）

**Interfaces:**
- `ProcessSettingsDto(bool Enabled, string? UserName, bool HasPassword, string? DnsSuffix, int TimeoutSeconds, string? ExcludeMachines, DateTime? UpdatedAt)`
- `ProcessSettingsInput(bool Enabled, string? UserName, string? Password, string? DnsSuffix, int TimeoutSeconds, string? ExcludeMachines)`：`Password` が null または `********` なら保存値を維持、空文字ならクリア。TimeoutSeconds 1〜120。
- Test input: `{ host }` → `{ success, message }`（自プロセス一覧件数とプロトコル）

- [ ] Step 1: テスト：Filter 既定で JobSlave 除外・DbLinkSuspect 含む；IncludeInternal で含む；Kind=JobSlave で含む；Password マージ（null/マスク→維持、""→クリア、新値→置換）
- [ ] Step 2: FAIL → Step 3: 実装 → Step 4: PASS
- [ ] Step 5: コミット

### Task 6: Client

**Files:**
- Modify: `src/OracleSessionHistory.Client/src/api.ts`, `pages/SessionsPage.tsx`, `components/SessionDetailPanel.tsx`, `pages/CountsPage.tsx`, `pages/SummaryPage.tsx`, `pages/SettingsPage.tsx`, `App.tsx`, `format.ts`, `styles.css`
- Create: `src/components/AppCell.tsx`（AppName 太字＋AppDetail 小さく、未取得/失敗バッジとツールチップ）

内容は spec §4。メニュー名「接続数表」→「接続アプリ」。セッション一覧の「アプリ」条件はクエリ `app`、接続アプリ行クリックで `machine`+`app` を渡す。
- [ ] Step 1: 実装
- [ ] Step 2: `npm run typecheck && npm run build` 成功
- [ ] Step 3: Demo で画面確認（AppHost 起動、oraclesessionhistory-dev スキル）
- [ ] Step 4: コミット

### Task 7: ドキュメント

- Modify: `docs/architecture.md`, `docs/screens.md`, `docs/limitations.md`, `docs/setup.md`（接続先サーバーの権限・ファイアウォール・IIS WMI）, `docs/development.md`（API 一覧）, `CLAUDE.md`（Processes の位置づけ・ルール 1 行）, `.claude/skills/oracle-session-collection/SKILL.md`（必要なら）
- [ ] コミット

### Task 8: AI コードレビューと修正

- [ ] `/code-review` 相当のレビュー（code-review スキル）を実行し、指摘を検証して修正、`dotnet test` と `npm run typecheck` を再実行してコミット
