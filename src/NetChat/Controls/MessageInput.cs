using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TextBlock = System.Windows.Controls.TextBlock;

namespace NetChat.Controls;

/// <summary>
/// Çok satırlı mesaj giriş alanı. WPF'nin TextBox'ı renkli emoji çizemediği için emojileri Emoji.Wpf görselleriyle
/// gösteren bir RichTextBox üzerine kurulu; dışarıya düz metin + imleç konumu sunar. Fluent metin kutusu görünümündedir.
/// </summary>
public sealed class MessageInput : Grid
{
    private readonly Border _frame;
    private readonly Border _focusLine;
    private readonly EmojiBox _box;
    private readonly TextBlock _placeholder;

    public event TextChangedEventHandler? TextChanged;
    public event RoutedEventHandler? SelectionChanged;

    public MessageInput()
    {
        _box = new EmojiBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontSize = 14,
            AcceptsReturn = true,
            AcceptsTab = false,
            AllowDrop = false, // dosya bırakma sohbet görünümünde ele alınır
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 140,
            MinHeight = 20,
            VerticalAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null,
            SpellCheck = { IsEnabled = false },
        };
        _box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextFillColorPrimaryBrush");
        _box.Document.PagePadding = new Thickness(0);
        _box.Document.LineHeight = 20;
        var paragraphStyle = new Style(typeof(Paragraph));
        paragraphStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
        _box.Resources.Add(typeof(Paragraph), paragraphStyle);
        AutomationProperties.SetName(_box, "Mesaj");

        _placeholder = new TextBlock
        {
            Text = "Bir mesaj yazın",
            FontSize = 14,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            // İmleç ilk harfin üstüne binmesin.
            Margin = new Thickness(4, 0, 0, 0),
        };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        var content = new Grid { Margin = new Thickness(11, 9, 11, 9) };
        content.Children.Add(_placeholder);
        content.Children.Add(_box);

