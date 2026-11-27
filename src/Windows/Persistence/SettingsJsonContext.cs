using System.Text.Json.Serialization;

namespace ShareFileWatch;

// 型情報はコンパイル時に生成し、Native AOT でも設定だけを同じ形式で読む。
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppState))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
