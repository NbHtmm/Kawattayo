# かわったよ：Avalonia版のアーキテクチャ

## 依存関係

```text
Avalonia GUI                  CLI
  └──────────┬────────────────┘
             ▼
  Kawattayo.Windows ──→ Kawattayo.Core
  Win32 / SQLite / JSON    判定・ユースケース・境界の型
```

GUIとCLIが同じC#ファイルを別々にコンパイルする方式をやめ、共有プロジェクトを参照する。CLIはAvaloniaを参照しない。Native AOT配布では共有マネージドDLLを別途利用者に配布しない。機能を追加するときは、先にどの層が責任を持つかを決める。

| 場所 | 責務 | 主な入口 |
|---|---|---|
| `src/Core/Collection` | チェックポイントの作成・比較・収集。保存・走査・コピーのインターフェースだけを参照 | `Checkpoints` |
| `src/Core/Monitoring` | 照会結果から未読・履歴・通知候補・フック候補を決める | `MonitoringReducer` |
| `src/Windows/Collection` | SMB/ローカルの読み取り専用走査、SQLiteへのチェックポイント保存 | `WindowsCheckpointScanner`、`SqliteCheckpointRepository` |
| `src/Windows/Monitoring` | 巡回、購読、登録・除外、並行処理、保存と外部フックへの振り分け | `MonitorSession` |
| `src/Windows/Persistence` | JSON設定の検証・復旧と、SQLite監視キャッシュの復元・保存 | `SettingsFile`、`MonitoringCache`（調整役は `StateStore`） |
| `avalonia` | 画面、ダイアログ、通知領域、Win32の画面呼び出し | `DesktopEntry` |
| `cli` | 引数、標準出力、終了コード | `Program` |

## 守る境界

- **監視**：OS照会はワーカーで行う。観測の反映とイベントの配信はアプリの直列コンテキストで行う。タイマーはGUI起動時に渡すため、監視ランタイムはAvaloniaの型を持たない。設定変更後の古い照会は破棄し、通信失敗を削除と混同しない。未読通知の抑止はフックへの振り分けに影響しない。
- **差分収集**：SQLiteを読み終えてから共有先を走査する。読めなかった配下を削除とみなさず、不完全な基準は保存しない。コピーは選択した追加・更新だけで、監視元を書き換えない。
- **永続化**：既存の `settings.json`、`cache.sqlite3`、チェックポイント表、フック設定の保存場所と形式を維持する。今回の構造変更でデータ移行は不要。CLIはGUIと同じフォルダに置く。GUI未起動でも使え、起動時にGUIの設定を作らない。

## 確認

必須テストの選び方と完了基準は [テスト規約](TESTING.md) を参照。

- `dotnet run --project tests/Core.Tests/Kawattayo.Core.Tests.csproj -c Release`：OSやDBを使わない監視・差分判定の回帰テスト。
- `tool\test.bat`：上記に加え、Windows上でAvaloniaのスモークテストを実行。検証データは `build\` 内に分離する。
- 配布経路を変更した場合、Native AOTのZIPと単体EXEも個別に起動する。CLaunch本体、実SMB、Windowsシェル通知は必要に応じて別途確認する。
