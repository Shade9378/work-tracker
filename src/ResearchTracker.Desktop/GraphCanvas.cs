using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Styling;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Nodify.Avalonia;
using Nodify.Avalonia.Connections;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

public enum GraphInteractionMode { Select, Move }

/// <summary>Nodify owns the viewport, item layout and connection rendering; domain services own graph edits.</summary>
public sealed class GraphCanvas : NodifyEditor
{
    protected override Type StyleKeyOverride => typeof(NodifyEditor);
    private MainViewModel? vm;
    private GraphNode? connecting;
    private GraphNode? dragging;
    private GraphEdge? draggingLabel;
    private IPointer? nodePointer;
    private readonly Dictionary<GraphNode, Point> dragNodes = [];
    private readonly Dictionary<GraphEdge, (GraphPoint Source, GraphPoint Target)> dragEdges = [];
    private readonly Dictionary<GraphEdge, Vector> dragLabelOffsets = [];
    private readonly Dictionary<GraphEdge, Point> dragLabelAnchors = [];
    private Vector dragDelta;
    private Point? marqueeStart;
    private Rect? selectionRectangle;
    private HashSet<Guid> initialSelection = [];
    private IPointer? marqueePointer;
    private GraphInteractionMode mode = GraphInteractionMode.Move;
    public GraphInteractionMode Mode { get => mode; set { if (mode == value) return; CancelInteraction(); mode = value; ModeChanged?.Invoke(); } }
    public event Action? ModeChanged;
    public Rect? SelectionRectangle => selectionRectangle;
    private bool panning;
    private EndpointEdit? endpointEdit;
    private IPointer? endpointPointer;
    private EdgeEnd? focusedEnd;
    private Guid? selectedEdge;
    public event Action? AdornerChanged;
    public event Action? DeleteRequested;
    public event Action? RenameRequested;
    public event Action? HelpRequested;
    public EdgeEnd? FocusedEnd => focusedEnd;
    public bool EditingEndpoint => endpointEdit != null;
    private Point lastPointer;
    private readonly ObservableCollection<NodeItem> nodes = [];
    private readonly ObservableCollection<EdgeItem> edges = [];
    private readonly Dictionary<Guid, Border> cards = [];
    private readonly Dictionary<Guid, Border> headings = [];
    private readonly Dictionary<Guid, Connection> connectionControls = [];
    public bool Connecting { get; set; }
    public MainViewModel? ViewModel
    {
        get => vm;
        set
        {
            if (vm != null) { vm.Refreshed -= Synchronize; vm.SelectionChanged -= UpdateAppearance; }
            vm = value;
            if (vm != null) { vm.Refreshed += Synchronize; vm.SelectionChanged += UpdateAppearance; }
            Synchronize();
        }
    }
    public GraphCanvas()
    {
        ClipToBounds = true; Focusable = true; MinViewportZoom = .2; MaxViewportZoom = 2.5;
        Background = DesignTokens.Brush("Canvas"); ItemsSource = nodes; Connections = edges;
        ItemTemplate = new FuncDataTemplate<NodeItem>((item, _) => BuildNode(item!));
        ConnectionTemplate = new FuncDataTemplate<EdgeItem>((item, _) => BuildConnection(item!));
        var style = new Style(s => s.OfType<ItemContainer>());
        style.Setters.Add(new Setter(ItemContainer.LocationProperty, new Binding(nameof(NodeItem.Location)) { Mode = BindingMode.TwoWay }));
        Styles.Add(style);
        ViewportUpdated += (_, _) => AdornerChanged?.Invoke();
        AddHandler(PointerPressedEvent, OnGraphPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnGraphWheel, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => vm?.Save(), RoutingStrategies.Bubble, true);
    }
    private void OnGraphWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var pointer = e.GetPosition(this); var anchor = World(pointer);
            ViewportZoom = Math.Clamp(ViewportZoom * Math.Pow(1.15, e.Delta.Y), MinViewportZoom, MaxViewportZoom);
            ViewportLocation = new Point(anchor.X - pointer.X / ViewportZoom, anchor.Y - pointer.Y / ViewportZoom);
        }
        else ViewportLocation -= new Vector(e.Delta.X * 48 / ViewportZoom, e.Delta.Y * 48 / ViewportZoom);
        e.Handled = true;
    }
    private void Synchronize()
    {
        if (endpointEdit != null) { UpdateAppearance(); return; }
        var current = vm?.Nodes.ToList() ?? [];
        foreach (var item in nodes.Where(i => !current.Contains(i.Node)).ToList()) { item.Dispose(); nodes.Remove(item); cards.Remove(item.Node.Id); headings.Remove(item.Node.Id); }
        foreach (var node in current.Where(n => nodes.All(i => i.Node != n))) nodes.Add(new NodeItem(node, UpdateAppearance));
        foreach (var edge in edges) edge.Dispose(); edges.Clear(); connectionControls.Clear();
        if (vm != null) foreach (var edge in vm.Edges) edges.Add(new EdgeItem(edge, vm.Data, () => AdornerChanged?.Invoke()));
        UpdateAppearance();
    }
    public void RefreshTheme()
    {
        foreach (var item in nodes) item.Dispose(); nodes.Clear(); cards.Clear(); headings.Clear(); Synchronize();
    }
    private void UpdateAppearance()
    {
        var id = (vm?.Selected as GraphEdge)?.Id;
        if (selectedEdge != id) { selectedEdge = id; focusedEnd = null; if (endpointEdit != null) { CancelEndpoint(); return; } }
        Background = DesignTokens.Brush("Canvas");
        foreach (var item in nodes)
            if (cards.TryGetValue(item.Node.Id, out var card))
            {
                card.BorderBrush = DesignTokens.Brush(vm?.IsGraphSelected(item.Node) == true || item.Node == connecting || item.Node == endpointEdit?.Snap ? "Accent" : "Border");
                card.BorderThickness = new Thickness(vm?.IsGraphSelected(item.Node) == true ? 2 : 1);
                card.Background = DesignTokens.Brush("Surface"); card.Opacity = vm?.Matches(item.Node) != false ? 1 : .3;
                if (headings.TryGetValue(item.Node.Id, out var heading))
                {
                    heading.Background = ProjectColors.HeadingBrush(vm?.Data.Projects.FirstOrDefault(p => p.Id == item.Node.ProjectId));
                    foreach (var label in heading.GetVisualDescendants().OfType<TextBlock>()) label.Foreground = ProjectColors.HeadingText;
                }
            }
        foreach (var item in edges)
            if (connectionControls.TryGetValue(item.Edge.Id, out var connection))
            {
                var project = vm?.Data.Projects.FirstOrDefault(p => p.Id == item.Edge.ProjectId); connection.Stroke = vm?.IsGraphSelected(item.Edge) == true ? DesignTokens.Brush("Accent") : ProjectColors.Marker(project);
                connection.StrokeThickness = vm?.IsGraphSelected(item.Edge) == true ? 3 : 1.25;
                connection.TextBackground = ProjectColors.HeadingBrush(project); connection.Foreground = ProjectColors.HeadingText; connection.InvalidateVisual();
            }
        AdornerChanged?.Invoke();
    }
    private Control BuildNode(NodeItem item)
    {
        var color = ProjectColors.Heading(vm?.Data.Projects.FirstOrDefault(p => p.Id == item.Node.ProjectId)); var ink = ProjectColors.HeadingText;
        var stack = new StackPanel { Spacing = 2 };
        var title = new TextBlock { Name = "NodeTitle" + item.Node.Id.ToString("N"), FontSize = DesignTokens.Body, Foreground = ink, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, new Binding("Node.Title") { Source = item }); stack.Children.Add(title);
        var header = new Border { Name = "NodeHeading" + item.Node.Id.ToString("N"), Child = stack, Background = new SolidColorBrush(color), Padding = new Thickness(12, 7), CornerRadius = new CornerRadius(3, 3, 0, 0) }; headings[item.Node.Id] = header;
        var summary = new TextBlock { Name = "NodeSummary" + item.Node.Id.ToString("N"), FontSize = DesignTokens.Caption, Foreground = DesignTokens.Brush("TextMuted"), TextWrapping = TextWrapping.Wrap, MaxHeight = 32, Margin = new Thickness(12, 7) };
        summary.Bind(TextBlock.TextProperty, new Binding("Node.Summary") { Source = item });
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") }; content.Children.Add(header); Grid.SetRow(summary, 1); content.Children.Add(summary);
        var grid = new Grid(); grid.Children.Add(content);
        foreach (var right in new[] { false, true }) grid.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = DesignTokens.Brush("Surface"), BorderBrush = DesignTokens.Brush("TextMuted"), BorderThickness = new Thickness(1), HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(right ? 0 : -4, 0, right ? -4 : 0, 0) });
        var border = new Border { Width = 240, Height = GraphGeometry.NodeHeight, CornerRadius = new CornerRadius(DesignTokens.Radius), BorderThickness = new Thickness(vm?.IsGraphSelected(item.Node) == true ? 2 : 1), Background = DesignTokens.Brush("Surface"), BorderBrush = DesignTokens.Brush(vm?.IsGraphSelected(item.Node) == true ? "Accent" : "Border"), Child = grid };
        cards[item.Node.Id] = border; return border;
    }
    private Control BuildConnection(EdgeItem item)
    {
        var project = vm?.Data.Projects.FirstOrDefault(p => p.Id == item.Edge.ProjectId);
        var selected = vm?.IsGraphSelected(item.Edge) == true;
        var control = new ResearchConnection(item.Edge) { Name = "ActionBlock" + item.Edge.Id.ToString("N"), StrokeThickness = selected ? 3 : 1.25, Stroke = selected ? DesignTokens.Brush("Accent") : ProjectColors.Marker(project), Foreground = ProjectColors.HeadingText, FontSize = DesignTokens.Caption, TextPadding = new Thickness(6, 3), TextCornerRadius = DesignTokens.Radius, TextBackground = ProjectColors.HeadingBrush(project), ArrowSize = new Size(8, 5) };
        control.Bind(BaseConnection.SourceProperty, new Binding(nameof(EdgeItem.Source)) { Source = item });
        control.Bind(BaseConnection.TargetProperty, new Binding(nameof(EdgeItem.Target)) { Source = item });
        control.Bind(BaseConnection.TextProperty, new Binding("Edge.Title") { Source = item });
        if (item.Edge.TargetId == null) control.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 };
        control.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; Focus(); var toggle = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
            vm?.SelectGraphItem(item.Edge, toggle);
            if (!toggle && control.IsOverLabel(e.GetPosition(control))) { draggingLabel = item.Edge; BeginGroupDrag(e); }
            e.Handled = true;
        }, RoutingStrategies.Tunnel, true);
        connectionControls[item.Edge.Id] = control;
        return control;
    }
    public void Fit()
    {
        if (Bounds.Width < 100 || Bounds.Height < 100) return;
        var points = nodes.SelectMany(n => new[] { new Point(n.Node.X, n.Node.Y), new Point(n.Node.X + 240, n.Node.Y + GraphGeometry.NodeHeight) }).Concat(edges.SelectMany(e => new[] { e.Source, e.Target, new Point((e.Source.X + e.Target.X) / 2 + e.Edge.LabelOffsetX, (e.Source.Y + e.Target.Y) / 2 + e.Edge.LabelOffsetY) })).ToList();
        if (points.Count == 0) return;
        var minX = points.Min(p => p.X); var minY = points.Min(p => p.Y); var maxX = points.Max(p => p.X) + 30; var maxY = points.Max(p => p.Y) + 30;
        ViewportZoom = Math.Clamp(Math.Min((Bounds.Width - 64) / Math.Max(1, maxX - minX), (Bounds.Height - 64) / Math.Max(1, maxY - minY)), .3, 1.1);
        ViewportLocation = new Point(minX - 32 / ViewportZoom, minY - 32 / ViewportZoom);
    }
    public Rect ContentBounds()
    {
        var points = nodes.SelectMany(n => new[] { new Point(n.Node.X, n.Node.Y), new Point(n.Node.X + 240, n.Node.Y + GraphGeometry.NodeHeight) }).Concat(edges.SelectMany(e => new[] { e.Source, e.Target, new Point((e.Source.X + e.Target.X) / 2 + e.Edge.LabelOffsetX - 70, (e.Source.Y + e.Target.Y) / 2 + e.Edge.LabelOffsetY - 16), new Point((e.Source.X + e.Target.X) / 2 + e.Edge.LabelOffsetX + 70, (e.Source.Y + e.Target.Y) / 2 + e.Edge.LabelOffsetY + 16) })).ToList();
        return points.Count == 0 ? new Rect(ViewportLocation, new Size(Bounds.Width / ViewportZoom, Bounds.Height / ViewportZoom)) : new Rect(new Point(points.Min(p => p.X) - 24, points.Min(p => p.Y) - 24), new Point(points.Max(p => p.X) + 24, points.Max(p => p.Y) + 24));
    }
    public Point GraphToScreen(Point p) => new((p.X - ViewportLocation.X) * ViewportZoom, (p.Y - ViewportLocation.Y) * ViewportZoom);
    private Point World(Point p) => new(p.X / ViewportZoom + ViewportLocation.X, p.Y / ViewportZoom + ViewportLocation.Y);
    public (Point Source, Point Target) ArrowEnds(GraphEdge edge)
    {
        var item = edges.FirstOrDefault(i => i.Edge == edge);
        if (item != null) return (item.Source, item.Target);
        var p = GraphGeometry.Endpoints(vm!.Data, edge); return (new Point(p.Source.X, p.Source.Y), new Point(p.Target.X, p.Target.Y));
    }
    public void BeginConnection()
    {
        CancelEndpoint(); connecting = vm?.Selected as GraphNode; Connecting = true; Focus(); UpdateAppearance();
        if (vm != null) vm.Status = connecting == null ? "Choose the source state, then a destination or empty canvas. Esc cancels." : "Choose a destination state or empty canvas for a planned action. Esc cancels.";
    }
    private bool initialFit;
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!initialFit && e.NewSize.Width >= 100 && e.NewSize.Height >= 100) { initialFit = true; Fit(); }
    }
    private void OnGraphPressed(object? sender, PointerPressedEventArgs e)
    {
        if (vm == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (endpointEdit != null) CancelEndpoint();
        var hit = vm.Edges.SelectMany(edge =>
        {
            var ends = ArrowEnds(edge);
            return Enum.GetValues<EdgeEnd>().Where(end => vm.Selected == edge || end == EdgeEnd.Target || edge.SourceId == null).Select(end =>
            {
                var delta = GraphToScreen(end == EdgeEnd.Source ? ends.Source : ends.Target) - e.GetPosition(this);
                return new { Edge = edge, End = end, Distance = delta.X * delta.X + delta.Y * delta.Y };
            });
        }).Where(candidate => candidate.Distance <= 144).OrderBy(candidate => candidate.Edge == vm.Selected ? 0 : 1).ThenBy(candidate => candidate.Distance).FirstOrDefault();
        if (hit != null && !Connecting)
        {
            vm.Selected = hit.Edge; BeginEndpoint(hit.Edge, hit.End, false);
            endpointPointer = e.Pointer; e.Pointer.Capture(this); e.Handled = true; return;
        }
        var point = World(e.GetPosition(this)); var node = vm.Nodes.Reverse().FirstOrDefault(n => new Rect(n.X - 6, n.Y, 252, GraphGeometry.NodeHeight).Contains(point));
        if (connecting != null)
        {
            var edge = vm.Graph.Connect(connecting, node, "New action");
            if (node == null) vm.Graph.MoveEndpoint(edge, EdgeEnd.Target, null, point.X, point.Y);
            vm.Selected = edge; connecting = null; Connecting = false; vm.Refresh(); e.Handled = true; return;
        }
        if (node != null)
        {
            if (!Connecting && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0) { vm.SelectGraphItem(node, true); e.Handled = true; return; }
            vm.SelectGraphItem(node);
            if (Connecting) { connecting = node; vm.Status = "Select a target state or empty canvas for a planned action. Esc cancels."; UpdateAppearance(); e.Handled = true; }
            else if (Math.Abs(point.X - node.X - 240) < 12)
            {
                var action = vm.Graph.Connect(node, null, "New action");
                var free = GraphGeometry.Endpoints(vm.Data, action).Target;
                vm.Graph.MoveEndpoint(action, EdgeEnd.Target, null, free.X, free.Y);
                vm.Selected = action; vm.Refresh(); vm.Status = "Planned action created."; e.Handled = true;
            }
            else
            {
                dragging = node; BeginGroupDrag(e); e.Handled = true;
            }
        }
        else if (e.Source is not BaseConnection && (e.Source as Visual)?.GetVisualAncestors().OfType<BaseConnection>().Any() != true)
        {
            if (Mode == GraphInteractionMode.Select)
            {
                marqueeStart = e.GetPosition(this); marqueePointer = e.Pointer; selectionRectangle = new Rect(marqueeStart.Value, default(Size)); initialSelection = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0 ? vm.GraphSelection.ToHashSet() : [];
                e.Pointer.Capture(this); AdornerChanged?.Invoke();
            }
            else { panning = true; lastPointer = e.GetPosition(this); e.Pointer.Capture(this); vm.Selected = null; }
            e.Handled = true;
        }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (marqueeStart is { } start)
        {
            var marqueePoint = e.GetPosition(this); selectionRectangle = new Rect(Math.Min(start.X, marqueePoint.X), Math.Min(start.Y, marqueePoint.Y), Math.Abs(start.X - marqueePoint.X), Math.Abs(start.Y - marqueePoint.Y));
            var rectangle = selectionRectangle.Value;
            var items = vm!.Nodes.Where(n => rectangle.Intersects(new Rect(GraphToScreen(new Point(n.X, n.Y)), new Size(240 * ViewportZoom, GraphGeometry.NodeHeight * ViewportZoom)))).Cast<Entity>().ToList();
            items.AddRange(vm.Edges.Where(edge => { var ends = ArrowEnds(edge); return rectangle.Contains(GraphToScreen(new Point((ends.Source.X + ends.Target.X) / 2 + edge.LabelOffsetX, (ends.Source.Y + ends.Target.Y) / 2 + edge.LabelOffsetY))); }));
            items.AddRange(vm.Nodes.Cast<Entity>().Concat(vm.Edges).Where(item => initialSelection.Contains(item.Id)));
            vm.SelectGraph(items.DistinctBy(item => item.Id)); AdornerChanged?.Invoke(); e.Handled = true; return;
        }
        if (endpointEdit is { Keyboard: false } edit)
        {
            var worldPoint = World(e.GetPosition(this)); var editDelta = worldPoint - edit.Initial;
            if (edit.Moved || (Math.Abs(editDelta.X) + Math.Abs(editDelta.Y)) * ViewportZoom >= 3) { edit.Moved = true; PreviewEndpoint(worldPoint, e.KeyModifiers.HasFlag(KeyModifiers.Alt)); }
            e.Handled = true; return;
        }
        if (endpointEdit == null && vm?.Selected is GraphEdge selected)
        {
            var points = ArrowEnds(selected); var mouse = e.GetPosition(this); var a = GraphToScreen(points.Source) - mouse; var b = GraphToScreen(points.Target) - mouse;
            Cursor = new Cursor(Math.Min(a.X * a.X + a.Y * a.Y, b.X * b.X + b.Y * b.Y) <= 144 ? StandardCursorType.SizeAll : StandardCursorType.Arrow);
        }
        if (dragging == null && draggingLabel == null && !panning) { base.OnPointerMoved(e); return; }
        var point = e.GetPosition(this); var delta = point - lastPointer; lastPointer = point;
        if (panning) ViewportLocation -= delta / ViewportZoom;
        else
        {
            dragDelta += delta / ViewportZoom;
            foreach (var node in dragNodes.Keys) { node.X += delta.X / ViewportZoom; node.Y += delta.Y / ViewportZoom; }
            foreach (var edge in dragEdges.Keys)
            {
                if (edge.SourceId == null) { edge.SourceX += delta.X / ViewportZoom; edge.SourceY += delta.Y / ViewportZoom; }
                if (edge.TargetId == null) { edge.TargetX += delta.X / ViewportZoom; edge.TargetY += delta.Y / ViewportZoom; }
            }
            foreach (var pair in dragLabelAnchors)
            {
                var ends = GraphGeometry.Endpoints(vm!.Data, pair.Key); pair.Key.LabelOffsetX = pair.Value.X + dragDelta.X - (ends.Source.X + ends.Target.X) / 2; pair.Key.LabelOffsetY = pair.Value.Y + dragDelta.Y - (ends.Source.Y + ends.Target.Y) / 2;
            }
        }
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (marqueeStart != null)
        {
            if (selectionRectangle is { Width: < 3, Height: < 3 }) vm!.SelectGraph(vm.Nodes.Cast<Entity>().Concat(vm.Edges).Where(item => initialSelection.Contains(item.Id)).ToList());
            marqueeStart = null; selectionRectangle = null; marqueePointer = null; e.Pointer.Capture(null); AdornerChanged?.Invoke(); e.Handled = true; return;
        }
        if (endpointEdit is { Keyboard: false } edit) { if (edit.Moved) { PreviewEndpoint(World(e.GetPosition(this)), e.KeyModifiers.HasFlag(KeyModifiers.Alt)); CommitEndpoint(); } else CancelEndpoint(); e.Handled = true; return; }
        if (dragging == null && draggingLabel == null && !panning) { base.OnPointerReleased(e); return; }
        var moved = dragging != null || draggingLabel != null; dragging = null; draggingLabel = null; nodePointer = null; dragNodes.Clear(); dragEdges.Clear(); dragLabelOffsets.Clear(); dragLabelAnchors.Clear(); panning = false; e.Pointer.Capture(null); if (moved) vm?.EndGraphGesture(true); else vm?.Save(); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { var moved = dragging != null || draggingLabel != null; dragging = null; draggingLabel = null; nodePointer = null; panning = false; if (moved) vm?.EndGraphGesture(true); if (marqueeStart != null) { marqueeStart = null; selectionRectangle = null; marqueePointer = null; AdornerChanged?.Invoke(); } if (endpointEdit is { Keyboard: false }) CancelEndpoint(); base.OnPointerCaptureLost(e); }
    private void BeginGroupDrag(PointerPressedEventArgs e)
    {
        vm!.BeginGraphGesture(); dragNodes.Clear(); dragEdges.Clear(); dragLabelOffsets.Clear(); dragLabelAnchors.Clear(); dragDelta = default;
        foreach (var node in vm.Nodes.Where(vm.IsGraphSelected)) dragNodes[node] = new Point(node.X, node.Y);
        foreach (var edge in vm.Edges.Where(vm.IsGraphSelected))
        {
            var ends = GraphGeometry.Endpoints(vm.Data, edge); dragEdges[edge] = ends; dragLabelOffsets[edge] = new Vector(edge.LabelOffsetX, edge.LabelOffsetY);
            dragLabelAnchors[edge] = new Point((ends.Source.X + ends.Target.X) / 2 + edge.LabelOffsetX, (ends.Source.Y + ends.Target.Y) / 2 + edge.LabelOffsetY);
        }
        lastPointer = e.GetPosition(this); nodePointer = e.Pointer; e.Pointer.Capture(this);
    }
    protected override void OnLostFocus(FocusChangedEventArgs e) { if (endpointEdit is { Keyboard: true }) CancelEndpoint(); base.OnLostFocus(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CancelInteraction(); e.Handled = true; return; }
        if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.None) { Mode = GraphInteractionMode.Select; e.Handled = true; return; }
        if (e.Key == Key.H && e.KeyModifiers == KeyModifiers.None) { Mode = GraphInteractionMode.Move; e.Handled = true; return; }
        if (e.Key == Key.Tab && vm?.Selected is GraphEdge selected)
        {
            var end = focusedEnd == EdgeEnd.Source ? EdgeEnd.Target : EdgeEnd.Source; CancelEndpoint(); BeginEndpoint(selected, end, true); e.Handled = true; return;
        }
        if (endpointEdit == null && focusedEnd is { } focused && vm?.Selected is GraphEdge arrow && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Left or Key.Right or Key.Up or Key.Down) BeginEndpoint(arrow, focused, true);
        if (endpointEdit is { Keyboard: true } edit)
        {
            if (e.Key == Key.Enter) { if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) edit.Snap = null; CommitEndpoint(); e.Handled = true; return; }
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            var delta = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), Key.Down => new Vector(0, step), _ => default };
            if (delta != default) { PreviewEndpoint(edit.Position + delta, e.KeyModifiers.HasFlag(KeyModifiers.Alt)); e.Handled = true; return; }
        }
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.None) { Fit(); e.Handled = true; return; }
        if (e.Key == Key.F2) { RenameRequested?.Invoke(); e.Handled = true; return; }
        if (e.Key is Key.Delete or Key.Back) { DeleteRequested?.Invoke(); e.Handled = true; return; }
        if (e.Key == Key.OemQuestion) { HelpRequested?.Invoke(); e.Handled = true; return; }
        // Graph commands must not fall through to Nodify's unbound delete/undo gestures.
        if (!e.Handled) base.OnKeyDown(e);
    }
    private sealed class EndpointEdit(GraphEdge edge, EdgeEnd end, Point position, bool keyboard)
    {
        public GraphEdge Edge { get; } = edge; public EdgeEnd End { get; } = end;
        public Point Initial { get; } = position; public bool Moved { get; set; }
        public Point Position { get; set; } = position; public bool Keyboard { get; } = keyboard; public GraphNode? Snap { get; set; }
    }
    private void BeginEndpoint(GraphEdge edge, EdgeEnd end, bool keyboard)
    {
        connecting = null; Connecting = false; vm!.BeginGraphGesture(); focusedEnd = end;
        var points = ArrowEnds(edge); endpointEdit = new EndpointEdit(edge, end, end == EdgeEnd.Source ? points.Source : points.Target, keyboard);
        endpointEdit.Snap = vm.Nodes.FirstOrDefault(n => n.Id == (end == EdgeEnd.Source ? edge.SourceId : edge.TargetId));
        vm.Status = keyboard ? "Arrow keys move this end · Shift moves 10 · Enter attaches/places · Alt+Enter leaves free · Tab switches ends · Esc cancels" : "Drop on a state to attach, or on canvas to leave free · Hold Alt to bypass snapping · Esc cancels";
        UpdateAppearance();
    }
    private void PreviewEndpoint(Point position, bool free)
    {
        var edit = endpointEdit!; edit.Position = position;
        edit.Snap = free ? null : vm!.Nodes.Select(n => new { Node = n, Distance = DistanceToRect(position, new Rect(n.X, n.Y, 240, GraphGeometry.NodeHeight)) }).Where(n => n.Distance * ViewportZoom <= 18).OrderBy(n => n.Distance).ThenBy(n => Math.Abs(n.Node.X + 120 - position.X) + Math.Abs(n.Node.Y + GraphGeometry.NodeHeight / 2 - position.Y)).FirstOrDefault()?.Node;
        var shown = edit.Snap != null && !edit.Keyboard ? new Point(edit.Snap.X + (edit.End == EdgeEnd.Source ? 240 : 0), edit.Snap.Y + GraphGeometry.NodeHeight / 2) : position;
        edges.First(i => i.Edge == edit.Edge).Preview(edit.End, shown); UpdateAppearance();
        if (!edit.Keyboard) vm!.Status = (edit.End == EdgeEnd.Source ? "Start" : "End") + (edit.Snap == null ? " → free on canvas" : " → attach to “" + edit.Snap.Title + "”") + " · Alt bypasses snapping · Esc cancels";
    }
    private static double DistanceToRect(Point p, Rect r) { var dx = Math.Max(r.Left - p.X, Math.Max(0, p.X - r.Right)); var dy = Math.Max(r.Top - p.Y, Math.Max(0, p.Y - r.Bottom)); return Math.Sqrt(dx * dx + dy * dy); }
    private void CommitEndpoint()
    {
        var edit = endpointEdit!; endpointEdit = null; var pointer = endpointPointer; endpointPointer = null; pointer?.Capture(null);
        vm!.Graph.MoveEndpoint(edit.Edge, edit.End, edit.Snap, edit.Position.X, edit.Position.Y); vm.EndGraphGesture(true);
        vm.Status = edit.Snap == null ? "Arrow end left free. Drag its handle to attach it later." : "Arrow end attached to “" + edit.Snap.Title + "”.";
    }
    public void CancelEndpoint()
    {
        if (endpointEdit == null) return;
        endpointEdit = null; var pointer = endpointPointer; endpointPointer = null; pointer?.Capture(null); vm!.EndGraphGesture(false); vm.Status = "Endpoint move cancelled.";
    }
    public void CancelInteraction()
    {
        CancelEndpoint();
        if (dragging != null || draggingLabel != null)
        {
            dragging = null; draggingLabel = null;
            foreach (var pair in dragNodes) { pair.Key.X = pair.Value.X; pair.Key.Y = pair.Value.Y; }
            foreach (var pair in dragEdges) { if (pair.Key.SourceId == null) { pair.Key.SourceX = pair.Value.Source.X; pair.Key.SourceY = pair.Value.Source.Y; } if (pair.Key.TargetId == null) { pair.Key.TargetX = pair.Value.Target.X; pair.Key.TargetY = pair.Value.Target.Y; } }
            foreach (var pair in dragLabelOffsets) { pair.Key.LabelOffsetX = pair.Value.X; pair.Key.LabelOffsetY = pair.Value.Y; }
            dragNodes.Clear(); dragEdges.Clear(); dragLabelOffsets.Clear(); dragLabelAnchors.Clear(); var pointer = nodePointer; nodePointer = null; pointer?.Capture(null); vm?.EndGraphGesture(false);
        }
        if (marqueeStart != null) { marqueeStart = null; selectionRectangle = null; vm?.SelectGraph(vm.Nodes.Cast<Entity>().Concat(vm.Edges).Where(item => initialSelection.Contains(item.Id)).ToList()); var pointer = marqueePointer; marqueePointer = null; pointer?.Capture(null); AdornerChanged?.Invoke(); }
        connecting = null; Connecting = false; UpdateAppearance();
    }
    private sealed class NodeItem : ObservableObject, IDisposable
    {
        public GraphNode Node { get; }
        public Point Location { get => new(Node.X, Node.Y); set { Node.X = value.X; Node.Y = value.Y; } }
        private readonly Action refresh;
        public NodeItem(GraphNode node, Action refresh) { Node = node; this.refresh = refresh; Node.PropertyChanged += Changed; }
        private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(GraphNode.X) or nameof(GraphNode.Y)) OnPropertyChanged(nameof(Location)); if (e.PropertyName is nameof(GraphNode.ColorHex) or nameof(GraphNode.Kind)) refresh(); }
        public void Dispose() => Node.PropertyChanged -= Changed;
    }
    private sealed class ResearchConnection(GraphEdge edge) : Connection, ICustomHitTest
    {
        public static readonly StyledProperty<Vector> RouteOffsetProperty = AvaloniaProperty.Register<ResearchConnection, Vector>(nameof(RouteOffset));
        public Vector RouteOffset { get => GetValue(RouteOffsetProperty); set => SetValue(RouteOffsetProperty, value); }
        static ResearchConnection() { AffectsGeometry<ResearchConnection>(RouteOffsetProperty); AffectsRender<ResearchConnection>(RouteOffsetProperty); }
        private Rect labelBounds;
        public bool IsOverLabel(Point point) => labelBounds.Contains(point);
        protected override Point GetTextPosition(FormattedText text, Point source, Point target) => base.GetTextPosition(text, source, target) + RouteOffset;
        protected override ((Point ArrowStartSource, Point ArrowStartTarget), (Point ArrowEndSource, Point ArrowEndTarget)) DrawLineGeometry(StreamGeometryContext context, Point source, Point target)
        {
            if (RouteOffset.Length < .01) return base.DrawLineGeometry(context, source, target);
            var middle = new Point((source.X + target.X) / 2, (source.Y + target.Y) / 2) + RouteOffset;
            var departure = Math.Clamp(Math.Abs(middle.X - source.X) * .5 + Math.Abs(middle.Y - source.Y) * .15, 24, 160);
            var arrival = Math.Clamp(Math.Abs(target.X - middle.X) * .5 + Math.Abs(target.Y - middle.Y) * .15, 24, 160);
            var tangent = new Vector(Math.Clamp(Math.Abs(target.X - source.X) * .15, 20, 80), 0);
            var firstControl = source + new Vector(departure, 0); var lastControl = target - new Vector(arrival, 0);
            context.BeginFigure(source, false); context.CubicBezierTo(firstControl, middle - tangent, middle); context.CubicBezierTo(middle + tangent, lastControl, target); context.EndFigure(false);
            return ((firstControl, source), (lastControl, target));
        }
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); RouteOffset = new Vector(edge.LabelOffsetX, edge.LabelOffsetY); edge.PropertyChanged += EdgeChanged; }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { edge.PropertyChanged -= EdgeChanged; base.OnDetachedFromVisualTree(e); }
        private void EdgeChanged(object? sender, PropertyChangedEventArgs e) { RouteOffset = new Vector(edge.LabelOffsetX, edge.LabelOffsetY); InvalidateVisual(); }
        protected override void DrawTextBackground(DrawingContext context, Rect bounds)
        {
            labelBounds = bounds.Inflate(TextPadding);
            context.DrawRectangle(TextBackground, new Pen(Stroke, .75), labelBounds, DesignTokens.Radius, DesignTokens.Radius);
        }
        bool ICustomHitTest.HitTest(Point point) => base.HitTest(point) || VisualRoot is Visual root && root.TranslatePoint(point, this) is Point local && labelBounds.Contains(local);
    }
    private sealed class EdgeItem : ObservableObject, IDisposable
    {
        public GraphEdge Edge { get; }
        private readonly Workspace data;
        private readonly List<GraphNode> watched;
        private readonly Action changed;
        private Point? previewSource, previewTarget;
        public Point Source { get { var p = GraphGeometry.Endpoints(data, Edge).Source; return previewSource ?? new Point(p.X, p.Y); } }
        public Point Target { get { var p = GraphGeometry.Endpoints(data, Edge).Target; return previewTarget ?? new Point(p.X, p.Y); } }
        public EdgeItem(GraphEdge edge, Workspace data, Action changed) { Edge = edge; this.data = data; this.changed = changed; Edge.PropertyChanged += Changed; watched = data.Nodes.Where(n => n.Id == edge.SourceId || n.Id == edge.TargetId).ToList(); foreach (var n in watched) n.PropertyChanged += Changed; }
        public void Preview(EdgeEnd end, Point position) { if (end == EdgeEnd.Source) previewSource = position; else previewTarget = position; Changed(null, new PropertyChangedEventArgs("")); }
        private void Changed(object? sender, PropertyChangedEventArgs e) { OnPropertyChanged(nameof(Source)); OnPropertyChanged(nameof(Target)); changed(); }
        public void Dispose() { Edge.PropertyChanged -= Changed; foreach (var n in watched) n.PropertyChanged -= Changed; }
    }
}
