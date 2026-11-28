# 差分収集をコマンドで使う

GUIとCLIは同じフォルダに置きます。チェックポイントは実行ファイル横の同じ `cache.sqlite3` を使います。既存の監視登録や未読は変更しません。

```powershell
.\Kawattayo-cli.exe checkpoint create --name "試験前" --root "\\server\share\logs" --exclude '~$*'
.\Kawattayo-cli.exe checkpoint list
.\Kawattayo-cli.exe diff --checkpoint "試験前" --json
.\Kawattayo-cli.exe collect --checkpoint "試験前" --output "C:\試験結果\試験001"
```

同名のチェックポイントがある場合は一覧に出るIDを指定します。collectは実行時に比較し直して追加・更新分をすべて対象にします。既存ファイルは上書きしません。終了コード: 0 成功、2 引数不正、3 確認不能または保存失敗あり、4 処理失敗、130 Ctrl+C。
