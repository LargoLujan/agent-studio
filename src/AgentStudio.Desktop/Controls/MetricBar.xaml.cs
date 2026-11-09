using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentStudio.Desktop.Controls;

/// <summary>
/// The common bar of the app (docs/18 P3, tanda 2): context, quota, budget and tokens against their maximum, always with the
/// value in words, its origin and its level (<see cref="MetricBarReading"/>). Every screen that shows a percentage uses it
/// (the status bar, the monitor, the panel of the tray, «Consumo», the planning and the debate room), and the team strip of
/// Phase 13 reuses it (docs/17 D-12).
/// <list type="bullet">
/// <item>Without a value the track is drawn empty, and the words say why: never a bar at zero.</item>
/// <item>An estimate carries «~», «estimado» and a pattern on its fill: never told apart by colour alone.</item>
/// <item>UI Automation: a progress bar whose name is the whole line (what is measured, the value, its origin, its level
/// and the detail), with the RangeValue pattern (read-only) while there is a value.</item>
/// </list>
/// </summary>
public partial class MetricBar : UserControl
{
    /// <summary>The key of the brush of the pattern of an estimate, in the resources of the control.</summary>
    public const string HatchStripeKey = "MetricBar.HatchStripe";

    /// <summary>The key of the brush of the marks of the thresholds, in the resources of the control.</summary>
    public const string MarkKey = "MetricBar.Mark";

