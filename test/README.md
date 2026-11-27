# UT / SCT の実行と引き継ぎ

| コマンド | 実行対象 | 結果 |
|---|---|---|
| `test\run_ut.bat` | Core・Windows・Avalonia・CLIの判定を確認するxUnitテスト。実ファイル・SQLite・GUIは使わない | `build/UT-*/result.trx`、Coverlet の Cobertura/OpenCover、`source-map.csv`、`functions.csv`、`function-trace.csv` |
| `test\run_sct.bat` | 現在のソースから配布用Native AOT ZIPを新規作成し、展開したEXEを外から実行 | `build/SCT-*/result.json`、CLIの標準出力・標準エラー、ZIPのSHA-256 |
| `test\run_sct.bat "配布用ZIPのパス"` | 指定したZIP。再ビルドしないので、別の人も同一の配布物を試験できる | 同上 |
| `tool\test.bat integration` | 既存のAvalonia内部結合テスト。テスト専用ビルドを起動 | 従来のログ。配布EXEを検証するSCTとは別 |

Windowsから実行する。UTには.NET 10 SDKと、初回のNuGet復元またはパッケージキャッシュが必要。SCTのビルド付き実行には既存のNative AOT用C++ビルド環境も必要。GUIのSCTは対話型Windowsセッションで、起動中の「かわったよ」は共通前処理で通常終了する。試験専用の新しい展開先で初期化し、通常版のファイルは削除・初期化しない。終了を確認できなければGUIは `BLOCKED`（強制終了しない）。BATはPowerShell 7があれば優先する。Windows PowerShell 5ではAvalonia版の終了メニューを認識できない環境がある。失敗時は終了コードが非0。検証用アプリ・データを他の利用者と共有しない。

## UTを追加する

1. ソースに対応するファイルを1つ作る。例：`src/Core/Monitoring/MonitoringReducer.cs` → `test/UT/Core/Monitoring/MonitoringReducer.Tests.cs`。同名・partialの場合も**クラス名ではなくソースの相対パス**で対応させる。
2. テスト名は `関数名_入力条件_期待結果`。オーバーロードは引数型も識別できる名前にする。`[Fact]` は単独のケース、`[Theory]` は複数の入力値に同じ規則を適用するケースに使う。
3. 値の境界、異常時、変更してはいけない状態を検証する。private関数も対象にし、呼び出し元からその振る舞いを検証する。独立して試験すべきロジックがあれば抽出し、privateをリフレクションで直接呼ぶことを既定にしない。
4. `run_ut.bat` のテスト結果・カバレッジ・対応表を別々に読む。ファイル対応があっても、そのファイル内の全関数が試験済みとは限らない。

### 関数と試験結果の照合

`test/UT/function-assertions.csv` に、ソースパスと `functions.csv` の完全な関数署名、確認に使うxUnitのクラス・メソッド名、最低実行件数を書く。最低件数はTheoryの入力行を落とさないために使い、複数の試験は行を分ける。対象関数の振る舞いを実際に検証するアサーションがあるときだけ追加する。

`run_ut.bat` はTRXと照合し、全宣言について `function-trace.csv` に `mapped_passed`（対応する試験がすべて合格）、`mapped_not_verified`（不足・未実行・不合格）、`not_mapped`（対応をまだ登録していない）のいずれかを出力する。登録済みの対応が無効・未確認なら終了コードは非0。未登録を合格扱いしない。網羅率やCoverletの計測結果とは別の指標。

`function-boundaries.csv` は、OS・GUI・ファイルI/Oなど実環境に依存する関数とSCTケースの対応を記録する。対象の署名とケースIDが実在することを `run_ut.bat` が検査し、`function-trace.csv` の `sct_linked` に分けて表示する。これはUT合格でもSCT合格でもない。記載した確認範囲とケースIDを読み、SCTの最新の `result.json` で実際の合否を確認する。1件のリンクで全分岐が検証済みになるわけではない。

### 全関数を管理する次の段階

`run_ut.bat` はSDKに含まれるRoslynで実装本体のある宣言を抽出して `functions.csv` に出す。自動生成メンバー・本体のない宣言・製品ビルドでは無効な分岐は含まない。ソース行、宣言種別、署名、期待する試験ファイルを記録する。ファイルがある行は `needs_function_review` であり、関数の合格を表さない。今後は明示的な対応を全関数に増やし、外部境界でしか確認できないものは検証経路と除外理由を別途管理する。通常メソッド、コンストラクタ、ローカル関数、処理を持つアクセサーや演算子を対象とし、コンパイラ生成コードは対象外。P/Invokeなど本体を持たない宣言は署名台帳上で明示し、境界は実装を呼ぶ結合・SCTで検証する。OSやGUIに密接な処理は判定をコアへ抽出し、アダプターそのものは例外扱いを明記する。集計では「UT確認済み」「外部境界で別途確認」「未対応」を別々に数える。**全関数をUT済みと報告できるのは、未対応が0で、除外数を別掲した場合だけ。**

