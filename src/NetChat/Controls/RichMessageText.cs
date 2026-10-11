using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Emoji.Wpf;
using NetChat.Models;

namespace NetChat.Controls;

/// <summary>
/// Mesaj metni: renkli emoji, tıklanabilir (mavi, altı çizili) bağlantılar ve "@Ad" etiket vurgusu.
/// </summary>
public sealed partial class RichMessageText : System.Windows.Controls.TextBlock
{
    public static readonly DependencyProperty TextContentProperty =
        DependencyProperty.Register(nameof(TextContent), typeof(string), typeof(RichMessageText),
            new PropertyMetadata(null, (d, _) => ((RichMessageText)d).Rebuild()));

    public static readonly DependencyProperty MentionsProperty =
        DependencyProperty.Register(nameof(Mentions), typeof(IEnumerable<GroupMember>), typeof(RichMessageText),
            new PropertyMetadata(null, (d, _) => ((RichMessageText)d).Rebuild()));

    public static readonly DependencyProperty LinkBrushProperty =
        DependencyProperty.Register(nameof(LinkBrush), typeof(Brush), typeof(RichMessageText),
            new PropertyMetadata(Brushes.DodgerBlue, (d, _) => ((RichMessageText)d).Rebuild()));

    public static readonly DependencyProperty MentionBrushProperty =
        DependencyProperty.Register(nameof(MentionBrush), typeof(Brush), typeof(RichMessageText),
            new PropertyMetadata(null, (d, _) => ((RichMessageText)d).Rebuild()));

    /// <summary>Gösterilecek düz metin (TextBlock.Text ile çakışmasın diye ayrı ad).</summary>
    public string? TextContent { get => (string?)GetValue(TextContentProperty); set => SetValue(TextContentProperty, value); }
    public IEnumerable<GroupMember>? Mentions { get => (IEnumerable<GroupMember>?)GetValue(MentionsProperty); set => SetValue(MentionsProperty, value); }
    public Brush LinkBrush { get => (Brush)GetValue(LinkBrushProperty); set => SetValue(LinkBrushProperty, value); }
    public Brush? MentionBrush { get => (Brush?)GetValue(MentionBrushProperty); set => SetValue(MentionBrushProperty, value); }

    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    private void Rebuild()
    {
        Inlines.Clear();
        var text = TextContent;
        if (string.IsNullOrEmpty(text)) return;

        // Bağlantılar ve etiketler için tek geçişte bulunan aralıklar.
        var spans = new List<(int Start, int Length, bool IsLink)>();
        foreach (Match m in UrlRegex().Matches(text))
        {
            var value = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            spans.Add((m.Index, value.Length, true));
        }

        var names = Mentions?.Select(m => m.Name).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct().OrderByDescending(n => n.Length).ToList() ?? [];
        if (names.Count > 0)
        {
            var pattern = "@(?:" + string.Join("|", names.Select(Regex.Escape)) + ")";
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                if (!spans.Any(s => m.Index < s.Start + s.Length && s.Start < m.Index + m.Length))
                    spans.Add((m.Index, m.Length, false));
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var pos = 0;
        foreach (var (start, length, isLink) in spans)
        {
            if (start > pos) AddPlain(Inlines, text[pos..start]);
            var value = text.Substring(start, length);
            if (isLink) Inlines.Add(MakeLink(value));
            else Inlines.Add(new Run(value) { FontWeight = FontWeights.SemiBold, Foreground = MentionBrush ?? LinkBrush });
            pos = start + length;
        }
        if (pos < text.Length) AddPlain(Inlines, text[pos..]);
    }

    /// <summary>Düz metni renkli emoji görselleri ile birlikte ekler.</summary>
    private void AddPlain(InlineCollection inlines, string text)
    {
        var pos = 0;
        foreach (Match m in EmojiData.MatchOne.Matches(text))
        {
            if (m.Index > pos) inlines.Add(new Run(text[pos..m.Index]));
            // EmojiInline kendi TextBlock'u dışında renk karıştırması uygulayıp tek renk çiziyor;
            // Emoji.Wpf.TextBlock ise her zaman renkli çizer, bu yüzden onu satır içine yerleştiriyoruz.
            inlines.Add(new InlineUIContainer(new Emoji.Wpf.TextBlock { Text = m.Value, FontSize = FontSize })
            {
                BaselineAlignment = BaselineAlignment.TextBottom,
            });
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
    }

    private Hyperlink MakeLink(string value)
    {
        var url = value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + value : value;
        var link = new Hyperlink(new Run(value))
        {
            Foreground = LinkBrush,
            TextDecorations = System.Windows.TextDecorations.Underline,
            Cursor = Cursors.Hand,
            ToolTip = url,
            FocusVisualStyle = null,
        };
        link.Click += (_, e) =>
        {
            e.Handled = true;
            // Yalnızca web bağlantıları açılır.
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch
                {
                }
            }
        };
        return link;
    }
}
