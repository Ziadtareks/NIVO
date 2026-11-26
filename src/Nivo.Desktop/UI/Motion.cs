using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Nivo.UI;

public static class Motion
{
    internal interface IRenderParticipant
    {
        bool Step(double dtSeconds);
        void OnMotionDisabled();
    }

    private sealed record Transition(double Target);
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Transition>> transitions = new();
    private static readonly ConditionalWeakTable<FrameworkElement, TranslateTransform> entranceOffsets = new();
    private static readonly CubicEase EaseOut = CreateEaseOut();
    private static readonly List<IRenderParticipant> renderParticipants = [];
    private static bool reduced, followWindows = true, renderSubscribed;
    private static TimeSpan lastRenderEventTime = TimeSpan.MinValue;
    private static long lastRenderTimestamp, clockCount;

    public static event Action? PolicyChanged;
    public static bool Reduced { get => reduced; set { if (reduced == value) return; reduced = value; RefreshPolicy(); } }
    public static bool FollowWindows { get => followWindows; set { if (followWindows == value) return; followWindows = value; RefreshPolicy(); } }
    public static bool Enabled => !Reduced && (!FollowWindows || SystemParameters.ClientAreaAnimation);

    internal static long ClockCount => Interlocked.Read(ref clockCount);
    internal static bool IsRenderLoopActive => renderSubscribed;
    internal static int ActiveRenderParticipantCount => renderParticipants.Count;