機械チェックでは、ソースファイルの対応、署名とテストケースIDの対応、実行済みか、実行されていない行・分岐をそれぞれ検査する。移行中は既存の不足数を固定して新規・変更関数の未対応を増やさない。最終的には未対応をゲートで拒否する。Coverletは通過位置の測定であり、期待結果を検証した証明ではない。

既存試験で確認済みの範囲と追加の優先順位は [試験範囲一覧](COVERAGE.md) を参照。

## SCTを追加する

[SCT試験仕様](SCT/SPEC.md) をケース一覧とする。Reqnrollで自動化したケースの目的・前提・操作・期待結果は `SCT/Features/*.feature` に書き、仕様を二重管理しない。ケースIDをシナリオタグに付けて `SCT/Steps/` のC#に接続する。BAT実行後は共通の `result.json` と、ケースID別の `cucumber.ndjson`、`sct.trx` を読む。手動ケースは別に実施記録を残す。 UT・SCTともテスト0件は失敗。Reqnrollケースは毎回新しい出力先に、対象IDの完了記録と全手順の合格・対応コードがそろった場合だけ合格とする。SCTのBATはドライランに切り替えない。

SCTは `--smoke` を使用せず、本番と同じEXEに対して、起動引数・終了コード・ファイル・画面など外から見える結果を検証する。`result.json` のケースIDに対応させる。比較種別、上書き拒否、CLIとGUIの保存領域共有、画面操作、実SMB、通知バナーを別シナリオとする。GUI操作を拡張するなら FlaUI/UIA3 と固定の `AutomationId` を使い、座標クリックや固定時間待ちは避ける。実SMBと通知バナーはこの初期版の対象外。


### 専用環境のSCT

`run_sct.bat` が実行するのは台帳の「自動」だけ。「要環境」は専用端末で個別実行し、自動結果とは別の `environment-result.json` に残す。ファイルの作成、更新、試験アプリの起動は原則として使い捨てのフィクスチャに限る。実SMBを試すなら、本番共有ではなく明示指定した試験専用のUNCを使う。

| 確認範囲 | 補助スクリプト | 実施に必要なもの |
|---|---|---|
| SMBの切断・書き込み競合・パスと権限 | `Run-SmbReconnect.ps1`, `Run-SmbLifecycle.ps1`, `Run-SmbPathChecks.ps1` | 専用共有と権限、切断・書き込み試験では別クライアント |
| Windows通知のクリック・安定待ち | `Run-NotificationActivation.ps1`, `Run-NotificationTiming.ps1` | 通知を利用できる対話セッションと、実バナーの操作・記録 |
| 異なる倍率、照会負荷 | `Run-DpiSession.ps1`, `Run-PerfSession.ps1` | 4Kと異なる倍率の画面、または照会中の応答を測れる専用端末 |
| エクスプローラー操作、関連付け | `Run-ExplorerDrop.ps1`, `Run-DownloadAndOpen.ps1` | 実際のドラッグ操作、または試験用ビューアーを関連付けた使い捨てユーザー |

詳しくは [専用環境の実行手順](SCT/ENVIRONMENT.md)。`PREPARED_ONLY`、`INSPECTED_ONLY`、`REHEARSAL_PASS`、`BLOCKED` は合格件数に加えない。準備や予行で通っても、その環境での動作は未確認のまま。

## ツールの選定

- 採用：[xUnit v3](https://xunit.net/docs/getting-started/v3/cmdline) と [.NET CLI/VSTest](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-vstest) でテストとTRX、[Coverlet](https://github.com/coverlet-coverage/coverlet) でカバレッジ。初期版はパッケージバージョンとロックを固定する。
- 次段階：[Roslyn](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/semantic-analysis) で関数の対応漏れを検査。[ReportGenerator](https://github.com/danielpalme/ReportGenerator) は結果をHTMLで見たい場合に追加。[FlaUI](https://github.com/FlaUI/FlaUI) は配布EXEのGUI操作を増やす段階で導入する。
- [Avalonia Headless](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform) は画面ロジックの補助テスト用。実行ファイルをWindows上で操作するSCTの代わりにはしない。

実装範囲は[試験範囲一覧](COVERAGE.md)と[SCTケース台帳](SCT/CASES.md)を参照する。SCTの台帳は実装状況、実際の合否は実行結果を示す。
