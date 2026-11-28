namespace ShareFileWatch;

internal static class DesktopIdentity
{
    // バージョンは共有ライブラリではなく、実際に動いているGUIのアセンブリから取得。
    internal static string WindowTitle => $"{AppDefaults.AppName} v{typeof(DesktopIdentity).Assembly.GetName().Version!.ToString(3)}";
}
