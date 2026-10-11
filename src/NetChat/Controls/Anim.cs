using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetChat.Models;

namespace NetChat.Controls;

/// <summary>
/// Yumuşak geçiş yardımcıları. Ayarlardan animasyonlar kapatılırsa tüm geçişler anında uygulanır.
/// </summary>
public static class Anim
{
    public static bool Enabled => App.Settings?.AnimationsEnabled ?? true;

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    private static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        element.RenderTransform = t;
        return t;
    }

    public static void FadeIn(UIElement element, double fromY = 12, double fromX = 0, int ms = 280, int delayMs = 0, Action? done = null)
    {
        NextGeneration(element); // bekleyen bir FadeOut varsa geçersiz kılınır
        element.Visibility = Visibility.Visible;
        var t = EnsureTranslate(element);
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.BeginAnimation(TranslateTransform.XProperty, null);
            element.Opacity = 1;
            t.X = t.Y = 0;
            done?.Invoke();
            return;
        }

        element.Opacity = 1;
        var fade = Delayed(0, 1, delayMs, ms);
        var finish = Once(done);
        fade.Completed += (_, _) => finish();
        Fallback(finish, delayMs + ms);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        t.BeginAnimation(TranslateTransform.YProperty, fromY != 0 ? Delayed(fromY, 0, delayMs, ms) : null);
        t.BeginAnimation(TranslateTransform.XProperty, fromX != 0 ? Delayed(fromX, 0, delayMs, ms) : null);
    }

    /// <summary>Gecikme süresince başlangıç değerinde bekleyen, sonra yumuşakça hedefe giden animasyon.</summary>
    private static AnimationTimeline Delayed(double from, double to, int delayMs, int ms)
    {
        var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delayMs > 0) anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + ms)), EaseOut));
        return anim;
    }

    public static void FadeOut(UIElement element, double toY = -8, double toX = 0, int ms = 200, Action? done = null, bool collapse = true)
    {
        if (!Enabled || element.Visibility != Visibility.Visible)
        {
            NextGeneration(element);
            if (collapse) element.Visibility = Visibility.Collapsed;
            done?.Invoke();
            return;
        }

        var t = EnsureTranslate(element);
        var duration = TimeSpan.FromMilliseconds(ms);
        var fade = new DoubleAnimation(element.Opacity, 0, duration) { EasingFunction = EaseIn };
        var generation = NextGeneration(element);
        var finish = Once(() =>
        {
            // Bu arada aynı öğe için yeni bir geçiş başladıysa (ör. hızla geri dönüldü) eski sonucu uygulama.
            if (Generation(element) != generation) return;
            if (collapse) element.Visibility = Visibility.Collapsed;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.X = t.Y = 0;
            done?.Invoke();
        });
        fade.Completed += (_, _) => finish();
        Fallback(finish, ms);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        if (toY != 0) t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, toY, duration) { EasingFunction = EaseIn });
        if (toX != 0) t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, toX, duration) { EasingFunction = EaseIn });
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, System.Runtime.CompilerServices.StrongBox<int>> Generations = new();

    private static int NextGeneration(UIElement element) => ++Generations.GetOrCreateValue(element).Value;

    private static int Generation(UIElement element) => Generations.GetOrCreateValue(element).Value;

    /// <summary>Bir eylemi en fazla bir kez çalıştıran sarmalayıcı.</summary>
    private static Action Once(Action? action)
    {
        var done = false;
        return () =>
        {
            if (done) return;
            done = true;
            action?.Invoke();
        };
    }

    /// <summary>
    /// Animasyon saati ilerlemezse (pencere gizli, ekran kilitli/uykuda) "bitti" olayı gelmez; akış takılmasın
    /// diye süre dolduktan kısa süre sonra işlemi yine de tamamla.
    /// </summary>
    private static void Fallback(Action finish, int ms)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms + 250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            finish();
        };
        timer.Start();
    }

    /// <summary>Ölçek + opaklık ile "belirme" efekti (diyaloglar, açılır paneller).</summary>
    public static void PopIn(FrameworkElement element, int ms = 220)
    {
        element.Visibility = Visibility.Visible;
        if (!Enabled)
        {
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
            return;
        }
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.94, 0.94);
        element.RenderTransform = scale;
        var duration = TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    /// <summary>Genişliği animasyonla değiştirir (yan paneller).</summary>
    public static void AnimateWidth(FrameworkElement element, double to, int ms = 260, Action? done = null)
    {
        if (!Enabled)
        {
            element.BeginAnimation(FrameworkElement.WidthProperty, null);
            element.Width = to;
            done?.Invoke();
            return;
        }
        var from = double.IsNaN(element.Width) ? element.ActualWidth : element.Width;
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) => done?.Invoke();
        element.Width = to;
        element.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }

    // --- Ekli özellik: yeni mesaj balonlarını yüklenince canlandırır ---

    public static readonly DependencyProperty AppearOnLoadProperty =
        DependencyProperty.RegisterAttached("AppearOnLoad", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnAppearOnLoadChanged));

    public static bool GetAppearOnLoad(DependencyObject d) => (bool)d.GetValue(AppearOnLoadProperty);

    public static void SetAppearOnLoad(DependencyObject d, bool value) => d.SetValue(AppearOnLoadProperty, value);

    private static void OnAppearOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe && (bool)e.NewValue) fe.Loaded += OnAppearLoaded;
    }

    private static void OnAppearLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if (fe.DataContext is not ChatMessage { AnimateIn: true } msg) return;
        msg.AnimateIn = false;
        FadeIn(fe, fromY: 14, fromX: msg.IsSystem ? 0 : msg.IsOutgoing ? 18 : -18, ms: 260);
    }
}
