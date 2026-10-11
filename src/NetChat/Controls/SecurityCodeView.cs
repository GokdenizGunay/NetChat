using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetChat.Controls;

/// <summary>Güvenlik kodunu eş aralıklı yazı tipiyle, her satırda 4 beşli grup olarak gösterir.</summary>
public sealed class SecurityCodeView : Grid
{
    public static readonly DependencyProperty CodeProperty =
        DependencyProperty.Register(nameof(Code), typeof(string), typeof(SecurityCodeView),
            new PropertyMetadata("", (d, e) => ((SecurityCodeView)d)._text.Text = e.NewValue as string ?? ""));

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public double CodeFontSize
    {
        get => _text.FontSize;
        set
        {
            _text.FontSize = value;
            _text.LineHeight = value * 1.6;
        }
    }

    private readonly TextBlock _text = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
    };

    public SecurityCodeView()
    {
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        CodeFontSize = 13;
        Children.Add(_text);
    }
}
