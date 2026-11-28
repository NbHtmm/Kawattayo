using Avalonia.Media;
using ShareFileWatch;
using Xunit;

public sealed class TableRowSurfaceTests
{
    [Theory]
    [InlineData(false, "#FFFFFF", "#EDF3F8", "#C8D3DF")]
    [InlineData(true, "#222831", "#293340", "#435367")]
    public void ResolvePaint_AlternatesRowsAndUsesTheEvenColorForUnknownIndex(bool dark, string even, string odd, string separator)
    {
        Check(TableRowSurface.ResolvePaint(-1, selected: false, hovered: false, dark), even, separator, "#00000000");
        Check(TableRowSurface.ResolvePaint(0, selected: false, hovered: false, dark), even, separator, "#00000000");
        Check(TableRowSurface.ResolvePaint(1, selected: false, hovered: false, dark), odd, separator, "#00000000");
        Check(TableRowSurface.ResolvePaint(2, selected: false, hovered: false, dark), even, separator, "#00000000");
    }

    [Theory]
    [InlineData(false, "#E1EDF8", "#CCE4FA", "#1664AD", "#C8D3DF")]
    [InlineData(true, "#344A64", "#284966", "#8CC8FF", "#435367")]
    public void ResolvePaint_UsesSelectionOverHoverAndBothOverAlternatingRows(bool dark, string hover, string selected, string accent, string separator)
    {
        foreach (int index in new[] { -1, 0, 1 })
        {
            Check(TableRowSurface.ResolvePaint(index, false, true, dark), hover, separator, "#00000000");
            Check(TableRowSurface.ResolvePaint(index, true, false, dark), selected, separator, accent);
            Check(TableRowSurface.ResolvePaint(index, true, true, dark), selected, separator, accent);
        }
    }

    private static void Check((IBrush Background, IBrush Separator, IBrush Accent) brushes,
        string background, string separator, string accent)
    {
        Assert.Equal(Color.Parse(background), Assert.IsAssignableFrom<ISolidColorBrush>(brushes.Background).Color);
        Assert.Equal(Color.Parse(separator), Assert.IsAssignableFrom<ISolidColorBrush>(brushes.Separator).Color);
        // Avalonia's built-in transparent color may preserve nonzero RGB components.
        var actualAccent = Assert.IsAssignableFrom<ISolidColorBrush>(brushes.Accent).Color;
        if (accent == "#00000000") Assert.Equal(0, actualAccent.A);
        else Assert.Equal(Color.Parse(accent), actualAccent);
    }
}