        _focusLine = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(0, 0, 4, 4), Opacity = 0 };
        _focusLine.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");

        _frame = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            MinHeight = 40,
            Child = content,
            Cursor = Cursors.IBeam,
        };
        _frame.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        _frame.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        _frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == _frame || e.OriginalSource == content)
            {
                _box.Focus();
                _box.CaretPosition = _box.Document.ContentEnd;
            }
        };

        Children.Add(_frame);
        Children.Add(_focusLine);

        _box.TextChanged += (s, e) =>
        {
            _placeholder.Visibility = IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            TextChanged?.Invoke(this, e);
        };
        _box.SelectionChanged += (_, e) => SelectionChanged?.Invoke(this, e);
        _box.GotKeyboardFocus += (_, _) => SetFocusVisual(true);
        _box.LostKeyboardFocus += (_, _) => SetFocusVisual(false);
        // Biçimli metin yerine her zaman düz metin yapıştır.
        DataObject.AddPastingHandler(_box, OnPasting);
    }

    private void SetFocusVisual(bool focused)
    {
        _focusLine.Opacity = focused ? 1 : 0;
        _frame.SetResourceReference(Border.BackgroundProperty, focused ? "ControlFillColorInputActiveBrush" : "ControlFillColorDefaultBrush");
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (e.DataObject.GetData(DataFormats.UnicodeText) is string text) InsertAtCaret(text);
    }

    private bool IsEmpty =>
        _box.Document.Blocks.Count == 0 ||
        (_box.Document.Blocks.Count == 1 && _box.Document.Blocks.FirstBlock is Paragraph p && !p.Inlines.Any(i => i is not Run { Text.Length: 0 }));

    /// <summary>Düz metin (emojiler dahil). Satır sonları "\n".</summary>
    public string Text
    {
        get => NormalizeNewlines(ExtractText(_box.Document.ContentStart, _box.Document.ContentEnd));
        set
        {
            _box.Document.Blocks.Clear();
            _box.Document.Blocks.Add(new Paragraph(new Run(value ?? "")));
            _box.CaretPosition = _box.Document.ContentEnd;
            _placeholder.Visibility = string.IsNullOrEmpty(value) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>İmleçten önceki metnin uzunluğu (karakter).</summary>
    public int CaretIndex => NormalizeNewlines(ExtractText(_box.Document.ContentStart, _box.CaretPosition)).Length;

    /// <summary>İmleçten önceki metin.</summary>
    public string TextBeforeCaret => NormalizeNewlines(ExtractText(_box.Document.ContentStart, _box.CaretPosition));

    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    /// <summary>
    /// Belge aralığını düz metne çevirir: renkli emoji görselleri (EmojiInline) asıl karakterlerine,
    /// satır sonları ve paragraflar "\n"e dönüşür.
    /// </summary>
    private static string ExtractText(TextPointer start, TextPointer end)
    {
        var sb = new System.Text.StringBuilder();
        var nav = start;
        var firstParagraph = true;
        while (nav != null && nav.CompareTo(end) < 0)
        {
            var context = nav.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var run = nav.GetTextInRun(LogicalDirection.Forward);
                var remaining = nav.GetOffsetToPosition(end);
                if (remaining < run.Length) run = run[..remaining];
                sb.Append(run);
                nav = nav.GetPositionAtOffset(run.Length);
                continue;
            }
            if (context is TextPointerContext.ElementStart or TextPointerContext.EmbeddedElement)
            {
                switch (nav.GetAdjacentElement(LogicalDirection.Forward))
                {
                    case Emoji.Wpf.EmojiInline emoji:
                        sb.Append(emoji.Text);
                        nav = emoji.ElementEnd;
                        continue;
                    case LineBreak:
                        sb.Append('\n');
                        break;
                    case Paragraph:
                        if (!firstParagraph) sb.Append('\n');
                        firstParagraph = false;
                        break;
                }
            }
            nav = nav.GetNextContextPosition(LogicalDirection.Forward);
        }
        return sb.ToString();
    }

    public void Clear()
    {
        _box.Document.Blocks.Clear();
        _box.Document.Blocks.Add(new Paragraph());
        _placeholder.Visibility = Visibility.Visible;
    }

    public void MoveCaretToEnd() => _box.CaretPosition = _box.Document.ContentEnd;

    /// <summary>Seçimi metinle değiştirir (emojiler renkli görsele dönüşür).</summary>
    public void InsertAtCaret(string text)
    {
        _box.Selection.Text = text;
        _box.CaretPosition = _box.Selection.End;
        _box.Selection.Select(_box.CaretPosition, _box.CaretPosition);
    }

    /// <summary>İmleçten önceki <paramref name="count"/> karakteri (düz harfler) <paramref name="text"/> ile değiştirir.</summary>
    public void ReplaceBeforeCaret(int count, string text)
    {
        var end = _box.CaretPosition;
        var start = end;
        for (int i = 0; i < count; i++)
        {
            var prev = start.GetNextInsertionPosition(LogicalDirection.Backward);
            if (prev == null) break;
            start = prev;
        }
        _box.Selection.Select(start, end);
        InsertAtCaret(text);
    }

    public new bool Focus() => _box.Focus();

    public new bool IsKeyboardFocusWithin => _box.IsKeyboardFocusWithin;

    /// <summary>
    /// Emojileri renkli görsele (EmojiInline) çeviren RichTextBox. Emoji.Wpf.RichTextBox her tuş vuruşunda
    /// tüm belgeyi harf harf yeniden taradığı için uzun mesajlarda yazma gecikiyordu; bu sürüm yalnızca
    /// değişen bölgeyi tarar.
    /// </summary>
    private sealed class EmojiBox : RichTextBox
    {
        private bool _substituting;
        private TextPointer? _clickTarget;

        public EmojiBox()
        {
            SetValue(Block.LineHeightProperty, 1.0);
            CommandManager.AddPreviewExecutedHandler(this, OnPreviewExecuted);
        }

        protected override void OnTextChanged(TextChangedEventArgs e)
        {
            // Emojilerin yerleştirilmesi de bir değişikliktir; o iç değişiklik dışarıya ayrıca bildirilmez.
            if (_substituting) return;
            _substituting = true;
            try
            {
                BeginChange();
                // Sondan başa: öndeki değişiklikler arkadakilerin konumunu kaydırmasın.
                foreach (var change in e.Changes.Where(c => c.AddedLength > 0).OrderByDescending(c => c.Offset))
                {
                    // Birleşik emojiler (ten rengi, ZWJ) önceki karakterle birleşebilir; biraz geriden başla.
                    var start = Document.ContentStart.GetPositionAtOffset(Math.Max(0, change.Offset - 8)) ?? Document.ContentStart;
                    var end = Document.ContentStart.GetPositionAtOffset(change.Offset + change.AddedLength) ?? Document.ContentEnd;
                    SubstituteGlyphs(start, end);
                }
                EndChange();
            }
            finally
            {
                _substituting = false;
            }
            base.OnTextChanged(e);
        }

        private void SubstituteGlyphs(TextPointer start, TextPointer end)
        {
            var caret = CaretPosition;
            var nav = start;
            while (nav != null && nav.CompareTo(end) < 0)
            {
                if (nav.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
                {
                    nav = nav.GetNextContextPosition(LogicalDirection.Forward);
                    continue;
                }
                var text = nav.GetTextInRun(LogicalDirection.Forward);
                var limit = Math.Min(text.Length, Math.Max(0, nav.GetOffsetToPosition(end)));
                var first = -1;
                for (int i = 0; i < limit && first < 0; i++)
                    if (Emoji.Wpf.EmojiData.MatchStart.Contains(text[i])) first = i;
                if (first < 0)
                {
                    nav = nav.GetPositionAtOffset(text.Length);
                    continue;
                }
                // Eşleşme değişen bölgeyi biraz aşabilir (ör. ZWJ dizisinin devamı); koşu sonuna kadar bakılır.
                var match = Emoji.Wpf.EmojiData.MatchOne.Match(text, first);
                if (!match.Success || match.Index >= limit)
                {
                    nav = nav.GetPositionAtOffset(first + 1);
                    continue;
                }
                var from = nav.GetPositionAtOffset(match.Index)!;
                var to = from.GetPositionAtOffset(match.Length)!;
                var caretInside = from.CompareTo(caret) < 0 && to.CompareTo(caret) >= 0;
                new TextRange(from, to).Text = "";
                var emoji = new Emoji.Wpf.EmojiInline(from) { FontSize = FontSize, Foreground = Brushes.Black, Text = match.Value };
                if (caretInside) caret = emoji.ElementEnd;
                nav = emoji.ElementEnd;
            }
            CaretPosition = caret;
        }

        /// <summary>Kopyala/kes: emoji görselleri asıl karakterleriyle düz metin olarak panoya gider.</summary>
        private void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Command != ApplicationCommands.Copy && e.Command != ApplicationCommands.Cut) return;
            var text = ExtractText(Selection.Start, Selection.End);
            if (e.Command == ApplicationCommands.Cut) Selection.Text = "";
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Pano başka bir uygulamada kilitli.
            }
            e.Handled = true;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            // Emoji görseline tıklanınca imleç görselin en yakın kenarına konur.
            _clickTarget = null;
            if (e.ClickCount == 1 && VisualTreeHelper.HitTest(this, e.GetPosition(this))?.VisualHit is Image { Parent: Emoji.Wpf.EmojiInline emoji } image)
                _clickTarget = e.GetPosition(image).X < image.ActualWidth / 2 ? emoji.ElementStart : emoji.ElementEnd;
            base.OnMouseDown(e);
        }

        protected override void OnSelectionChanged(RoutedEventArgs e)
        {
            if (_clickTarget is { } target)
            {
                _clickTarget = null;
                Selection.Select(target, target);
            }
            base.OnSelectionChanged(e);
        }
    }
}