    static Motion()
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is { HasShutdownStarted: true }) return;
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(new Action(RefreshPolicy));
            else RefreshPolicy();
        };
    }

    private static CubicEase CreateEaseOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private static void RefreshPolicy()
    {
        if (!Enabled) StopActive();
        PolicyChanged?.Invoke();
    }

    internal static void RegisterRenderParticipant(IRenderParticipant participant)
    {
        if (!Enabled)
        {
            participant.OnMotionDisabled();
            return;
        }
        if (!renderParticipants.Contains(participant))
            renderParticipants.Add(participant);
        EnsureRenderLoop();
    }

    internal static void UnregisterRenderParticipant(IRenderParticipant participant)
    {
        renderParticipants.Remove(participant);
        if (renderParticipants.Count == 0) StopRenderLoop();
    }

    private static void EnsureRenderLoop()
    {
        if (renderSubscribed || renderParticipants.Count == 0) return;
        renderSubscribed = true;
        lastRenderEventTime = TimeSpan.MinValue;
        lastRenderTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnCompositionTargetRendering;
    }

    private static void StopRenderLoop()
    {
        if (!renderSubscribed) return;
        renderSubscribed = false;
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        lastRenderEventTime = TimeSpan.MinValue;
        lastRenderTimestamp = 0;
    }

    private static void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        if (!Enabled || renderParticipants.Count == 0)
        {
            StopRenderLoop();
            return;
        }
        if (e is RenderingEventArgs renderingArgs)
        {
            if (renderingArgs.RenderingTime == lastRenderEventTime) return;
            lastRenderEventTime = renderingArgs.RenderingTime;
        }

        long now = Stopwatch.GetTimestamp();
        double dt = lastRenderTimestamp > 0
            ? (double)(now - lastRenderTimestamp) / Stopwatch.Frequency
            : 1.0 / 60.0;
        lastRenderTimestamp = now;
        dt = Math.Clamp(dt, 1.0 / 240.0, 0.05);

        for (int i = renderParticipants.Count - 1; i >= 0; i--)
        {
            var participant = renderParticipants[i];
            if (!participant.Step(dt))
            {
                if (i < renderParticipants.Count && ReferenceEquals(renderParticipants[i], participant))
                    renderParticipants.RemoveAt(i);
                else
                    renderParticipants.Remove(participant);
            }
        }

        if (renderParticipants.Count == 0) StopRenderLoop();
    }

    public static DoubleAnimation To(double value, int ms = 180) =>
        new(value, TimeSpan.FromMilliseconds(Enabled ? ms : 0)) { EasingFunction = EaseOut };

    public static void AnimateTo(DependencyObject target, DependencyProperty property, double value, int ms = 180)
    {
        var animated = target as IAnimatable ?? throw new ArgumentException("Motion requires an animatable property owner.", nameof(target));
        double from = (double)target.GetValue(property);
        if (Enabled && ms > 0 && transitions.TryGetValue(target, out var active) && active.TryGetValue(property, out var running) && Math.Abs(running.Target - value) < .001) return;
        if (!IsAnimating(target, property)
            && Math.Abs(from - value) < .001
            && (!animated.HasAnimatedProperties || Math.Abs((double)animated.GetAnimationBaseValue(property) - value) < .001))
        {
            return;
        }
        Run(target, property, from, value, ms);
    }

    internal static bool IsAnimating(DependencyObject target, DependencyProperty property) =>
        transitions.TryGetValue(target, out var active) && active.ContainsKey(property);

    internal static void Cancel(DependencyObject target)
    {
        CancelTarget(target);
        if (target is FrameworkElement element && entranceOffsets.TryGetValue(element, out var offset))
            CancelTarget(offset);
    }

    private static void CancelTarget(DependencyObject target)
    {
        if (!transitions.TryGetValue(target, out var active) || active.Count == 0) return;
        var properties = active.Keys.ToArray();
        active.Clear();
        var animated = (IAnimatable)target;
        foreach (var property in properties) animated.BeginAnimation(property, null);
    }

    private static void Run(DependencyObject target, DependencyProperty property, double from, double to, int ms)
    {
        var animated = (IAnimatable)target;
        bool hadActive = transitions.TryGetValue(target, out var existing) && existing.Remove(property);
        if (hadActive || animated.HasAnimatedProperties)
            animated.BeginAnimation(property, null);
        // WPF discards a SetCurrentValue override when animation storage is attached.
        // These are motion-owned properties: commit the resting value before the clock starts.
        if (hadActive || Math.Abs((double)target.GetValue(property) - to) >= .0001)
            target.SetValue(property, to);
        if (!Enabled || ms <= 0 || !double.IsFinite(from) || Math.Abs(from - to) < .001) return;
        var active = existing ?? transitions.GetOrCreateValue(target);
        var transition = new Transition(to);
        active[property] = transition;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop
        };
        var clock = animation.CreateClock();
        Interlocked.Increment(ref clockCount);
        clock.Completed += (_, _) =>
        {
            if (!active.TryGetValue(property, out var current) || !ReferenceEquals(current, transition)) return;
            active.Remove(property);
            animated.BeginAnimation(property, null);
        };
        animated.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
        clock.Controller!.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
    }

    private static void StopActive()
    {
        StopRenderLoop();
        if (renderParticipants.Count > 0)
        {
            var activeParticipants = renderParticipants.ToArray();
            renderParticipants.Clear();
            foreach (var participant in activeParticipants)
                participant.OnMotionDisabled();
        }
        foreach (var pair in transitions)
        {
            if (pair.Value.Count == 0) continue;
            var properties = pair.Value.Keys.ToArray();
            pair.Value.Clear();
            var animated = (IAnimatable)pair.Key;
            foreach (var property in properties) animated.BeginAnimation(property, null);
        }
    }

    private static bool Contains(Transform transform, Transform candidate) =>
        ReferenceEquals(transform, candidate) || transform is TransformGroup group && group.Children.Any(child => Contains(child, candidate));

    public static void Enter(FrameworkElement view, double distance = 12)
    {
        if (!Enabled) return;
        var offset = entranceOffsets.GetOrCreateValue(view);
        if (!Contains(view.RenderTransform, offset))
        {
            if (view.RenderTransform == Transform.Identity) view.RenderTransform = offset;
            else view.RenderTransform = new TransformGroup { Children = { view.RenderTransform, offset } };
        }
        double opacity = (double)view.GetAnimationBaseValue(UIElement.OpacityProperty);
        Run(view, UIElement.OpacityProperty, IsAnimating(view, UIElement.OpacityProperty) ? view.Opacity : 0, opacity, 180);
        Run(offset, TranslateTransform.YProperty, IsAnimating(offset, TranslateTransform.YProperty) ? offset.Y : distance, 0, 240);
    }

    public static void Button(Button button)
    {
        var scale = new ScaleTransform(1, 1);
        button.RenderTransformOrigin = new Point(.5, .5);
        if (button.RenderTransform is TransformGroup group) group.Children.Add(scale);
        else if (button.RenderTransform != Transform.Identity)
        {
            var groupWithPress = new TransformGroup();
            groupWithPress.Children.Add(button.RenderTransform);
            groupWithPress.Children.Add(scale);
            button.RenderTransform = groupWithPress;
        }
        else button.RenderTransform = scale;
        void Set(double value)
        {
            AnimateTo(scale, ScaleTransform.ScaleXProperty, value, 110);
            AnimateTo(scale, ScaleTransform.ScaleYProperty, value, 110);
        }
        button.PreviewMouseLeftButtonDown += (_, _) => Set(.96);
        button.PreviewMouseLeftButtonUp += (_, _) => Set(1);
        button.MouseLeave += (_, _) => Set(1);
        button.LostMouseCapture += (_, _) => Set(1);
    }
}
