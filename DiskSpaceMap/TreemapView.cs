using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiskSpaceMap.Core;

namespace DiskSpaceMap;

/// <summary>Retained drawings for completed maps; bounded, animated snapshots while scanning.</summary>
public sealed class TreemapView : FrameworkElement
{
    private sealed record Tile(DiskNode? Node, DiskNode Container, Rect Bounds, string Label, Brush Fill);
    private static readonly string[] Palette = ["#287D79", "#345E99", "#795BA1", "#967039", "#3F7893", "#91616E", "#467A5E", "#65699D"];
    private readonly List<Tile> _tiles = [];
    private sealed record MovingTile(Tile Target, double From, double To);
    private readonly List<MovingTile> _moving = [];
    private readonly Dictionary<string, double> _frameWeights = [];
    private readonly ProgressiveMap _mapState = new();
    private DiskNode? _stableRoot;
    private IReadOnlyList<MovingTile> _settled = [];
    private readonly Stopwatch _transition = new();
    private double _lastFrame;
    private bool _live, _rendering, _finishing;
    private DrawingGroup? _drawing;
    private DiskNode? _root, _selected, _hover, _contextTarget;
    private readonly ToolTip _hoverCard = new()
    {
        Placement = PlacementMode.Relative,
        StaysOpen = true,
        IsHitTestVisible = false,
        MaxWidth = 480
    };
    private string? _hoverText;
    private Point _hoverPosition;
    public event Action<DiskNode?>? SelectionChanged;
    public event Action<DiskNode>? FolderInvoked;
    public event Action<DiskNode>? LocationInvoked;
    public DiskNode? Root
    {
        get => _root;
        set
        {
            HideHover();
            if (_live && value != null && value.FullPath == _root?.FullPath)
            {
                BeginTransition(value, complete: true);
                return;
            }
            StopAnimation();
            _live = _finishing = false;
            if (_stableRoot != null)
            {
                bool sharesStableRoot = false;
                for (var ancestor = value; ancestor != null; ancestor = ancestor.Parent)
                    if (ReferenceEquals(ancestor, _stableRoot)) { sharesStableRoot = true; break; }
                if (sharesStableRoot) RebindSettledRoot();
                else ResetSettledRoot();
            }
            _root = value;
            if (value == null) ResetSettledRoot();
            _selected = _hover = _contextTarget = null;
            Rebuild();
        }
    }
    public DiskNode? Selected { get => _selected; set { _selected = value; InvalidateVisual(); } }

