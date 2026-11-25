# 開発規約

- `if`・`else`・`for`・`foreach`・`while`・`do` の本体は、1 文だけでも波括弧で囲む。条件、開始括弧、本体、終了括弧を別行にする。`else if` は同じ分岐列として扱う。
- 関数内関数は原則使わず、名前のあるメソッドへ分離する。イベントの短い委譲を除き、ラムダに複雑な処理を移さない。
- `if`・`else if`・`else` の直前には、どの状況を区別し、その経路で何をするかを日本語で記す。`switch` では何を基準に処理を選ぶかを示す。条件式の逐語訳ではなく、異常時に保持する状態や、処理を省く理由を書く。

- Microsoft Learn の「C# identifier naming rules and conventions」「Common C# code conventions」を基準にする。型・メソッド・定数は PascalCase、ローカル・引数は camelCase。private インスタンスフィールドには `_` 接頭辞を使う。保存済み JSON のキーと Win32 ABI は命名整理のために変更しない。
- Doxygen の「Documenting the code」に従い、型・メソッドに `/// @brief`、フィールドには `//!<` を使う。自明でない分岐や寿命・単位・副作用は処理の近くに記述する。
- 40 行を超える関数、3 段以上のネスト、長い式は責務単位で分割を検討する。順序に意味がある UI 構築やシナリオ試験は、分割で理解しにくくならないことも確認する。
- 共通ポリシーは `AppDefaults.cs`、Windows の列挙・起動処理は専用クラスに置く。OS のエラー値・描画寸法など利用箇所に固有の定数はその近くに置く。
- `tool\build.bat` と README の両テストで確認する。Doxygen 導入済みなら `doxygen Doxyfile` を実行する（ビルド時に取得しない）。