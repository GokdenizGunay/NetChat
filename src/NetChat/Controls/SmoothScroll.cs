using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NetChat.Controls;

/// <summary>
/// Fare tekerleğiyle yumuşak kaydırma (tüm kaydırılabilir alanlar). Her tekerlek adımı hedefi kaydırır; içerik kısa,
/// yavaşlayarak biten bir geçişle hedefe gider. Kare güncellemesi yalnızca kaydırma sürerken yapılır; bitince
/// tamamen durur (boştayken ekran yenilenmez).
/// </summary>
public static class SmoothScroll
{
    private const double PixelsPerNotch = 64;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(220);

    private sealed class State
    {
        public double From;
        public double Target;
        public long Start;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();
    private static readonly List<ScrollViewer> Active = [];
    private static bool _rendering;

    /// <summary>Uygulama açılışında bir kez çağrılır.</summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl/Shift ile tekerlek (yakınlaştırma, yatay kaydırma) ve kapalı animasyonlarda varsayılan davranış.
        if (e.Handled || !Anim.Enabled || Keyboard.Modifiers != ModifierKeys.None) return;
        // Önizleme olayı dıştan içe ilerler: ilk yakalayan dış kaydırıcı, imlecin altındaki en içteki uygun kaydırıcıyı bulur.
        if (FindTarget(e.OriginalSource as DependencyObject, e.Delta) is not { } viewer) return;
        e.Handled = true;
        ScrollBy(viewer, -e.Delta / 120.0 * PixelsPerNotch);
    }

    /// <summary>İmlecin altındaki, bu yönde kaydırılabilen en içteki piksel kaydırmalı alan; yoksa null.</summary>
    private static ScrollViewer? FindTarget(DependencyObject? source, int delta)
    {
        for (var d = source; d != null; d = Parent(d))
        {
            if (d is not ScrollViewer sv || sv.ScrollableHeight <= 0) continue;
            // Öğe öğe kaydıran (sanallaştırmalı, piksel birimi olmayan) listelerde ofset piksel değildir: varsayılana bırak.
            if (!IsPixelBased(sv)) return null;
            var position = States.TryGetValue(sv, out var state) && Active.Contains(sv) ? state.Target : sv.VerticalOffset;
            var canScroll = delta > 0 ? position > 0.5 : position < sv.ScrollableHeight - 0.5;
            if (canScroll) return sv;
        }
        return null;
    }

    private static bool IsPixelBased(ScrollViewer sv) =>
        !sv.CanContentScroll ||
        sv.TemplatedParent is ItemsControl ic && VirtualizingPanel.GetScrollUnit(ic) == ScrollUnit.Pixel;

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    private static void ScrollBy(ScrollViewer sv, double pixels)
    {
        var state = States.GetOrCreateValue(sv);
        var running = Active.Contains(sv);
        var current = running ? Current(sv, state) : sv.VerticalOffset;
        state.Target = Math.Clamp((running ? state.Target : sv.VerticalOffset) + pixels, 0, sv.ScrollableHeight);
        state.From = current;
        state.Start = Environment.TickCount64;
        if (!running) Active.Add(sv);
        if (!_rendering)
        {
            _rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private static double Current(ScrollViewer sv, State state)
    {
        var t = Math.Clamp((Environment.TickCount64 - state.Start) / Duration.TotalMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        return state.From + (state.Target - state.From) * eased;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var sv = Active[i];
            if (!States.TryGetValue(sv, out var state) || !sv.IsVisible)
            {
                Active.RemoveAt(i);
                continue;
            }
            // İçerik bu arada kısaldıysa hedef sınır içinde kalsın.
            state.Target = Math.Min(state.Target, sv.ScrollableHeight);
            sv.ScrollToVerticalOffset(Current(sv, state));
            if (Environment.TickCount64 - state.Start >= Duration.TotalMilliseconds) Active.RemoveAt(i);
        }
        if (Active.Count == 0)
        {
            // Kaydırma bitti: kare güncellemesi tamamen durdurulur.
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }
}
