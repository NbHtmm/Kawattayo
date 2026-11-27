using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace ShareFileWatch;

// 行コンテナの選択・並び順・テーマに合わせて、可視行だけを描き直す。
internal sealed class TableRowSurface : Border
{
    private sealed record Palette(IBrush Even, IBrush Odd, IBrush Separator, IBrush Hover, IBrush Selected, IBrush Accent);
    private static readonly Palette Light = new(
        Brush.Parse("#FFFFFF"), Brush.Parse("#EDF3F8"), Brush.Parse("#C8D3DF"),
        Brush.Parse("#E1EDF8"), Brush.Parse("#CCE4FA"), Brush.Parse("#1664AD"));
    private static readonly Palette Dark = new(
        Brush.Parse("#222831"), Brush.Parse("#293340"), Brush.Parse("#435367"),
        Brush.Parse("#344A64"), Brush.Parse("#284966"), Brush.Parse("#8CC8FF"));
    private readonly ListBox _list;
    private readonly Border _accent = new() { Width = 3, IsHitTestVisible = false };
    private ListBoxItem? _item;
    private int _index;

    internal TableRowSurface(ListBox list, Control content)
    {
        _list = list;
        var grid = new Grid { ColumnDefinitions = new("3,*") };
        Grid.SetColumn(content, 1);
        grid.Children.Add(_accent);
        grid.Children.Add(content);
        Child = grid;
        BorderThickness = new Thickness(0, 0, 0, 1);
        ActualThemeVariantChanged += (_, _) => Paint();
        Paint();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _item = this.FindAncestorOfType<ListBoxItem>();
        if (_item is not null)
        {
            _index = _list.IndexFromContainer(_item);
            _item.PropertyChanged += ItemChanged;
            _list.ContainerIndexChanged += IndexChanged;
        }
        Paint();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 仮想化で画面外に出た行をリスト側のイベントに保持しない。
        if (_item is not null)
        {
            _item.PropertyChanged -= ItemChanged;
            _list.ContainerIndexChanged -= IndexChanged;
            _item = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void ItemChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ListBoxItem.IsSelectedProperty || e.Property == IsPointerOverProperty) Paint();
    }

    private void IndexChanged(object? sender, ContainerIndexChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Container, _item)) return;
        _index = e.NewIndex;
        Paint();
    }

    private void Paint()
    {
        var palette = ActualThemeVariant == ThemeVariant.Dark ? Dark : Light;
        bool selected = _item?.IsSelected == true;
        Background = selected ? palette.Selected : _item?.IsPointerOver == true ? palette.Hover
            : _index >= 0 && (_index & 1) != 0 ? palette.Odd : palette.Even;
        BorderBrush = palette.Separator;
        _accent.Background = selected ? palette.Accent : Brushes.Transparent;
    }
}