    public static readonly DependencyProperty ReadingProperty = DependencyProperty.Register(
        nameof(Reading), typeof(MetricBarReading), typeof(MetricBar), new PropertyMetadata(null, OnLookChanged));

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(MetricBar), new PropertyMetadata(false, OnLookChanged));

    public static readonly DependencyProperty TrackWidthProperty = DependencyProperty.Register(
        nameof(TrackWidth), typeof(double), typeof(MetricBar), new PropertyMetadata(120d, OnLookChanged));

    public static readonly DependencyProperty ShowsLabelProperty = DependencyProperty.Register(
        nameof(ShowsLabel), typeof(bool), typeof(MetricBar), new PropertyMetadata(true, OnLookChanged));

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth), typeof(double), typeof(MetricBar), new PropertyMetadata(double.NaN, OnLookChanged));

    public static readonly DependencyProperty ShowsToolTipProperty = DependencyProperty.Register(
        nameof(ShowsToolTip), typeof(bool), typeof(MetricBar), new PropertyMetadata(true, OnLookChanged));

    /// <summary>The tooltip now is the one the bar put there (its whole line), not one the screen gave.</summary>
    private bool _ownToolTip;

    // What the last pass drew (docs/17 D-16, F13-7): a screen with dozens of bars sets the same reading again at every grouped change, and
    // a bar that says the same draws nothing. The brushes are looked up once per load, not once per pass.
    private MetricBarReading? _drawn;
    private (bool Compact, double Track, bool Label, double LabelWidth, bool ToolTip) _drawnLook;
    private IReadOnlyList<double> _drawnMarks = [];
    private IReadOnlyList<double> _drawnSegmentEnds = [];
    private readonly Dictionary<string, Brush?> _brushes = [];

    public MetricBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // The theme may have changed while the bar was away: its brushes are looked up again, and the bar is drawn again with them.
            _brushes.Clear();
            _drawn = null;
            Update();
        };
        Update();
    }

    /// <summary>
    /// The properties a screen sets in XAML (the track, the label, the compact form) arrive one by one while it initializes: the bar is
    /// drawn once, when they are all there, and not once for each.
    /// </summary>
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Update();
    }

    private Brush? Resource(string key)
    {
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = TryFindResource(key) as Brush;
            if (brush is not null)
            {
                _brushes[key] = brush;
            }
        }

        return brush;
    }

    /// <summary>What the bar shows; without one, an empty track that says «sin dato».</summary>
    public MetricBarReading? Reading
    {
        get => (MetricBarReading?)GetValue(ReadingProperty);
        set => SetValue(ReadingProperty, value);
    }

    /// <summary>The short form of the status bar: the value with its origin in one word; the whole line goes to the tooltip.</summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    /// <summary>The width of the track, in device-independent pixels (120 by default).</summary>
    public double TrackWidth
    {
        get => (double)GetValue(TrackWidthProperty);
        set => SetValue(TrackWidthProperty, value);
    }

    /// <summary>Whether the label goes before the track (true by default); off where the screen already says what is measured.</summary>
    public bool ShowsLabel
    {
        get => (bool)GetValue(ShowsLabelProperty);
        set => SetValue(ShowsLabelProperty, value);
    }

    /// <summary>A fixed width for the label, to line up several bars; automatic by default.</summary>
    public double LabelWidth
    {
        get => (double)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    /// <summary>
    /// Whether the bar puts its whole line in its own tooltip (true by default); off where a container says more in its
    /// tooltip (the status bar, whose tooltip has the detail of every bar).
    /// </summary>
    public bool ShowsToolTip
    {
        get => (bool)GetValue(ShowsToolTipProperty);
        set => SetValue(ShowsToolTipProperty, value);
    }

    /// <summary>The reading drawn now: <see cref="Reading"/>, or the empty one when there is none.</summary>
    internal MetricBarReading Shown => Reading ?? Unknown;

    private static MetricBarReading Unknown { get; } = new() { Label = string.Empty, ValueText = "sin dato", Origin = "sin dato" };

    /// <summary>
    /// The key of the theme brush that fills a bar of <paramref name="level"/>: the colour of the level, which the words of the
    /// bar always say too. The contrast tests read the colours through these keys.
    /// </summary>
    public static string FillKey(MetricBarLevel level) => level switch
    {
        MetricBarLevel.Healthy => "SuccessBrush",
        MetricBarLevel.Warning or MetricBarLevel.Compression => "WarningBrush",
        MetricBarLevel.Critical => "ErrorBrush",
        _ => "AccentBrush",
    };

    protected override AutomationPeer OnCreateAutomationPeer() => new MetricBarAutomationPeer(this);

    private static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (MetricBar)d;
        var before = e.Property == ReadingProperty ? e.OldValue as MetricBarReading : bar.Reading;
        if (bar.IsInitialized)
        {
            bar.Update();
        }

        if (e.Property == ReadingProperty)
        {
            bar.Announce(before);
        }
    }

    private void Update()
    {
        var reading = Shown;
        var look = (IsCompact, TrackWidth, ShowsLabel, LabelWidth, ShowsToolTip);
        if (_drawn is not null && look.Equals(_drawnLook) && (ReferenceEquals(reading, _drawn) || reading.Equals(_drawn)))
        {
            return;
        }

        _drawn = reading;
        _drawnLook = look;
        LabelText.Text = reading.Label;
        LabelText.Visibility = ShowsLabel && !string.IsNullOrWhiteSpace(reading.Label) ? Visibility.Visible : Visibility.Collapsed;
        LabelColumn.Width = double.IsNaN(LabelWidth) || !ShowsLabel ? GridLength.Auto : new GridLength(LabelWidth);
        Track.Width = TrackWidth;
        Track.Height = IsCompact ? 8 : 10;

        // The short form is tighter (V38): the bars of a run fit in one line of the status bar.
        LabelText.Margin = new Thickness(0, 0, IsCompact ? 4 : 8, 0);
        ValueLine.Margin = new Thickness(IsCompact ? 4 : 8, 0, 0, 0);

        var fraction = reading.Fraction ?? 0;
        FillColumn.Width = new GridLength(fraction, GridUnitType.Star);
        RestColumn.Width = new GridLength(1 - fraction, GridUnitType.Star);
        var filled = reading.HasValue && fraction > 0;
        Fill.Fill = Resource(FillKey(reading.Level));
        Fill.Visibility = filled ? Visibility.Visible : Visibility.Collapsed;
        Hatch.Visibility = filled && reading.IsEstimate ? Visibility.Visible : Visibility.Collapsed;
        DrawSegments(reading);
        DrawMarks(reading);

        // The value stands out; after it, the rest of the line: in the short form, the origin in one word, the level written
        // when the colour says one, and the age of the figure.
        var line = IsCompact ? reading.CompactText : reading.Text;
        var value = IsCompact && !reading.HasValue ? line : reading.ValueText;
        ValueRun.Text = value;
        OriginRun.Text = line.Length > value.Length && line.StartsWith(value, StringComparison.Ordinal)
            ? line[value.Length..]
            : string.Empty;
        ValueLine.TextWrapping = IsCompact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        ValueLine.TextTrimming = IsCompact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        if (ShowsToolTip)
        {
            ToolTip = string.IsNullOrWhiteSpace(reading.Label) && string.IsNullOrWhiteSpace(reading.Detail) ? null : reading.AccessibleName;
            _ownToolTip = true;
        }
        else if (_ownToolTip)
        {
            // Only the tooltip the bar put there goes: one the screen gave stays.
            ClearValue(ToolTipProperty);
            _ownToolTip = false;
        }
    }

    /// <summary>
    /// The gaps of a bar by parts (docs/17 D-13, F13-6): two pixels of the track where each part ends inside the fill, so the
    /// parts are seen as segments; the words of the bar say each part with its value (<see cref="MetricBarReading.PartsText"/>).
    /// </summary>
    private void DrawSegments(MetricBarReading reading)
    {
        var ends = reading.SegmentEnds;
        if (ends.SequenceEqual(_drawnSegmentEnds) && (ends.Count > 0 || SegmentsLayer.Children.Count == 0))
        {
            return;
        }

        _drawnSegmentEnds = ends;
        SegmentsLayer.Children.Clear();
        var brush = Track.Background;
        foreach (var end in ends)
        {
            var holder = new Grid { IsHitTestVisible = false };
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(end, GridUnitType.Star) });
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - end, GridUnitType.Star) });
            var gap = new Rectangle { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Fill = brush };
            Grid.SetColumn(gap, 1);
            holder.Children.Add(gap);
            SegmentsLayer.Children.Add(holder);
        }
    }

    /// <summary>A mark of one pixel at each threshold: decoration over the words, which say the level.</summary>
    private void DrawMarks(MetricBarReading reading)
    {
        var marks = reading.Marks.Where(m => m > 0 && m < 1).ToList();
        if (marks.SequenceEqual(_drawnMarks) && (marks.Count > 0 || MarksLayer.Children.Count == 0))
        {
            return;
        }

        _drawnMarks = marks;
        MarksLayer.Children.Clear();
        var brush = Resource(MarkKey);
        foreach (var mark in marks)
        {
            var holder = new Grid { IsHitTestVisible = false };
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(mark, GridUnitType.Star) });
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - mark, GridUnitType.Star) });
            var line = new Rectangle { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, Fill = brush };
            Grid.SetColumn(line, 1);
            holder.Children.Add(line);
            MarksLayer.Children.Add(holder);
        }
    }

    /// <summary>The name the screen gave the bar; null when none.</summary>
    private string? GivenName => AutomationProperties.GetName(this) is { Length: > 0 } given ? given : null;

    /// <summary>
    /// What UI Automation names the bar with (V37): the name the screen gave, else the whole line of its reading; without a
    /// reading, «sin dato» while it is shown and nothing while it is hidden (never «: sin dato»).
    /// </summary>
    private string AccessibleName => GivenName ?? Reading?.AccessibleName ?? (Visibility == Visibility.Visible ? NoReadingName : string.Empty);

    /// <summary>What a bar shown without a reading is called: what its track says.</summary>
    private const string NoReadingName = "sin dato";

    /// <summary>Tells UI Automation that the value and the name changed, when anything listens.</summary>
    private void Announce(MetricBarReading? before)
    {
        if (UIElementAutomationPeer.FromElement(this) is not MetricBarAutomationPeer peer)
        {
            return;
        }

        // The names as the peer gives them (V37): a name the screen gave, else the line of the reading, and none without one,
        // never the «: sin dato» of the empty reading drawn.
        var after = Shown;
        var nameBefore = GivenName ?? before?.AccessibleName ?? string.Empty;
        var nameAfter = AccessibleName;
        if (!string.Equals(nameBefore, nameAfter, StringComparison.Ordinal))
        {
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, nameBefore, nameAfter);
        }

        if (before?.Value != after.Value && after.Value is { } value)
        {
            peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, before?.Value ?? 0d, value);
        }
    }

    /// <summary>
    /// The bar for UI Automation: a progress bar named with its whole line, without the inner texts as children (the name
    /// already says them), and with the RangeValue pattern, read-only, only while there is a value: a bar without one is
    /// never read as zero.
    /// <para>
    /// V37: a bar the screen hid or collapsed (a task without an agent, a quota window without a bar, a planning not started)
    /// is not a control nor content while it is not shown. A bar shown without a reading is still read, as «sin dato» (or with
    /// the name the screen gave it): never «: sin dato».
    /// </para>
    /// </summary>
    private sealed class MetricBarAutomationPeer(MetricBar owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        /// <summary>The bar is drawn: not hidden nor collapsed by the screen.</summary>
        private bool IsShown => owner.Visibility == Visibility.Visible;

        public double Value => owner.Shown.Value ?? 0;

        public bool IsReadOnly => true;

        public double Maximum => owner.Shown.Maximum;

        public double Minimum => 0;

        public double LargeChange => 0;

        public double SmallChange => 0;

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue && owner.Shown.HasValue ? this : base.GetPattern(patternInterface);

        public void SetValue(double value) => throw new ElementNotEnabledException("A metric bar only shows a value: it cannot be set.");

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;

        protected override string GetClassNameCore() => nameof(MetricBar);

        protected override string GetNameCore() => owner.AccessibleName;

        protected override string GetItemStatusCore() => owner.Reading?.Origin ?? string.Empty;

        protected override string GetHelpTextCore() => owner.Reading?.Detail ?? string.Empty;

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override bool IsContentElementCore() => IsShown;

        protected override bool IsControlElementCore() => IsShown;
    }
}
