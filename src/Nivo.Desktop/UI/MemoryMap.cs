using Nivo.Core;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
namespace Nivo.UI;

public sealed class MemoryMap : Grid
{
    public static readonly string[] Tiers = ["Active Now", "Core Memory", "Recent Memory", "Archive"];
    public static string TierLabel(string tier) => tier switch
    {
        "Active Now" => Loc.T("Active Now", "نشط الآن"),
        "Core Memory" => Loc.T("Core Memory", "ذاكرة أساسية"),
        "Recent Memory" => Loc.T("Recent Memory", "ذاكرة حديثة"),
        _ => Loc.T("Archive", "الأرشيف")
    };
    public static readonly string[] TierColors = ["#22D3EE", "#A78BFA", "#2DD4BF", "#8B9DBD"];
    private readonly Viewport3D viewport = new();
    private readonly Model3DGroup world = new();
    private readonly AxisAngleRotation3D tilt = new(new Vector3D(1, 0, 0), 0);
    private GeometryModel3D? hovered;
    private readonly AxisAngleRotation3D spin = new(new Vector3D(0, 1, 0), 24);
    private readonly Dictionary<GeometryModel3D, Memory> nodes = [];
    private readonly List<Memory> memories;
    private readonly Action<Memory> select;
    private readonly HashSet<string> hidden = [];
    private readonly TextBlock hint = Ui.Text(Loc.T("Drag to rotate · select a memory", "اسحب للتدوير · اضغط على أي نقطة لتعديلها"), 11, "#9CB3C3");
    private readonly System.Windows.Shapes.Ellipse hintDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer inertia = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Point? previous, start;
    private double velocity;
    public MemoryMap(IList<Memory> items, Action<Memory> select)
    {
        memories = items.ToList(); this.select = select; Height = 380; ClipToBounds = true; FlowDirection = Loc.Flow;
        var stage = new RadialGradientBrush { Center = new Point(.45, .35), GradientOrigin = new Point(.4, .25), RadiusX = .85, RadiusY = 1 }; stage.GradientStops.Add(new GradientStop(Ui.Brush("#163A7C").Color, 0)); stage.GradientStops.Add(new GradientStop(Ui.Brush("#0B1F4D").Color, .55)); stage.GradientStops.Add(new GradientStop(Ui.Brush("#071433").Color, 1)); Background = stage;
        SizeChanged += (_, _) => Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 16, 16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition()); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var topBar = new Grid { Margin = new Thickness(16, 14, 16, 0) }; topBar.ColumnDefinitions.Add(new ColumnDefinition()); topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var legend = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var tier in Tiers)
        {
            Button? b = null;
            b = Ui.Btn("", () => { if (!hidden.Add(tier)) hidden.Remove(tier); b!.Opacity = hidden.Contains(tier) ? .42 : 1; System.Windows.Automation.AutomationProperties.SetHelpText(b, hidden.Contains(tier) ? "Hidden" : "Visible"); Draw(); });
            var name = Ui.Text(TierLabel(tier) + "  " + memories.Count(m => m.Tier == tier), 11); name.Foreground = Ui.Brush("#E3EEFF");
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.Brush(TierColors[Array.IndexOf(Tiers, tier)]), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            b.Content = Ui.Row(dot, name); b.Padding = new Thickness(10, 6, 10, 6); b.FontSize = 11; b.Background = Ui.Brush("#80354F7D"); b.BorderBrush = Ui.Brush("#706D90BF"); b.ToolTip = Loc.T("Show or hide ", "إظهار أو إخفاء ") + TierLabel(tier); legend.Children.Add(b);
        }
        topBar.Children.Add(legend);
        var resetBtn = Ui.Btn(Loc.T("Reset view", "إعادة التمركز"), () => { inertia.Stop(); velocity = 0; spin.Angle = 24; tilt.Angle = 0; });
        resetBtn.Padding = new Thickness(10, 5, 10, 5); resetBtn.FontSize = 11; resetBtn.Foreground = Ui.Brush("#E3EEFF"); resetBtn.Background = Ui.Brush("#80354F7D"); resetBtn.BorderBrush = Ui.Brush("#706D90BF"); Grid.SetColumn(resetBtn, 1); topBar.Children.Add(resetBtn);
        Children.Add(topBar);
        viewport.Camera = new OrthographicCamera(new Point3D(0, 6, 8), new Vector3D(0, -6, -8), new Vector3D(0, 1, 0), 8);
        viewport.SizeChanged += (_, _) => ((OrthographicCamera)viewport.Camera).Width = Math.Max(6.4, viewport.ActualWidth / Math.Max(1, viewport.ActualHeight) * 3.7);
        var lights = new Model3DGroup(); lights.Children.Add(new AmbientLight(Color.FromRgb(145, 155, 175))); lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-2, -4, -3))); viewport.Children.Add(new ModelVisual3D { Content = lights });
        var rotations = new Transform3DGroup(); rotations.Children.Add(new RotateTransform3D(spin)); rotations.Children.Add(new RotateTransform3D(tilt)); world.Transform = rotations; viewport.Children.Add(new ModelVisual3D { Content = world }); Grid.SetRow(viewport, 1); Children.Add(viewport);
        hint.Foreground = Ui.Brush("#D6E7FF");
        var hintPill = new Border { Background = Ui.Brush("#66142A54"), BorderBrush = Ui.Brush("#557AA6E0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 4, 16, 14), Child = Ui.Row(hintDot, hint) };
        Grid.SetRow(hintPill, 2); Children.Add(hintPill);
        viewport.MouseLeftButtonDown += (_, e) => { inertia.Stop(); velocity = 0; previous = start = e.GetPosition(viewport); viewport.CaptureMouse(); };
        viewport.MouseMove += (_, e) =>
        {
            var point = e.GetPosition(viewport);
            if (previous.HasValue && e.LeftButton == MouseButtonState.Pressed) { velocity = (point.X - previous.Value.X) * .45; spin.Angle += velocity; tilt.Angle = Math.Clamp(tilt.Angle + (point.Y - previous.Value.Y) * .2, -18, 18); previous = point; }
            else
            {
                var memory = Hit(point); Hover(memory);
                if (memory != null) { int idx = Math.Max(0, Array.IndexOf(Tiers, memory.Tier)); hintDot.Fill = Ui.Brush(TierColors[idx]); hintDot.Visibility = Visibility.Visible; hint.Text = $"{TierLabel(memory.Tier)}  ·  {memory.Text}"; }
                else { hintDot.Visibility = Visibility.Collapsed; hint.Text = Loc.T("Drag to rotate · select a memory", "اسحب للتدوير · اضغط على أي نقطة لتعديلها"); }
                Cursor = memory == null ? Cursors.Arrow : Cursors.Hand;
            }
        };
        viewport.MouseLeave += (_, _) => { if (!previous.HasValue) { Hover(null); hintDot.Visibility = Visibility.Collapsed; hint.Text = Loc.T("Drag to spin · select a memory", "اسحب للتدوير · اختر ذاكرة"); } };
        viewport.MouseLeftButtonUp += (_, e) =>
        {
            var point = e.GetPosition(viewport); var origin = start; previous = start = null; viewport.ReleaseMouseCapture();
            if (origin.HasValue && (point - origin.Value).Length < 6) { if (Hit(point) is Memory memory) select(memory); }
            else if (Motion.Enabled && Math.Abs(velocity) > .1) inertia.Start();
        };
        inertia.Tick += (_, _) => { if (!Motion.Enabled || Math.Abs(velocity) < .03) { inertia.Stop(); return; } spin.Angle += velocity; velocity *= .91; };
        Loaded += (_, _) => { Theme.Changed += Draw; Draw(); }; Unloaded += (_, _) => { inertia.Stop(); Theme.Changed -= Draw; };
        Draw();
    }
    private void Hover(Memory? memory)
    {
        var node = memory == null ? null : nodes.FirstOrDefault(p => p.Value == memory).Key; if (node == hovered) return;
        void Scale(GeometryModel3D model, double value) { var transform = (ScaleTransform3D)((Transform3DGroup)model.Transform).Children[0]; transform.BeginAnimation(ScaleTransform3D.ScaleXProperty, Motion.To(value)); transform.BeginAnimation(ScaleTransform3D.ScaleYProperty, Motion.To(value)); transform.BeginAnimation(ScaleTransform3D.ScaleZProperty, Motion.To(value)); }
        if (hovered != null) Scale(hovered, 1); hovered = node; if (node != null) Scale(node, 1.5);
    }
    private Memory? Hit(Point point)
    {
        Memory? found = null;
        VisualTreeHelper.HitTest(viewport, null, result => { if (result is RayMeshGeometry3DHitTestResult hit && nodes.TryGetValue((GeometryModel3D)hit.ModelHit, out var memory)) { found = memory; return HitTestResultBehavior.Stop; } return HitTestResultBehavior.Continue; }, new PointHitTestParameters(point));
        return found;
    }
    private void Draw()
    {
        world.Children.Clear(); nodes.Clear(); hovered = null;
        var accent = Ui.Brush("#79BFFF").Color;
        world.Children.Add(Model(Sphere(.17), new Point3D(0, 0, 0), accent));
        for (int tier = 0; tier < 4; tier++)
        {
            if (hidden.Contains(Tiers[tier])) continue;
            double radius = 1 + tier * .48; var tierColor = Ui.Brush(TierColors[tier]).Color; var line = Color.FromArgb(125, tierColor.R, tierColor.G, tierColor.B);
            world.Children.Add(Model(Ring(radius, .009), new Point3D(), line));
            var peers = memories.Where(m => m.Tier == Tiers[tier]).ToArray();
            for (int index = 0; index < peers.Length; index++)
            {
                double angle = index * Math.PI * 2 / Math.Max(peers.Length, 1) + tier * .6;
                var node = Model(Sphere(.085), new Point3D(Math.Cos(angle) * radius, 0, Math.Sin(angle) * radius), tierColor);
                nodes[node] = peers[index]; world.Children.Add(node);
            }
        }
    }
    private static GeometryModel3D Model(MeshGeometry3D mesh, Point3D position, Color color)
    {
        var materials = new MaterialGroup(); materials.Children.Add(new DiffuseMaterial(new SolidColorBrush(color))); materials.Children.Add(new SpecularMaterial(Brushes.White, 60));
        var transforms = new Transform3DGroup(); transforms.Children.Add(new ScaleTransform3D()); transforms.Children.Add(new TranslateTransform3D(position.X, position.Y, position.Z));
        return new GeometryModel3D(mesh, materials) { BackMaterial = materials, Transform = transforms };
    }
    private static MeshGeometry3D Sphere(double radius)
    {
        var mesh = new MeshGeometry3D(); const int rows = 12, cols = 20;
        for (int y = 0; y <= rows; y++) for (int x = 0; x <= cols; x++)
        {
            double a = x * 2 * Math.PI / cols, b = y * Math.PI / rows;
            var n = new Vector3D(Math.Sin(b) * Math.Cos(a), Math.Cos(b), Math.Sin(b) * Math.Sin(a)); mesh.Positions.Add(new Point3D(n.X * radius, n.Y * radius, n.Z * radius)); mesh.Normals.Add(n);
        }
        for (int y = 0; y < rows; y++) for (int x = 0; x < cols; x++) { int a = y * (cols + 1) + x, b = a + cols + 1; foreach (int v in new[] { a, a + 1, b, a + 1, b + 1, b }) mesh.TriangleIndices.Add(v); }
        mesh.Freeze(); return mesh;
    }
    private static MeshGeometry3D Ring(double radius, double tube)
    {
        var mesh = new MeshGeometry3D(); const int cols = 96, rows = 6;
        for (int x = 0; x <= cols; x++) for (int y = 0; y <= rows; y++)
        {
            double a = x * 2 * Math.PI / cols, b = y * 2 * Math.PI / rows;
            mesh.Positions.Add(new Point3D((radius + tube * Math.Cos(b)) * Math.Cos(a), tube * Math.Sin(b), (radius + tube * Math.Cos(b)) * Math.Sin(a)));
            mesh.Normals.Add(new Vector3D(Math.Cos(b) * Math.Cos(a), Math.Sin(b), Math.Cos(b) * Math.Sin(a)));
        }
        for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) { int a = x * (rows + 1) + y, b = a + rows + 1; foreach (int v in new[] { a, a + 1, b, b, a + 1, b + 1 }) mesh.TriangleIndices.Add(v); }
        mesh.Freeze(); return mesh;
    }
}
