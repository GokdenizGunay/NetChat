using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetChat.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace NetChat.Controls;

/// <summary>
/// Pencere içi modal diyalog katmanı (Fluent ContentDialog görünümünde).
/// Karartılmış arka plan + ortada kart; açılış/kapanış animasyonlu.
/// </summary>
public sealed class DialogHost : Grid
{
    public static DialogHost? Current { get; private set; }

    private readonly Border _scrim;
    private TaskCompletionSource<bool>? _pending;
    private Border? _card;

    public DialogHost()
    {
        Visibility = Visibility.Collapsed;
        _scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        _scrim.MouseLeftButtonDown += (_, _) => Close(false);
        Children.Add(_scrim);
        Current = this;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _pending != null)
            {
                Close(false);
                e.Handled = true;
            }
        };
    }

    public bool IsOpen => _pending != null;

    /// <summary>Diyaloğu gösterir; birincil düğmeye basılırsa true döner.</summary>
    public Task<bool> ShowAsync(string title, UIElement? body, string primaryText, string? secondaryText = "İptal",
        bool danger = false, Func<bool>? canSubmit = null, Action<Action>? bindRefresh = null, double width = 440)
    {
        if (_pending != null) Close(false);
        _pending = new TaskCompletionSource<bool>();

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var primary = new Button
        {
            Content = primaryText,
            Appearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 120,
            Margin = new Thickness(0, 0, secondaryText == null ? 0 : 8, 0),
            IsDefault = body is not System.Windows.Controls.TextBox { AcceptsReturn: true },
        };
        primary.Click += (_, _) =>
        {
            if (canSubmit == null || canSubmit()) Close(true);
        };
        if (canSubmit != null)
        {
            void Refresh() => primary.IsEnabled = canSubmit();
            Refresh();
            bindRefresh?.Invoke(Refresh);
        }

        var buttons = new System.Windows.Controls.Primitives.UniformGrid { Columns = secondaryText == null ? 1 : 2 };
        buttons.Children.Add(primary);
        if (secondaryText != null)
        {
            var secondary = new Button { Content = secondaryText, HorizontalAlignment = HorizontalAlignment.Stretch, IsCancel = true };
            secondary.Click += (_, _) => Close(false);
            buttons.Children.Add(secondary);
        }

        var footer = new Border { Padding = new Thickness(24, 18, 24, 18), Child = buttons, CornerRadius = new CornerRadius(0, 0, 8, 8) };
        footer.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        var content = new StackPanel { Margin = new Thickness(24, 22, 24, 22) };
        content.Children.Add(titleBlock);
        if (body != null) content.Children.Add(body);

        var layout = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        _card = new Border
        {
            Width = width,
            Margin = new Thickness(24, 40, 24, 40),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = layout,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Opacity = 0.35 },
        };
        _card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        _card.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        _card.MouseLeftButtonDown += (_, e) => e.Handled = true;
        Children.Add(_card);

        Visibility = Visibility.Visible;
        Anim.FadeIn(_scrim, fromY: 0, ms: 180);
        Anim.PopIn(_card);

        // İlk düzenlenebilir alana odaklan.
        Dispatcher.BeginInvoke(() =>
        {
            var focusTarget = FindFirst<System.Windows.Controls.TextBox>(content) as UIElement ?? primary;
            focusTarget.Focus();
            Keyboard.Focus(focusTarget as IInputElement);
        }, System.Windows.Threading.DispatcherPriority.Input);

        return _pending.Task;
    }

    public Task<bool> ConfirmAsync(string title, string message, string primaryText, bool danger = false)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return ShowAsync(title, text, primaryText, "İptal", danger);
    }

    private void Close(bool result)
    {
        var pending = _pending;
        var card = _card;
        _pending = null;
        _card = null;
        if (pending == null) return;

        Anim.FadeOut(_scrim, toY: 0, ms: 150, collapse: false);
        if (card != null)
        {
            Anim.FadeOut(card, toY: 8, ms: 150, done: () =>
            {
                Children.Remove(card);
                if (_pending == null) Visibility = Visibility.Collapsed;
            });
        }
        else
        {
            Visibility = Visibility.Collapsed;
        }
        pending.TrySetResult(result);
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                var found = FindFirst<T>(child);
                if (found != null) return found;
            }
        }
        else if (root is ContentControl { Content: DependencyObject c })
        {
            return FindFirst<T>(c);
        }
        else if (root is Decorator { Child: { } d })
        {
            return FindFirst<T>(d);
        }
        return null;
    }
}
