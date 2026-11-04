# DB Link 利用元の特定とジョブ等の紐付け

## なぜ ORACLE.EXE なのか

別の Oracle から DB Link で接続されると、監視対象側の `gv$session` では接続元 **Oracle のサーバープロセス** がクライアントに見えます。

| 接続元 OS | PROGRAM | MACHINE |
|---|---|---|
| Windows | `ORACLE.EXE` | 接続元 DB サーバー |
| Linux / UNIX | `oracle@host (TNS V1-V3)` | 接続元 DB サーバー |

このため、実際にどのシステム（端末・プログラム）が使っているかは監視対象側だけでは分かりません。OpenDbSession はこれらを **「DB Linkの可能性」** として表示します。

なお、Windows の Oracle では全プロセスが ORACLE.EXE のスレッドとして動くため、`ORACLE.EXE (J000)` のように括弧内のプロセス名で種別を判定しています（下表）。

## 接続元の特定方法

設定画面で接続元 DB の接続文字列を登録し、DB Link を選択すると、Batch が 30 秒間隔（変更可）で接続元 DB を調べます。

| 方法 | 確度 | 仕組み |
|---|---|---|
| プロセス一致 | 高（確定） | 監視対象側の DB Link セッションの `gv$session.process` には接続元サーバープロセスの SPID（Windows は `pid:tid`）が入る。接続元 DB の `gv$process.spid` が一致するセッションが利用元 |
| SQL の @DBLINK | 中 | 接続元 DB のセッションが実行中／直前の SQL に `@DBLINK名` を含む |
| シノニム経由 | 低 | DB Link を参照するシノニム名を SQL に含む（同名の表等で誤検知の可能性） |

プロセス一致したものはセッション詳細に「特定された接続元」として表示し、一致しない場合は同じ時間帯に DB Link を使っていた接続元セッションを「候補」として表示します。

## ジョブ・内部プロセスの紐付け

| PROGRAM の括弧内 | 種別 | 紐付ける情報 | 取得元 |
|---|---|---|---|
| `Jnnn` | ジョブ | ジョブ名・所有者・内容（PL/SQL・プログラム名） | `dba_scheduler_running_jobs` + `dba_scheduler_jobs`、`dba_jobs_running` + `dba_jobs`。取れない場合も `MODULE=DBMS_SCHEDULER` なら `ACTION` をジョブ名とする |
| `Pnnn` / `PZnn` | パラレル実行 | コーディネータセッションの端末・プログラム・ユーザ | `gv$px_session` |
| `DMnn` / `DWnn` | Data Pump | Data Pump ジョブ名・所有者 | `dba_datapump_sessions` |
| `Qnnn` | AQ | 種別のみ（キュー処理のスレーブ） | - |
| その他（`Mnnn` 等） | Oracle 内部 | 種別のみ | - |

ジョブ情報は一度取得できたら保持するので、ジョブ終了後も履歴で何のジョブだったかを確認できます。
