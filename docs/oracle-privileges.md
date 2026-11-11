# 監視用 Oracle ユーザの権限

監視専用のユーザを作成し、参照権限のみを付与してください。

## 監視対象 DB

```sql
CREATE USER monitor IDENTIFIED BY "********";
GRANT CREATE SESSION TO monitor;

-- 必須（取得できない場合は「取得失敗」となり収集されない）
GRANT SELECT ON sys.gv_$instance TO monitor;
GRANT SELECT ON sys.gv_$session  TO monitor;
GRANT SELECT ON sys.gv_$process  TO monitor;

-- 付加情報（無くても収集は継続。設定画面の「エラー」欄に警告が出る）
GRANT SELECT ON sys.gv_$sqlarea              TO monitor;  -- SQL テキスト
GRANT SELECT ON sys.gv_$session_connect_info TO monitor;  -- クライアントドライバ
GRANT SELECT ON sys.gv_$px_session           TO monitor;  -- パラレル実行の親
GRANT SELECT ON sys.dba_scheduler_running_jobs TO monitor; -- スケジューラジョブ
GRANT SELECT ON sys.dba_scheduler_jobs         TO monitor;
GRANT SELECT ON sys.dba_jobs_running           TO monitor; -- DBMS_JOB
GRANT SELECT ON sys.dba_jobs                   TO monitor;
GRANT SELECT ON sys.dba_datapump_sessions      TO monitor; -- Data Pump
GRANT SELECT ON sys.dba_audit_session          TO monitor; -- 監査証跡のログオン件数（接続アプリ表の端末の件数）
```

まとめて付与する場合は `GRANT SELECT_CATALOG_ROLE TO monitor;` でも構いません。

`dba_audit_session` は、接続アプリ表の端末の行のログオン件数に使います（[screens.md](screens.md)）。標準監査でログオンを監査している（`AUDIT SESSION`）場合のみ件数があります。統合監査のみ（`UNIFIED_AUDIT_TRAIL`）の環境では件数が 0 になります。参照できない場合は接続アプリ画面に理由が表示され、端末の行はこのシステムの履歴の件数になります（セッションの収集には影響しません）。

## DB Link 接続元 DB

```sql
GRANT CREATE SESSION TO monitor;
GRANT SELECT ON sys.gv_$session TO monitor;
GRANT SELECT ON sys.gv_$process TO monitor;
GRANT SELECT ON sys.gv_$sqlarea TO monitor;
GRANT SELECT ON sys.dba_db_links TO monitor;   -- 無い場合は all_db_links（自分が見えるものだけ）
GRANT SELECT ON sys.dba_synonyms TO monitor;   -- 無い場合は all_synonyms
```

- `gv$session` / `gv$process` が参照できない場合は、設定画面の DB Link 接続元の「エラー」欄に、不足しているビューと付与する `GRANT` 文が表示されます（利用元は記録されません）。
- `gv$sqlarea` が参照できない場合は「（警告）」となり、SQL テキストでの検索だけを行いません（プロセス一致での特定は続けます）。
- 監視対象 DB と同じユーザー名でも、接続元 DB は別の DB なので、接続元 DB 側でも権限の付与が必要です。

## 負荷について

- 1 回のポーリングで実行する SQL は 8 本程度で、いずれも動的パフォーマンスビューの参照です。
- SQL テキストは、セッションが現在／直前に実行している `sql_id` に絞って `gv$sqlarea` から取得します。
- セッション数が非常に多い（数千以上）環境では、取得間隔を 10〜30 秒に広げるか、「SQL テキストを取得する」をオフにしてください。
- 監視用セッション自身は結果から除外し、`MODULE=OracleSessionHistory` を設定しています。
