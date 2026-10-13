using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace NetChat.Controls;

/// <summary>
/// Hafif, dönen yükleme halkası. Yalnızca ekranda görünürken döner: gizlendiğinde ya da pencereden çıkarıldığında
/// animasyon tamamen kaldırılır. (WPF-UI'nin ProgressRing'i görünümden çıkarılınca da dönmeyi sürdürüyor ve
/// sürekli ekran yenilemesi klavye girişini geciktiriyordu.)
/// </summary>
public sealed class Spinner : Grid
{
    private const double Thickness = 3.5;
    private readonly Ellipse _arc;
    private readonly RotateTransform _rotate = new();
    private AnimationClock? _clock;

    public Spinner()
    {
        var track = new Ellipse { StrokeThickness = Thickness };
        track.SetResourceReference(Shape.StrokeProperty, "ControlStrokeColorDefaultBrush");
        _arc = new Ellipse
        {
            StrokeThickness = Thickness,
            StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rotate,
        };
        _arc.SetResourceReference(Shape.StrokeProperty, "AccentFillColorDefaultBrush");
        Children.Add(track);
        Children.Add(_arc);

        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) => UpdateAnimation();
        // Görünümden çıkarılan bir üst öğede IsVisible her zaman false'a dönmüyor; pencereden ayrılınca kesin durdur.
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, _) => UpdateDash();
    }

    private void UpdateDash()
    {
        // Çevrenin dörtte biri kadar yay (kesik çizgi birimi kalınlıktır).
        var circumference = Math.PI * Math.Max(0, Math.Min(ActualWidth, ActualHeight) - Thickness) / Thickness;
        _arc.StrokeDashArray = [circumference / 4, circumference];
    }

    private void UpdateAnimation()
    {
        if (IsVisible && IsLoaded)
        {
            if (_clock != null) return;
            _clock = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever }.CreateClock();
            _rotate.ApplyAnimationClock(RotateTransform.AngleProperty, _clock);
        }
        else
        {
            Stop();
        }
    }

    /// <summary>
    /// Saati durdurup kaldırır. Yalnızca animasyonu özellikten ayırmak (BeginAnimation(..., null)) yetmez: sonsuz
    /// saat çöp toplanana kadar işlemeye ve her karede ekranı yeniden çizdirmeye devam eder.
    /// </summary>
    private void Stop()
    {
        if (_clock == null) return;
        _clock.Controller?.Remove();
        _rotate.ApplyAnimationClock(RotateTransform.AngleProperty, null);
        _clock = null;
    }
}
