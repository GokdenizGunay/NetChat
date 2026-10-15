using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace NetChat.Controls;

/// <summary>Dairesel avatar: profil resmi varsa resim, yoksa renkli zeminde baş harfler; isteğe bağlı çevrimiçi göstergesi.</summary>
public sealed class Avatar : Grid
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(Avatar), new PropertyMetadata("", (d, _) => ((Avatar)d).Refresh()));

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.Register(nameof(Key), typeof(string), typeof(Avatar), new PropertyMetadata("", (d, _) => ((Avatar)d).Refresh()));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(Avatar), new PropertyMetadata(40.0, (d, _) => ((Avatar)d).Refresh()));

    public static readonly DependencyProperty IsGroupProperty =
        DependencyProperty.Register(nameof(IsGroup), typeof(bool), typeof(Avatar), new PropertyMetadata(false, (d, _) => ((Avatar)d).Refresh()));

    public static readonly DependencyProperty IsOnlineProperty =
        DependencyProperty.Register(nameof(IsOnline), typeof(bool), typeof(Avatar), new PropertyMetadata(false, (d, _) => ((Avatar)d).Refresh()));

    public static readonly DependencyProperty IsIdleProperty =
        DependencyProperty.Register(nameof(IsIdle), typeof(bool), typeof(Avatar), new PropertyMetadata(false, (d, _) => ((Avatar)d).Refresh()));

    public static readonly DependencyProperty ImageProperty =
        DependencyProperty.Register(nameof(Image), typeof(ImageSource), typeof(Avatar), new PropertyMetadata(null, (d, _) => ((Avatar)d).Refresh()));

    public static readonly DependencyProperty ShowPresenceProperty =
        DependencyProperty.Register(nameof(ShowPresence), typeof(bool), typeof(Avatar), new PropertyMetadata(false, (d, _) => ((Avatar)d).Refresh()));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool IsGroup { get => (bool)GetValue(IsGroupProperty); set => SetValue(IsGroupProperty, value); }
    public bool IsOnline { get => (bool)GetValue(IsOnlineProperty); set => SetValue(IsOnlineProperty, value); }
    /// <summary>Boşta: pencere arka planda (sarı nokta).</summary>
    public bool IsIdle { get => (bool)GetValue(IsIdleProperty); set => SetValue(IsIdleProperty, value); }
    public bool ShowPresence { get => (bool)GetValue(ShowPresenceProperty); set => SetValue(ShowPresenceProperty, value); }
    /// <summary>Profil resmi; null ise baş harfler gösterilir.</summary>
    public ImageSource? Image { get => (ImageSource?)GetValue(ImageProperty); set => SetValue(ImageProperty, value); }

    private static readonly SolidColorBrush OnlineBrush = Frozen(Color.FromRgb(0x2E, 0xB8, 0x5C));
    private static readonly SolidColorBrush IdleBrush = Frozen(Color.FromRgb(0xF2, 0xB1, 0x1A));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private readonly Border _circle = new();
    private readonly TextBlock _initials = new()
    {
        Foreground = Brushes.White,
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly SymbolIcon _groupIcon = new()
    {
        Symbol = SymbolRegular.People24,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Border _presence = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Background = new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x5C)),
        BorderThickness = new Thickness(2),
    };

    public Avatar()
    {
        var content = new Grid();
        content.Children.Add(_initials);
        content.Children.Add(_groupIcon);
        _circle.Child = content;
        Children.Add(_circle);
        Children.Add(_presence);
        _presence.SetResourceReference(Border.BorderBrushProperty, "ApplicationBackgroundBrush");
        Refresh();
    }

    private void Refresh()
    {
        var s = Size;
        Width = Height = s;
        _circle.Width = _circle.Height = s;
        _circle.CornerRadius = new CornerRadius(s / 2);
        var image = IsGroup ? null : Image;
        _circle.Background = image != null
            ? new ImageBrush(image) { Stretch = Stretch.UniformToFill }
            : AvatarPalette.BrushFor(Key);
        _initials.Text = Text;
        _initials.FontSize = Math.Max(9, s * 0.38);
        _initials.Visibility = IsGroup || image != null ? Visibility.Collapsed : Visibility.Visible;
        _groupIcon.Visibility = IsGroup ? Visibility.Visible : Visibility.Collapsed;
        _groupIcon.FontSize = s * 0.5;
        var dot = Math.Max(10, s * 0.3);
        _presence.Width = _presence.Height = dot;
        _presence.CornerRadius = new CornerRadius(dot / 2);
        _presence.Background = IsOnline ? OnlineBrush : IdleBrush;
        _presence.Visibility = ShowPresence && (IsOnline || IsIdle) ? Visibility.Visible : Visibility.Collapsed;
    }
}