    public TreemapView()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
        _hoverCard.PlacementTarget = this;
        SizeChanged += (_, _) => Rebuild();
        Unloaded += (_, _) => { HideHover(); if (_finishing) FinishTransition(); else StopAnimation(); };
        ContextMenu = new System.Windows.Controls.ContextMenu();
        var open = new System.Windows.Controls.MenuItem { Header = "Open in Explorer" };
        open.Click += (_, _) => { if (_contextTarget != null) LocationInvoked?.Invoke(_contextTarget); };
        var drill = new System.Windows.Controls.MenuItem { Header = "Drill into folder" };
        drill.Click += (_, _) => InvokeFolder(_contextTarget);
        ContextMenu.Items.Add(open);
        ContextMenu.Items.Add(drill);
        ContextMenuOpening += (_, e) =>
        {
            if (e.CursorLeft == -1) _contextTarget = _selected ?? _root; // Keyboard menu.
            _contextTarget ??= _selected ?? _root;
            open.IsEnabled = _contextTarget != null;
            drill.IsEnabled = CanDrill(_contextTarget) && _contextTarget != _root;
        };
    }

    public static Brush ColorFor(DiskNode node)
    {
        uint hash = 2166136261;
        foreach (char c in node.Name) hash = (hash ^ char.ToUpperInvariant(c)) * 16777619;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[hash % Palette.Length]));
        brush.Freeze();
        return brush;
    }

    private bool CanDrill(DiskNode? node) => !_live && node is { IsDirectory: true, IsDirectoryLink: false };

    private bool InvokeFolder(DiskNode? node)
    {
        if (!CanDrill(node) || node == _root) return false;
        FolderInvoked?.Invoke(node!);
        return true;
    }

    public void ShowLive(DiskNode snapshot) => BeginTransition(snapshot, complete: false);

    private void ResetSettledRoot()
    {
        _stableRoot = null;
        _settled = [];
        _frameWeights.Clear();
        _mapState.Reset();
    }

    private void RebindSettledRoot()
    {
        // Keep the settled partition order, but refresh both weights and node
        // references after any branch of this tree has been replaced.
        var entries = _mapState.Update(_stableRoot!, complete: true);
        _settled = entries.Select(entry => new MovingTile(
            new Tile(entry.Node, _stableRoot!, Rect.Empty, entry.Node.Name, ColorFor(entry.Node)), entry.Weight, entry.Weight)).ToArray();
        _frameWeights.Clear();
        foreach (var entry in entries) _frameWeights[entry.Key] = entry.Weight;
    }

    private static string KeyFor(Tile tile) => tile.Node == null ? "\0remainder"
        : (tile.Node.IsAggregate ? "\0aggregate" : "") + tile.Node.Name;

    private void BeginTransition(DiskNode snapshot, bool complete, bool animate = true)
    {
        _root = snapshot;
        _live = !complete;
        _finishing = complete;
        _selected = snapshot.Children.FirstOrDefault(n => n.Name == _selected?.Name);
        _hover = null;
        _moving.Clear();
        var entries = _mapState.Update(snapshot, complete);
        bool first = _frameWeights.Count == 0;
        foreach (var entry in entries)
        {
            var tile = new Tile(entry.Node, snapshot, Rect.Empty, entry.Node.Name, ColorFor(entry.Node));
            _moving.Add(new(tile, first ? entry.Weight : _frameWeights.GetValueOrDefault(entry.Key), entry.Weight));
        }
        if (complete)
        {
            // Persist before starting the last tween, including zero-weight entries.
            // Layout/resize events during the tween must also use this exact topology.
            _stableRoot = snapshot;
            _settled = _moving.ToArray();
        }
        bool motion = animate && IsVisible && SystemParameters.ClientAreaAnimation && _tiles.Count > 0;
        _transition.Restart();
        _lastFrame = 0;
        DrawTransition(motion ? 0 : 1);
        if (motion && !_rendering)
        {
            CompositionTarget.Rendering += AnimateFrame;
            _rendering = true;
        }
        else if (!motion) FinishTransition();
    }

    private void AnimateFrame(object? sender, EventArgs e)
    {
        double elapsed = _transition.Elapsed.TotalMilliseconds;
        if (elapsed - _lastFrame < 1000.0 / 30 && elapsed < 180) return;
        _lastFrame = elapsed;
        double fraction = Math.Clamp(elapsed / 180, 0, 1);
        DrawTransition(fraction);
        if (fraction >= 1 || !IsVisible) FinishTransition();
    }

    private void DrawTransition(double fraction)
    {
        double eased = fraction * fraction * (3 - 2 * fraction);
        var weights = new double[_moving.Count];
        _frameWeights.Clear();
        for (int i = 0; i < _moving.Count; i++)
        {
            var item = _moving[i];
            weights[i] = fraction >= 1 ? item.To : Math.Max(0, item.From + (item.To - item.From) * eased);
            _frameWeights[KeyFor(item.Target)] = weights[i];
        }
        var layout = TreemapLayout.LayoutOrdered(weights, new(0, 0, ActualWidth, ActualHeight));
        _tiles.Clear();
        _drawing = new DrawingGroup();
        using (var context = _drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 25, 35)), null, new Rect(RenderSize));
            foreach (var part in layout)
            {
                var item = _moving[part.Index];
                var b = part.Bounds;
                var rect = new Rect(b.X + 1.5, b.Y + 1.5, Math.Max(0, b.Width - 3), Math.Max(0, b.Height - 3));
                if (rect.Width < 0.5 || rect.Height < 0.5) continue;
                var tile = item.Target with { Bounds = rect };
                _tiles.Add(tile);
                context.DrawRoundedRectangle(tile.Fill, null, rect, 3, 3);
                if (rect.Width < 64 || rect.Height < 34) continue;
                DrawText(context, tile.Node is {} named ? ShellDisplayNames.Get(named) : tile.Label, rect.X + 10, rect.Y + 8, rect.Width - 20, 12, FontWeights.SemiBold);
                if (rect.Height >= 62)
                {
                    var node = tile.Node;
                    string detail = node == null ? "Smaller items" : node.IsDirectoryLink ? "Link · skipped"
                        : node.ScanError != null ? "Some folders unreadable"
                        : _live && !node.WasEnumerated ? node.Size > 0 ? $"{SizeFormatter.Format(node.Size)} · scanning" : "Waiting…"
                        : SizeFormatter.Format(node.Size);
                    DrawText(context, detail, rect.X + 10, rect.Y + 29, rect.Width - 20, 11, FontWeights.Normal);
                }
                if (rect.Height >= 94 && rect.Width >= 110 && tile.Node != null)
                    DrawText(context, $"{tile.Node.FileCount:N0} files", rect.X + 10, rect.Bottom - 25, rect.Width - 20, 10, FontWeights.Normal, 0.7);
            }
        }
        _drawing.Freeze();
        InvalidateVisual();
        if (_hoverCard.IsOpen) HoverAt(_hoverPosition);
    }

    private void StopAnimation()
    {
        if (_rendering) CompositionTarget.Rendering -= AnimateFrame;
        _rendering = false;
        _moving.Clear();
    }

    private void FinishTransition()
    {
        bool complete = _finishing;
        StopAnimation();
        _finishing = false;
        if (complete) Rebuild();
    }

    private void Rebuild()
    {
        if (_finishing && _moving.Count > 0)
        {
            DrawTransition(Math.Clamp(_transition.Elapsed.TotalMilliseconds / 180, 0, 1));
            return;
        }
        if (_live && _root != null) { BeginTransition(_root, complete: false, animate: false); return; }
        StopAnimation();
        _tiles.Clear();
        _drawing = new DrawingGroup();
        using (var context = _drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 25, 35)), null, new Rect(RenderSize));
            if (_root?.Size > 0 && ActualWidth > 1 && ActualHeight > 1)
                DrawChildren(context, _root, new Rect(0, 0, ActualWidth, ActualHeight));
        }
        _drawing.Freeze();
        InvalidateVisual();
    }

    private void DrawChildren(DrawingContext context, DiskNode folder, Rect bounds)
    {
        if (bounds.Width < 3 || bounds.Height < 3) return;
        // Only this folder's immediate children are laid out. Its descendants are
        // not rendered until a click makes that child the new outer map scope.
        bool stable = folder == _stableRoot;
        var area = new MapRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var layout = stable ? TreemapLayout.LayoutOrdered(_settled.Select(item => item.To).ToArray(), area)
            : TreemapLayout.Layout(new NodeWeights(folder.Children), area, 1200);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 25, 35)), null, bounds);
        foreach (var item in layout)
        {
            var raw = item.Bounds;
            var rect = new Rect(raw.X + 1.5, raw.Y + 1.5, Math.Max(0, raw.Width - 3), Math.Max(0, raw.Height - 3));
            if (rect.Width < 0.5 || rect.Height < 0.5) continue;
            DiskNode? node = stable ? _settled[item.Index].Target.Node : item.IsRemainder ? null : folder.Children[item.Index];
            Brush fill = node is null ? Brushes.SlateGray : ColorFor(node);
            string label = node is null ? "Smaller items" : ShellDisplayNames.Get(node);
            _tiles.Add(new(node, folder, rect, label, fill));
            context.DrawRoundedRectangle(fill, null, rect, 3, 3);
            if (rect.Width >= 64 && rect.Height >= 34)
            {
                DrawText(context, label, rect.X + 10, rect.Y + 8, rect.Width - 20, 12, FontWeights.SemiBold);
                if (rect.Height >= 62)
                    DrawText(context, node is null ? "Drill in to see all items" : SizeFormatter.Format(node.Size), rect.X + 10, rect.Y + 29, rect.Width - 20, 11, FontWeights.Normal);
                if (rect.Height >= 94 && rect.Width >= 110 && node?.IsDirectory == true)
                    DrawText(context, $"{node.FileCount:N0} files", rect.X + 10, rect.Bottom - 25, rect.Width - 20, 10, FontWeights.Normal, 0.7);
            }
        }
    }

    internal void RefreshDisplayNames()
    {
        bool hovered = _hoverCard.IsOpen;
        Rebuild();
        if (hovered) { _hoverText = null; HoverAt(_hoverPosition); }
    }

    internal IReadOnlyList<(DiskNode? Node, DiskNode Container, Rect Bounds)> VisibleTiles
        => _tiles.Select(tile => (tile.Node, tile.Container, tile.Bounds)).ToArray();

    private void DrawText(DrawingContext context, string text, double x, double y, double width, double size, FontWeight weight, double opacity = 1)
    {
        if (width < 1) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        context.PushOpacity(opacity);
        context.DrawText(formatted, new Point(x, y));
        context.Pop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_drawing != null) dc.DrawDrawing(_drawing);
        foreach (var tile in _tiles)
        {
            if (tile.Node != null && (tile.Node == _selected || tile.Node == _hover))
            {
                var rect = tile.Bounds;
                rect.Inflate(-1, -1);
                if (rect.Width > 0 && rect.Height > 0)
                    dc.DrawRoundedRectangle(null, new Pen(tile.Node == _selected ? Brushes.White : Brushes.LightGray, tile.Node == _selected ? 2 : 1), rect, 3, 3);
            }
        }
    }

    private Tile? Hit(Point point)
    {
        for (int i = _tiles.Count - 1; i >= 0; i--) if (_tiles[i].Bounds.Contains(point)) return _tiles[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        HoverAt(e.GetPosition(this));
    }

    internal string? HoverAt(Point point)
    {
        _hoverPosition = point;
        var tile = Hit(point);
        if (tile == null) { HideHover(); return null; }
        var node = tile?.Node;
        if (node == _hover && _hoverText != null)
        {
            if (IsVisible && PresentationSource.FromVisual(this) != null) _hoverCard.IsOpen = true;
            return _hoverText;
        }
        _hover = node;
        Cursor = CanDrill(node) ? Cursors.Hand : Cursors.Arrow;
        string name = node is null ? "Smaller items" : ShellDisplayNames.Get(node);
        string path = node?.FullPath ?? tile!.Container.FullPath;
        string size = node == null ? "Items grouped to keep the map responsive."
            : $"{SizeFormatter.Format(node.Size)} ({node.Size:N0} bytes) · {node.FileCount:N0} {(node.FileCount == 1 ? "file" : "files")}";
        string action = node is null || node.IsAggregate ? "Use the item list to inspect the individual entries."
            : node.IsDirectoryLink ? "Directory link skipped. Right-click to open its location."
            : _live ? "Scanning… Right-click to open the location in Explorer."
            : node.IsDirectory ? "Click to open this folder. Right-click for Explorer."
            : "Right-click to show this file in Explorer.";
        _hoverText = $"{name}\n{path}\n{size}\n{action}";
        var content = new StackPanel { MaxWidth = 440 };
        content.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = path, Foreground = Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        content.Children.Add(new TextBlock { Text = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        content.Children.Add(new TextBlock { Text = action, Foreground = new SolidColorBrush(Color.FromRgb(114, 226, 191)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        _hoverCard.Content = content;
        _hoverCard.HorizontalOffset = Math.Min(point.X + 16, Math.Max(0, ActualWidth - 320));
        _hoverCard.VerticalOffset = Math.Min(point.Y + 20, Math.Max(0, ActualHeight - 160));
        // Open on a tile change, rather than relying on mouse-enter events for the
        // entire map control. Even a 1px tile can show its complete name and path.
        if (IsVisible && PresentationSource.FromVisual(this) != null) _hoverCard.IsOpen = true;
        InvalidateVisual();
        return _hoverText;
    }

    private void HideHover()
    {
        _hoverCard.IsOpen = false;
        _hoverText = null;
        _hover = null;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { HideHover(); base.OnMouseLeave(e); }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        var point = e.GetPosition(this);
        Focus();
        bool opened = ActivateAt(point, e.ChangedButton);
        if (opened) e.Handled = true;
    }

    // One atomic input action: determine the clicked node, select it, then open it.
    // Selection/list/layout events cannot invalidate a pending mouse-up target.
    internal bool ActivateAt(Point point, MouseButton button)
    {
        HideHover();
        var tile = Hit(point);
        var clicked = tile?.Node;
        _selected = clicked;
        _contextTarget = button == MouseButton.Right ? tile?.Node ?? tile?.Container ?? _root : null;
        SelectionChanged?.Invoke(_selected);
        InvalidateVisual();
        return button == MouseButton.Left && InvokeFolder(clicked);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _selected != null)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) LocationInvoked?.Invoke(_selected);
            else InvokeFolder(_selected);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private sealed class NodeWeights(IReadOnlyList<DiskNode> nodes) : IReadOnlyList<long>
    {
        public long this[int index] => nodes[index].Size;
        public int Count => nodes.Count;
        public IEnumerator<long> GetEnumerator() { foreach (var node in nodes) yield return node.Size; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
