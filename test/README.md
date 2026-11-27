# UT / SCT の実行と引き継ぎ

| コマンド | 実行対象 | 結果 |
|---|---|---|
| `test\run_ut.bat` | コアを参照する xUnit テスト。実ファイル・SQLite・GUIは使わない | `build/UT-*/result.trx`、Coverlet の Cobertura/OpenCover、`source-map.csv` |
| `test\run_sct.bat` | 現在のソースから配布用Native AOT ZIPを新規作成し、展開したEXEを外から実行 | `build/SCT-*/result.json`、CLIの標準出力・標準エラー、ZIPのSHA-256 |
| `test\run_sct.bat "配布用ZIPのパス"` | 指定したZIP。再ビルドしないので、別の人も同一の配布物を試験できる | 同上 |
| `tool\test.bat integration` | 既存のAvalonia内部結合テスト。テスト専用ビルドを起動 | 従来のログ。配布EXEを検証するSCTとは別 |

Windowsから実行する。UTには.NET 10 SDKと、初回のNuGet復元またはパッケージキャッシュが必要。SCTのビルド付き実行には既存のNative AOT用C++ビルド環境も必要。GUIのSCTは対話型Windowsセッションで、起動中の「かわったよ」は共通前処理で通常終了する。試験専用の新しい展開先で初期化し、通常版のファイルは削除・初期化しない。終了を確認できなければGUIは `BLOCKED`（強制終了しない）。BATはPowerShell 7があれば優先する。Windows PowerShell 5ではAvalonia版の終了メニューを認識できない環境がある。失敗時は終了コードが非0。検証用アプリ・データを他の利用者と共有しない。

## UTを追加する

1. ソースに対応するファイルを1つ作る。例：`src/Core/Monitoring/MonitoringReducer.cs` → `test/UT/Core/Monitoring/MonitoringReducer.Tests.cs`。同名・partialの場合も**クラス名ではなくソースの相対パス**で対応させる。
2. テスト名は `関数名_入力条件_期待結果`。オーバーロードは引数型も識別できる名前にする。`[Fact]` は単独のケース、`[Theory]` は複数の入力値に同じ規則を適用するケースに使う。
3. 値の境界、異常時、変更してはいけない状態を検証する。private関数も対象にし、呼び出し元からその振る舞いを検証する。独立して試験すべきロジックがあれば抽出し、privateをリフレクションで直接呼ぶことを既定にしない。
4. `run_ut.bat` のテスト結果・カバレッジ・対応表を別々に読む。ファイル対応があっても、そのファイル内の全関数が試験済みとは限らない。

### 全関数を管理する次の段階

Roslynで手書きの関数を署名単位で抽出し、`UT coverage map` に「ソースパス、型・関数・引数型、ケースID、検証経路、状態、除外理由」を登録する。通常メソッド、コンストラクタ、ローカル関数、処理を持つアクセサーや演算子を対象とし、コンパイラ生成コードは対象外。P/Invokeなど本体を持たない宣言は署名台帳上で明示し、境界は実装を呼ぶ結合・SCTで検証する。OSやGUIに密接な処理は判定をコアへ抽出し、アダプターそのものは例外扱いを明記する。集計では「UT確認済み」「外部境界で別途確認」「未対応」を別々に数える。**全関数をUT済みと報告できるのは、未対応が0で、除外数を別掲した場合だけ。**

機械チェックでは、ソースファイルの対応、署名とテストケースIDの対応、実行済みか、実行されていない行・分岐をそれぞれ検査する。移行中は既存の不足数を固定して新規・変更関数の未対応を増やさない。最終的には未対応をゲートで拒否する。Coverletは通過位置の測定であり、期待結果を検証した証明ではない。

既存試験で確認済みの範囲と追加の優先順位は [試験範囲一覧](COVERAGE.md) を参照。

## SCTを追加する

[SCT試験仕様](SCT/SPEC.md) をケース一覧とする。Reqnrollで自動化したケースの目的・前提・操作・期待結果は `SCT/Features/*.feature` に書き、仕様を二重管理しない。ケースIDをシナリオタグに付けて `SCT/Steps/` のC#に接続する。BAT実行後は `result.json` と、手順結果の `cucumber.ndjson`、`sct.trx` を読む。手動ケースは別に実施記録を残す。 UT・SCTともテスト0件は失敗。Reqnrollケースは毎回新しい出力先に、対象IDの完了記録と全手順の合格・対応コードがそろった場合だけ合格とする。SCTのBATはドライランに切り替えない。

SCTは `--smoke` を使用せず、本番と同じEXEに対して、起動引数・終了コード・ファイル・画面など外から見える結果を検証する。`result.json` のケースIDに対応させる。比較種別、上書き拒否、CLIとGUIの保存領域共有、画面操作、実SMB、通知バナーを別シナリオとする。GUI操作を拡張するなら FlaUI/UIA3 と固定の `AutomationId` を使い、座標クリックや固定時間待ちは避ける。実SMBと通知バナーはこの初期版の対象外。

## ツールの選定

- 採用：[xUnit v3](https://xunit.net/docs/getting-started/v3/cmdline) と [.NET CLI/VSTest](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-vstest) でテストとTRX、[Coverlet](https://github.com/coverlet-coverage/coverlet) でカバレッジ。初期版はパッケージバージョンとロックを固定する。
- 次段階：[Roslyn](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/semantic-analysis) で関数の対応漏れを検査。[ReportGenerator](https://github.com/danielpalme/ReportGenerator) は結果をHTMLで見たい場合に追加。[FlaUI](https://github.com/FlaUI/FlaUI) は配布EXEのGUI操作を増やす段階で導入する。
- [Avalonia Headless](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform) は画面ロジックの補助テスト用。実行ファイルをWindows上で操作するSCTの代わりにはしない。

現在の範囲は既存のUT 2シナリオと、配布版のGUI起動・CLIの作成／差分／保存のSCT 2ケース。関数台帳と全関数へのテスト追加、FlaUIによる操作の自動化は未導入。
