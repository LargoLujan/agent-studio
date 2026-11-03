using System.Windows;
using System.Windows.Controls;

namespace AgentStudio.Desktop.Views.Orchestrator;

/// <summary>
/// Lays its children out in columns that fit the width it is given, never wider: all in one row when each gets its minimum
/// width; else the first one alone and the rest in a row under it; else one under another. Within a row the width is shared
/// by <see cref="WeightProperty"/>, and no column goes under its minimum: the child's <see cref="FrameworkElement.MinWidth"/>,
/// or <see cref="MinColumnWidth"/> when it has none. A row is as high as its highest child, or, with <see cref="FillHeight"/>
/// and a height to fill, the rows share that height by <see cref="RowWeightProperty"/> (the largest of the row). Collapsed
/// children take no place. So a band never needs a horizontal scroll bar and no text is squeezed into a sliver of a column.
/// </summary>
public sealed class AdaptiveColumnsPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(AdaptiveColumnsPanel), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsParentMeasure), w => w is double value && value > 0 && double.IsFinite(value));

    /// <summary>The share of the height a child's row gets with <see cref="FillHeight"/>; 1 by default.</summary>
    public static readonly DependencyProperty RowWeightProperty = DependencyProperty.RegisterAttached(
        "RowWeight", typeof(double), typeof(AdaptiveColumnsPanel), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsParentMeasure), w => w is double value && value > 0 && double.IsFinite(value));

    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(AdaptiveColumnsPanel), new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(AdaptiveColumnsPanel), new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(AdaptiveColumnsPanel), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FillHeightProperty = DependencyProperty.Register(
        nameof(FillHeight), typeof(bool), typeof(AdaptiveColumnsPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The minimum width of a column whose child sets none.</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>The rows share the height the panel is given (a region of a fixed height), instead of taking their content's.</summary>
    public bool FillHeight
    {
        get => (bool)GetValue(FillHeightProperty);
        set => SetValue(FillHeightProperty, value);
    }

    /// <summary>How many rows the children took in the last layout: 1 side by side, 2 with the first alone, or one per child.</summary>
    public int Rows { get; private set; }

    public static double GetWeight(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(WeightProperty);
    }

    public static void SetWeight(UIElement element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(WeightProperty, value);
    }

    public static double GetRowWeight(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(RowWeightProperty);
    }

    public static void SetRowWeight(UIElement element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(RowWeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Visible();
        if (children.Count == 0)
        {
            Rows = 0;
            return default;
        }

        if (double.IsInfinity(availableSize.Width))
        {
            // No width to fit into: side by side, each as wide as it wants.
            var width = 0d;
            var height = 0d;
            foreach (var child in children)
            {
                child.Measure(availableSize);
                width += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }

            Rows = 1;
            return new Size(width + (ColumnSpacing * (children.Count - 1)), height);
        }

        var rows = Arrange(children, availableSize.Width);
        Rows = rows.Count;
        var shared = SharedRowHeights(rows, availableSize.Height);
        var total = 0d;
        for (var r = 0; r < rows.Count; r++)
        {
            var rowHeight = 0d;
            foreach (var (child, columnWidth) in rows[r])
            {
                child.Measure(new Size(columnWidth, shared?[r] ?? double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            total += shared?[r] ?? rowHeight;
        }

        total += RowSpacing * (rows.Count - 1);
        return new Size(availableSize.Width, shared is not null ? Math.Max(availableSize.Height, 0) : total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Visible();
        if (children.Count == 0)
        {
            return finalSize;
        }

        var rows = Arrange(children, finalSize.Width);
        Rows = rows.Count;
        var shared = SharedRowHeights(rows, finalSize.Height);
        var y = 0d;
        for (var r = 0; r < rows.Count; r++)
        {
            var rowHeight = shared?[r] ?? rows[r].Max(c => c.Child.DesiredSize.Height);
            var x = 0d;
            foreach (var (child, columnWidth) in rows[r])
            {
                child.Arrange(new Rect(x, y, columnWidth, rowHeight));
                x += columnWidth + ColumnSpacing;
            }

            y += rowHeight + RowSpacing;
        }

        return finalSize;
    }

    /// <summary>
    /// The height of each row when the rows share a height to fill, by the largest <see cref="RowWeightProperty"/> of each;
    /// null when each takes its content's.
    /// </summary>
    private double[]? SharedRowHeights(List<List<(UIElement Child, double Width)>> rows, double height)
    {
        if (!FillHeight || !double.IsFinite(height) || rows.Count == 0)
        {
            return null;
        }

        var weights = rows.Select(row => row.Max(c => GetRowWeight(c.Child))).ToArray();
        var free = Math.Max(0, height - (RowSpacing * (rows.Count - 1)));
        var sum = weights.Sum();
        return [.. weights.Select(w => free * w / sum)];
    }

    private List<UIElement> Visible() => [.. InternalChildren.Cast<UIElement>().Where(c => c is not null && c.Visibility != Visibility.Collapsed)];

    private double MinimumOf(UIElement child) =>
        child is FrameworkElement { MinWidth: > 0 and var minimum } && double.IsFinite(minimum) ? minimum : MinColumnWidth;

    /// <summary>The rows the children go into for <paramref name="width"/>, each child with the width of its column.</summary>
    private List<List<(UIElement Child, double Width)>> Arrange(List<UIElement> children, double width)
    {
        if (Fits(children, width))
        {
            return [Row(children, width)];
        }

        if (children.Count > 2 && Fits(children.GetRange(1, children.Count - 1), width))
        {
            return [Row([children[0]], width), Row(children.GetRange(1, children.Count - 1), width)];
        }

        return [.. children.Select(child => Row([child], width))];
    }

    private bool Fits(List<UIElement> row, double width) =>
        row.Sum(MinimumOf) + (ColumnSpacing * (row.Count - 1)) <= width;

    /// <summary>
    /// The columns of one row: the width left by the spacing, shared by weight; a column that would go under its minimum gets
    /// its minimum, and the rest is shared again among the others.
    /// </summary>
    private List<(UIElement Child, double Width)> Row(List<UIElement> row, double width)
    {
        var free = Math.Max(0, width - (ColumnSpacing * (row.Count - 1)));
        var widths = new double[row.Count];
        var fixedAt = new bool[row.Count];
        for (var pass = 0; pass < row.Count; pass++)
        {
            var left = free - Enumerable.Range(0, row.Count).Where(i => fixedAt[i]).Sum(i => widths[i]);
            var weights = Enumerable.Range(0, row.Count).Where(i => !fixedAt[i]).Sum(i => GetWeight(row[i]));
            var changed = false;
            for (var i = 0; i < row.Count; i++)
            {
                if (fixedAt[i])
                {
                    continue;
                }

                widths[i] = weights > 0 ? Math.Max(0, left) * GetWeight(row[i]) / weights : 0;
                if (widths[i] < MinimumOf(row[i]) && row.Count > 1)
                {
                    widths[i] = MinimumOf(row[i]);
                    fixedAt[i] = true;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return [.. row.Select((child, i) => (child, widths[i]))];
    }
}
