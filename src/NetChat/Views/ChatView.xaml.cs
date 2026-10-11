using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NetChat.Controls;
using NetChat.Models;
using NetChat.Services;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace NetChat.Views;

public partial class ChatView : UserControl
{
    private const double InfoMinWidth = 260;
    /// <summary>Pencere daraldığında panelin kendiliğinden inebileceği en küçük genişlik.</summary>
    private const double InfoAutoMinWidth = 180;
    private const double InfoMaxWidth = 560;
    /// <summary>Sohbet alanına en az bu kadar yer bırakılır.</summary>
    private const double MainMinWidth = 320;

    private double _infoTarget;

    public bool IsInfoOpen => _infoOpen;

    /// <summary>Bilgi paneli açıldı/kapandı (ana ekran liste genişliğini buna göre ayarlar).</summary>
    public event Action? InfoOpenChanged;

    private double InfoWidth => ClampInfoWidth(App.Settings.InfoWidth);

    /// <summary>
    /// Panel genişliği: kaydedilen değer, sohbet alanına en az <see cref="MainMinWidth"/> kalacak şekilde sınırlanır.
    /// Yer yetmezse panel <see cref="InfoAutoMinWidth"/>'e kadar kendiliğinden daralır.
    /// </summary>
    private double ClampInfoWidth(double width)
    {
        var room = ActualWidth > 0 ? ActualWidth - MainMinWidth : InfoMaxWidth;
        var max = Math.Min(InfoMaxWidth, room);
        return max >= InfoMinWidth
            ? Math.Clamp(width, InfoMinWidth, max)
            : Math.Max(InfoAutoMinWidth, max);
    }

    private void SetInfoWidth(double width, bool animate)
    {
        _infoTarget = width;
        InfoContent.Width = width;
        if (animate)
        {
            Anim.AnimateWidth(InfoPanel, width);
        }
        else
        {
            InfoPanel.BeginAnimation(WidthProperty, null);
            InfoPanel.Width = width;
        }
    }

    private void OnChatViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_infoOpen) return;
        var width = InfoWidth;
        if (Math.Abs(width - _infoTarget) > 0.5) SetInfoWidth(width, animate: false);
    }

    private readonly ChatService _chat = App.Chat;
    private readonly Dictionary<string, string> _drafts = new();
    private ChatViewModel? _vm;
    private ScrollViewer? _scroll;
    private bool _infoOpen;
    private int _newWhileScrolledUp;
    private bool _refreshingInfo;

    public Conversation? Conversation => _vm?.Conversation;

    public event Action? CloseRequested;

    public ChatView()
    {
        InitializeComponent();
        SizeChanged += OnChatViewSizeChanged;
        // Sürükle-bırak (sohbet alanı). Sürükleme pencere içinde bırakılmadan biterse (ör. kabul etmeyen bir yerde)
        // DragLeave gelmeyebilir; sürükleme olayları kesilince bırakma paneli kısa süre sonra kendiliğinden kapanır.
        AddHandler(DragDrop.DragEnterEvent, new DragEventHandler(OnDragEnter), handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, new DragEventHandler(OnDragOver), handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler(OnDragLeave), handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, new DragEventHandler(OnDrop), handledEventsToo: true);
        _dropOverlayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _dropOverlayTimer.Tick += (_, _) => HideDropOverlay();
        EmojiPicker.EmojiSelected += InsertEmoji;
        EmojiPopup.Opened += (_, _) => EmojiPicker.FocusSearch();
        Messages.Loaded += (_, _) =>
        {
            if (_scroll != null) return;
            _scroll = Messages.Template.FindName("PART_Scroll", Messages) as ScrollViewer;
            if (_scroll != null) _scroll.ScrollChanged += OnScrollChanged;
        };
        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatService.IsWindowActive) && _chat.IsWindowActive && IsVisible) Input.Focus();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(() => Input.Focus(), DispatcherPriority.Input);
        };
        _chat.BlockChanged += peer =>
        {
            if (_vm?.Conversation.Peer != peer) return;
            _vm.Refresh();
            UpdateComposer();
            RefreshInfo();
        };

        // Hızlı ifadeler + tam emoji seçici.
        foreach (var emoji in QuickReactionEmojis)
        {
            var button = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("HoverAction"),
                Width = 38,
                Height = 38,
                Content = new Emoji.Wpf.TextBlock { Text = emoji, FontSize = 22 },
                ToolTip = emoji,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, emoji);
            button.Click += (_, _) => ApplyReaction(emoji);
            QuickReactions.Children.Add(button);
        }
        var more = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("HoverAction"),
            Width = 38,
            Height = 38,
            Content = new SymbolIcon { Symbol = SymbolRegular.Add20, FontSize = 18 },
            ToolTip = "Diğer ifadeler",
        };
        System.Windows.Automation.AutomationProperties.SetName(more, "Diğer ifadeler");
        more.Click += (_, _) =>
        {
            ReactionPopup.IsOpen = false;
            ReactionPickerPopup.PlacementTarget = ReactionPopup.PlacementTarget;
            ReactionPickerPopup.Placement = ReactionPopup.Placement;
            ReactionPickerPopup.IsOpen = true;
        };
        QuickReactions.Children.Add(more);
        ReactionPicker.EmojiSelected += emoji =>
        {
            ReactionPickerPopup.IsOpen = false;
            ApplyReaction(emoji);
        };
    }

    private static readonly string[] QuickReactionEmojis = ["👍", "❤️", "😂", "😮", "😢", "🙏"];

    // ------------------------------------------------------------------ Sohbet değiştirme

    public void SetConversation(Conversation conv)
    {
        if (_vm?.Conversation == conv)
        {
            Input.Focus();
            return;
        }

        var switching = _vm != null;
        if (_vm != null)
        {
            _drafts[_vm.Conversation.Id] = Input.Text;
            _vm.Conversation.Messages.CollectionChanged -= OnMessagesChanged;
            _vm.Conversation.PropertyChanged -= OnConversationPropertyChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Dispose();
        }

        _vm = new ChatViewModel(conv);
        _vm.PropertyChanged += OnViewModelChanged;
        conv.Messages.CollectionChanged += OnMessagesChanged;
        conv.PropertyChanged += OnConversationPropertyChanged;
        DataContext = _vm;

        HeaderAvatar.SetBinding(Avatar.TextProperty, new Binding(nameof(Models.Conversation.Initials)) { Source = conv });
        HeaderAvatar.Key = conv.ColorKey;
        HeaderAvatar.IsGroup = conv.IsGroup;
        HeaderAvatar.SetBinding(Avatar.IsOnlineProperty, new Binding("Peer.ShowOnlineDot") { Source = conv, FallbackValue = false });
        HeaderAvatar.SetBinding(Avatar.IsIdleProperty, new Binding("Peer.ShowIdleDot") { Source = conv, FallbackValue = false });
        HeaderTitle.SetBinding(Emoji.Wpf.TextBlock.TextProperty, new Binding(nameof(Models.Conversation.Title)) { Source = conv });

        Messages.ItemsSource = conv.Messages;
        _newWhileScrolledUp = 0;
        UpdateNewBadge();
        NoMessages.Visibility = ShowsEncryptionNotice(conv) ? Visibility.Visible : Visibility.Collapsed;

        Input.Text = _drafts.TryGetValue(conv.Id, out var draft) ? draft : "";
        Input.MoveCaretToEnd();
        CancelReply(animate: false);
        MentionPopup.IsOpen = false;
        HeaderMuted.Visibility = conv.IsMuted ? Visibility.Visible : Visibility.Collapsed;
        UpdateComposer();
        UpdateTyping(animate: false);
        RefreshInfo();

        if (switching) Anim.FadeIn(MainArea, fromY: 0, fromX: 10, ms: 200);
        ScrollToEnd();
        Dispatcher.BeginInvoke(() => Input.Focus(), DispatcherPriority.Input);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.BlockedReason):
                UpdateComposer();
                break;
            case nameof(ChatViewModel.Subtitle):
                if (_vm?.IsTyping == true) Anim.FadeIn(HeaderSubtitle, fromY: 4, ms: 160);
                if (_vm is { IsGroup: false, IsTyping: false }) InfoSub.Text = _vm.Subtitle;
                UpdateTyping(animate: true);
                break;
            case nameof(ChatViewModel.IsTyping):
                UpdateTyping(animate: true);
                break;
        }
    }

    // ------------------------------------------------------------------ Yazıyor göstergesi

    private void UpdateTyping(bool animate)
    {
        var typing = _vm?.IsTyping == true;
        if (typing)
        {
            // Grupta kimin yazdığını balonun içinde göster ("Ali yazıyor…" → "Ali").
            var text = _vm!.Conversation.TypingText ?? "";
            var name = _vm.IsGroup ? text.Replace(" yazıyor…", "") : "";
            TypingName.Text = name;
            TypingName.Visibility = name.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (TypingBubble.Visibility != Visibility.Visible)
            {
                var stick = IsNearBottom;
                StartTypingDots();
                if (animate) Anim.FadeIn(TypingBubble, fromY: 8, fromX: -8, ms: 200);
                else TypingBubble.Visibility = Visibility.Visible;
                if (stick) ScrollToEnd();
            }
        }
        else if (TypingBubble.Visibility == Visibility.Visible)
        {
            if (animate) Anim.FadeOut(TypingBubble, toY: 6, ms: 150, done: StopTypingDots);
            else
            {
                TypingBubble.Visibility = Visibility.Collapsed;
                StopTypingDots();
            }
        }
    }

    private void StartTypingDots()
    {
        Ellipse[] dots = [Dot1, Dot2, Dot3];
        for (int i = 0; i < dots.Length; i++)
        {
            var transform = (System.Windows.Media.TranslateTransform)dots[i].RenderTransform;
            if (!Anim.Enabled)
            {
                transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                dots[i].BeginAnimation(OpacityProperty, null);
                dots[i].Opacity = 0.8;
                continue;
            }
            // Her nokta sırayla zıplar ve parlar; döngü 1.2 sn.
            var begin = TimeSpan.FromMilliseconds(i * 160);
            var bounce = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                BeginTime = begin,
                Duration = TimeSpan.FromMilliseconds(1200),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            var ease = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
            bounce.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0, TimeSpan.Zero));
            bounce.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(-4, TimeSpan.FromMilliseconds(250), ease));
            bounce.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0, TimeSpan.FromMilliseconds(500), ease));
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, bounce);

            var glow = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                BeginTime = begin,
                Duration = TimeSpan.FromMilliseconds(1200),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            glow.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.45, TimeSpan.Zero));
            glow.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(250)));
            glow.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.45, TimeSpan.FromMilliseconds(500)));
            dots[i].Opacity = 0.45;
            dots[i].BeginAnimation(OpacityProperty, glow);
        }
    }

    private void StopTypingDots()
    {
        foreach (var dot in new[] { Dot1, Dot2, Dot3 })
        {
            ((System.Windows.Media.TranslateTransform)dot.RenderTransform).BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            dot.BeginAnimation(OpacityProperty, null);
        }
    }

    private void OnConversationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Models.Conversation.Group) or nameof(Models.Conversation.Title) or nameof(Models.Conversation.IsMuted))
            RefreshInfo();
        if (e.PropertyName == nameof(Models.Conversation.IsMuted) && _vm != null)
            HeaderMuted.Visibility = _vm.Conversation.IsMuted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateComposer()
    {
        var reason = _vm?.BlockedReason;
        if (reason == null)
        {
            BlockedBar.Visibility = Visibility.Collapsed;
            if (Composer.Visibility != Visibility.Visible) Anim.FadeIn(Composer, fromY: 10);
        }
        else
        {
            BlockedText.Text = reason;
            UnblockButton.Visibility = _vm?.Conversation.Peer?.IsBlocked == true ? Visibility.Visible : Visibility.Collapsed;
            Composer.Visibility = Visibility.Collapsed;
            EmojiPopup.IsOpen = false;
            MentionPopup.IsOpen = false;
            CancelReply(animate: false);
            if (BlockedBar.Visibility != Visibility.Visible) Anim.FadeIn(BlockedBar, fromY: 6);
        }
    }

    // ------------------------------------------------------------------ Kaydırma

    private bool IsNearBottom => _scroll == null || _scroll.ScrollableHeight - _scroll.VerticalOffset < 120;

    private void ScrollToEnd()
    {
        // Sanallaştırılmış listede öğeler ölçüldükten sonra iki aşamada sona kaydır.
        Dispatcher.BeginInvoke(() =>
        {
            if (Messages.Items.Count > 0) Messages.ScrollIntoView(Messages.Items[^1]);
            _scroll?.ScrollToEnd();
            Dispatcher.BeginInvoke(() => _scroll?.ScrollToEnd(), DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var showButton = !IsNearBottom;
        if (showButton && ScrollDownButton.Visibility != Visibility.Visible) Anim.PopIn(ScrollDownButton);
        else if (!showButton && ScrollDownButton.Visibility == Visibility.Visible) Anim.FadeOut(ScrollDownButton, toY: 6, ms: 150);
        if (!showButton && _newWhileScrolledUp > 0)
        {
            _newWhileScrolledUp = 0;
            UpdateNewBadge();
        }
    }

    private void UpdateNewBadge()
    {
        NewBadge.Visibility = _newWhileScrolledUp > 0 ? Visibility.Visible : Visibility.Collapsed;
        NewBadgeText.Text = _newWhileScrolledUp > 99 ? "99+" : _newWhileScrolledUp.ToString();
    }

    private void OnScrollDownClicked(object sender, RoutedEventArgs e)
    {
        _newWhileScrolledUp = 0;
        UpdateNewBadge();
        ScrollToEnd();
    }

    /// <summary>
    /// "Mesajlaşmalarınız şifrelidir" kutusu: sohbet boşken ya da yalnızca grubun ilk sistem mesajı
    /// ("grubu oluşturdunuz" / "… sizi ekledi") varken görünür; sonraki herhangi bir mesajla kaybolur.
    /// </summary>
    private static bool ShowsEncryptionNotice(Conversation conv) =>
        conv.Messages.Count == 0 || (conv.Messages.Count == 1 && conv.Messages[0].IsSystem);

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var conv = _vm?.Conversation;
        if (conv == null) return;
        var show = ShowsEncryptionNotice(conv);
        if (NoMessages.Visibility == Visibility.Visible && !show) Anim.FadeOut(NoMessages, toY: -8);
        else if (show && NoMessages.Visibility != Visibility.Visible) NoMessages.Visibility = Visibility.Visible;

        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;
        var outgoing = e.NewItems.OfType<ChatMessage>().Any(m => m.IsOutgoing);
        if (outgoing || IsNearBottom)
        {
            ScrollToEnd();
        }
        else
        {
            _newWhileScrolledUp += e.NewItems.Count;
            UpdateNewBadge();
        }
    }

    // ------------------------------------------------------------------ Yazma alanı

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // Etiket önerileri açıkken ok tuşları/Enter/Tab öneriyi seçer.
        if (MentionPopup.IsOpen && MentionList.Items.Count > 0)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MentionList.SelectedIndex = (MentionList.SelectedIndex + 1) % MentionList.Items.Count;
                    MentionList.ScrollIntoView(MentionList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Up:
                    MentionList.SelectedIndex = (MentionList.SelectedIndex - 1 + MentionList.Items.Count) % MentionList.Items.Count;
                    MentionList.ScrollIntoView(MentionList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Tab when (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                    if (MentionList.SelectedItem is GroupMember member) InsertMention(member);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    MentionPopup.IsOpen = false;
                    e.Handled = true;
                    return;
            }
        }

        // Enter gönderir; Shift+Enter yeni satır açar.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send();
        }
        else if (e.Key == Key.Escape && EmojiPopup.IsOpen)
        {
            EmojiPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _replyTo != null)
        {
            CancelReply(animate: true);
            e.Handled = true;
        }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        var has = Input.Text.Trim().Length > 0;
        if (SendButton.IsEnabled != has)
        {
            SendButton.IsEnabled = has;
            if (has) Anim.PopIn(SendButton, 160);
        }
        if (has && _vm != null) _chat.NotifyTyping(_vm.Conversation);
        UpdateMentionPopup();
    }

    private void OnInputSelectionChanged(object sender, RoutedEventArgs e) => UpdateMentionPopup();

    // ------------------------------------------------------------------ Etiketleme (@)

    /// <summary>İmleçten önceki "@sorgu" parçası; yoksa null.</summary>
    private (int Start, string Query)? CurrentMentionToken()
    {
        var before = Input.TextBeforeCaret;
        var at = before.LastIndexOf('@');
        if (at < 0) return null;
        if (at > 0 && !char.IsWhiteSpace(before[at - 1])) return null;
        var query = before[(at + 1)..];
        if (query.Length > 32 || query.Contains('\n') || query.Contains('\r')) return null;
        return (at, query);
    }

    private void UpdateMentionPopup()
    {
        if (_vm?.Conversation.Group is not { } g || _vm.BlockedReason != null || CurrentMentionToken() is not { } token)
        {
            MentionPopup.IsOpen = false;
            return;
        }
        var matches = g.Members
            .Where(m => m.Id != _chat.MyId)
            .Select(m => new GroupMember { Id = m.Id, Name = _chat.FindPeer(m.Id)?.Name ?? m.Name })
            .Where(m => token.Query.Length == 0 || m.Name.Contains(token.Query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(m => m.Name.StartsWith(token.Query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToList();
        if (matches.Count == 0)
        {
            MentionPopup.IsOpen = false;
            return;
        }
        MentionList.ItemsSource = matches;
        MentionList.SelectedIndex = 0;
        MentionPopup.IsOpen = true;
    }

    private void OnMentionClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d)?.DataContext is GroupMember member)
            InsertMention(member);
    }

    private void InsertMention(GroupMember member)
    {
        if (CurrentMentionToken() is not { } token) return;
        Input.ReplaceBeforeCaret(token.Query.Length + 1, "@" + member.Name + " ");
        MentionPopup.IsOpen = false;
        Input.Focus();
    }

    // ------------------------------------------------------------------ Yanıtlama

    private ChatMessage? _replyTo;

    private void StartReply(ChatMessage msg)
    {
        if (msg.IsSystem || msg.IsDeleted || _vm?.BlockedReason != null) return;
        _replyTo = msg;
        ReplyBarName.Text = msg.IsOutgoing ? "Siz" : msg.SenderName;
        ReplyBarText.Text = msg.PreviewText;
        ReplyBarAccent.Fill = msg.IsOutgoing
            ? (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush")
            : AvatarPalette.NameBrushFor(msg.SenderId, ThemeService.IsDark);
        if (ReplyBar.Visibility != Visibility.Visible) Anim.FadeIn(ReplyBar, fromY: 8, ms: 180);
        Input.Focus();
    }

    private void CancelReply(bool animate)
    {
        _replyTo = null;
        if (ReplyBar.Visibility != Visibility.Visible) return;
        if (animate) Anim.FadeOut(ReplyBar, toY: 6, ms: 140);
        else ReplyBar.Visibility = Visibility.Collapsed;
    }

    private void OnCancelReplyClicked(object sender, RoutedEventArgs e)
    {
        CancelReply(animate: true);
        Input.Focus();
    }

    private void OnReplyClicked(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } msg) StartReply(msg);
    }

    private void OnBubbleMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Çift tıklama: yanıtla.
        if (e.ClickCount == 2 && MessageOf(sender) is { } msg && !msg.IsFile)
        {
            StartReply(msg);
            e.Handled = true;
        }
    }

    /// <summary>Alıntıya tıklanınca orijinal mesaja kaydırıp kısa süre vurgular.</summary>
    private void OnQuoteClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (MessageOf(sender)?.ReplyToId is not { } id || _vm == null) return;
        var target = _vm.Conversation.Messages.FirstOrDefault(m => m.Id == id);
        if (target == null)
        {
            _chat.ShowInfo("Alıntılanan mesaj bu cihazda bulunamadı.");
            return;
        }
        Messages.ScrollIntoView(target);
        target.IsHighlighted = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            target.IsHighlighted = false;
        };
        timer.Start();
    }

    // ------------------------------------------------------------------ İfadeler

    private ChatMessage? _reactionTarget;
    private Point _menuPoint;

    private void OnReactButtonClicked(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { } msg) return;
        OpenReactionPopup(msg, (UIElement)sender, System.Windows.Controls.Primitives.PlacementMode.Top);
    }

    private void OnReactMenuClicked(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { } msg) return;
        // Sağ tık menüsünün açıldığı noktanın hemen üstünde göster.
        OpenReactionPopup(msg, Messages, System.Windows.Controls.Primitives.PlacementMode.Relative,
            _menuPoint.X - 24, _menuPoint.Y - 66);
    }

    private void OpenReactionPopup(ChatMessage msg, UIElement target, System.Windows.Controls.Primitives.PlacementMode placement,
        double offsetX = 0, double offsetY = -4)
    {
        if (_vm?.BlockedReason != null) return;
        _reactionTarget = msg;
        ReactionPopup.PlacementTarget = target;
        ReactionPopup.Placement = placement;
        ReactionPopup.HorizontalOffset = offsetX;
        ReactionPopup.VerticalOffset = offsetY;
        ReactionPopup.IsOpen = true;
    }

    private void ApplyReaction(string emoji)
    {
        ReactionPopup.IsOpen = false;
        if (_reactionTarget != null) _chat.React(_reactionTarget, emoji);
        _reactionTarget = null;
    }

    /// <summary>Bir ifade çipine tıklamak o ifadeyi ekler/kaldırır.</summary>
    private void OnReactionChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReactionChip chip } fe) return;
        if (FindAncestor<ItemsControl>(fe)?.DataContext is ChatMessage msg) _chat.React(msg, chip.Emoji);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void OnSendClicked(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        if (_vm == null || Input.Text.Trim().Length == 0) return;
        MentionPopup.IsOpen = false;
        _chat.SendText(_vm.Conversation, Input.Text, _replyTo);
        CancelReply(animate: true);
        Input.Clear();
        _drafts.Remove(_vm.Conversation.Id);
        Input.Focus();
    }

    private void OnEmojiClicked(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = !EmojiPopup.IsOpen;

    private void InsertEmoji(string emoji)
    {
        Input.Focus();
        Input.InsertAtCaret(emoji);
    }

    private void OnAttachClicked(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Gönderilecek dosyaları seçin" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var file in dialog.FileNames) _chat.SendFile(_vm.Conversation, file, _replyTo);
        CancelReply(animate: true);
    }

    // ------------------------------------------------------------------ Sürükle-bırak

    private bool CanDrop(DragEventArgs e) =>
        _vm?.BlockedReason == null && e.Data.GetDataPresent(DataFormats.FileDrop);

    private readonly System.Windows.Threading.DispatcherTimer _dropOverlayTimer;

    private static bool IsFileDrag(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void ShowDropOverlay()
    {
        _dropOverlayTimer.Stop();
        _dropOverlayTimer.Start();
        if (DropOverlay.Visibility != Visibility.Visible) Anim.FadeIn(DropOverlay, fromY: 0, ms: 150);
    }

    private void HideDropOverlay()
    {
        _dropOverlayTimer.Stop();
        if (DropOverlay.Visibility == Visibility.Visible) Anim.FadeOut(DropOverlay, toY: 0, ms: 120);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!IsFileDrag(e)) return;
        if (CanDrop(e)) ShowDropOverlay();
        e.Effects = CanDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!IsFileDrag(e)) return;
        if (CanDrop(e)) ShowDropOverlay();
        e.Effects = CanDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (!IsFileDrag(e)) return;
        var pos = e.GetPosition(this);
        if (pos.X <= 0 || pos.Y <= 0 || pos.X >= ActualWidth || pos.Y >= ActualHeight) HideDropOverlay();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!IsFileDrag(e)) return;
        e.Handled = true;
        HideDropOverlay();
        if (!CanDrop(e) || _vm == null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths.Where(File.Exists)) _chat.SendFile(_vm.Conversation, path, _replyTo);
        CancelReply(animate: true);
        if (paths.Any(Directory.Exists)) _chat.ShowInfo("Klasörler gönderilemez; lütfen dosyaları seçin.");
    }

    // ------------------------------------------------------------------ Mesaj bağlam menüsü

    private static ChatMessage? MessageOf(object sender) => (sender as FrameworkElement)?.DataContext as ChatMessage;

    private void OnBubbleMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _menuPoint = Mouse.GetPosition(Messages);
        if (sender is not Border { ContextMenu: { } menu } || MessageOf(sender) is not { } msg)
        {
            e.Handled = true;
            return;
        }
        var hasFile = msg.IsFile && msg.LocalPath != null && File.Exists(msg.LocalPath);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Visibility = (item.Tag as string) switch
            {
                "reply" or "react" => !msg.IsDeleted && _vm?.BlockedReason == null ? Visibility.Visible : Visibility.Collapsed,
                "copy" => !msg.IsDeleted && msg.Kind == MessageKind.Text ? Visibility.Visible : Visibility.Collapsed,
                "open" or "folder" => hasFile ? Visibility.Visible : Visibility.Collapsed,
                "retry" => msg.IsOutgoing && !msg.IsDeleted && msg.Status is MessageStatus.Pending or MessageStatus.Failed
                    ? Visibility.Visible : Visibility.Collapsed,
                "deleteall" => _chat.CanDeleteForEveryone(msg) ? Visibility.Visible : Visibility.Collapsed,
                _ => Visibility.Visible,
            };
        }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender)?.Text is { } text)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
            }
        }
    }

    private void OnOpenFileClicked(object sender, RoutedEventArgs e) => OpenFile(MessageOf(sender));

    private void OnImageClicked(object sender, MouseButtonEventArgs e) => OpenFile(MessageOf(sender));

    private static void OpenFile(ChatMessage? msg)
    {
        if (msg?.LocalPath == null || !File.Exists(msg.LocalPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(msg.LocalPath) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void OnShowInFolderClicked(object sender, RoutedEventArgs e)
    {
        var path = MessageOf(sender)?.LocalPath;
        if (path == null || !File.Exists(path)) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch
        {
        }
    }

    private void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } msg) _chat.RetryMessage(msg);
    }

    private void OnDeleteForMeClicked(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } msg) _chat.DeleteForMe(msg);
    }

    private async void OnDeleteForAllClicked(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { } msg) return;
        var shell = FindShell();
        if (shell != null && !await shell.ConfirmAsync("Herkesten silinsin mi?",
                "Bu mesaj sohbetteki herkes için silinecek. Çevrimdışı olan alıcılarda mesaj kalabilir.", "Herkesten sil", danger: true))
            return;
        _chat.DeleteForEveryone(msg);
    }

    private ShellView? FindShell()
    {
        DependencyObject? d = this;
        while (d != null && d is not ShellView) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as ShellView;
    }

    // ------------------------------------------------------------------ Bilgi paneli

    private void OnHeaderClicked(object sender, MouseButtonEventArgs e) => ToggleInfo(true);

    private void OnInfoClicked(object sender, RoutedEventArgs e) => ToggleInfo(!_infoOpen);

    private void ToggleInfo(bool open)
    {
        if (_infoOpen == open) return;
        _infoOpen = open;
        if (open) RefreshInfo();
        InfoOpenChanged?.Invoke();
        InfoSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            SetInfoWidth(InfoWidth, animate: true);
        }
        else
        {
            _infoTarget = 0;
            Anim.AnimateWidth(InfoPanel, 0);
        }
    }

    private void OnInfoSplitterDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!_infoOpen) return;
        var width = ClampInfoWidth((double.IsNaN(InfoPanel.Width) ? InfoPanel.ActualWidth : InfoPanel.Width) - e.HorizontalChange);
        SetInfoWidth(width, animate: false);
    }

    private void OnInfoSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_infoOpen) App.Settings.InfoWidth = Math.Round(InfoPanel.Width);
    }

    private void RefreshInfo()
    {
        if (_vm == null) return;
        var conv = _vm.Conversation;
        InfoTitle.Text = conv.IsGroup ? "Grup bilgileri" : "Kişi bilgileri";
        InfoAvatar.Text = conv.Initials;
        InfoAvatar.Key = conv.ColorKey;
        InfoAvatar.IsGroup = conv.IsGroup;
        InfoName.Text = conv.Title;
        GroupInfo.Visibility = conv.IsGroup ? Visibility.Visible : Visibility.Collapsed;
        PeerInfo.Visibility = conv.IsGroup ? Visibility.Collapsed : Visibility.Visible;

        if (conv.IsGroup && conv.Group is { } g)
        {
            InfoSub.Text = $"Grup · {g.Members.Count} üye · {g.CreatedAt.ToLocalTime():d MMMM yyyy}";
            GroupDesc.Text = g.Description;
            GroupDesc.Visibility = string.IsNullOrWhiteSpace(g.Description) ? Visibility.Collapsed : Visibility.Visible;
            EditInfoPanel.Visibility = _vm.CanEditInfo ? Visibility.Visible : Visibility.Collapsed;
            if (!GroupNameBox.IsKeyboardFocusWithin) GroupNameBox.Text = g.Name;
            if (!GroupDescBox.IsKeyboardFocusWithin) GroupDescBox.Text = g.Description;
            AdminPanel.Visibility = _vm.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
            _refreshingInfo = true;
            OnlyAdminsSendToggle.IsChecked = g.OnlyAdminsCanSend;
            OnlyAdminsEditToggle.IsChecked = g.OnlyAdminsCanEdit;
            _refreshingInfo = false;
            MembersHeader.Text = $"{g.Members.Count} üye";
            AddMembersButton.Visibility = _vm.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
            MembersList.ItemsSource = _vm.Members;
            LeaveButton.Visibility = _vm.IsMember ? Visibility.Visible : Visibility.Collapsed;
            DisbandButton.Visibility = _vm.IsOwner ? Visibility.Visible : Visibility.Collapsed;
            DeleteButton.Content = _vm.IsMember ? "Gruptan çık ve sil" : "Sohbeti sil";
        }
        else
        {
            var peer = conv.Peer;
            InfoSub.Text = _vm.Subtitle;
            PeerAddress.Text = peer?.Address != null ? $"{peer.Address} : {peer.Port}" : "Bilinmiyor";
            PeerCode.Code = _vm.PeerFingerprint;
            DeleteButton.Content = "Sohbeti sil";
            LeaveButton.Visibility = Visibility.Collapsed;
            DisbandButton.Visibility = Visibility.Collapsed;
        }

        MuteButton.Content = conv.IsMuted ? "Sesi aç" : "Sessize al";
        MuteButton.Icon = new SymbolIcon(conv.IsMuted ? SymbolRegular.Alert20 : SymbolRegular.AlertOff20);
        var blocked = conv.Peer?.IsBlocked == true;
        BlockButton.Visibility = conv.IsGroup || conv.Peer == null ? Visibility.Collapsed : Visibility.Visible;
        BlockButton.Content = blocked ? "Engeli kaldır" : "Engelle";
        BlockButton.Icon = new SymbolIcon(blocked ? SymbolRegular.PersonAvailable20 : SymbolRegular.PersonProhibited20);
    }

    private void OnMuteClicked(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _chat.SetMuted(_vm.Conversation, !_vm.Conversation.IsMuted);
    }

    private async void OnBlockClicked(object sender, RoutedEventArgs e)
    {
        if (_vm?.Conversation.Peer is not { } peer || FindShell() is not { } shell) return;
        if (peer.IsBlocked)
        {
            _chat.SetBlocked(peer, false);
            return;
        }
        if (await shell.ConfirmAsync($"{peer.Name} engellensin mi?",
                "Engellenen kişi size bire bir mesaj gönderemez ve ifade bırakamaz; siz de onun durumunu ve son aktifliğini görmezsiniz. Ortak gruplardaki mesajları görünmeye devam eder.",
                "Engelle", danger: true))
            _chat.SetBlocked(peer, true);
    }

    private void OnUnblockClicked(object sender, RoutedEventArgs e)
    {
        if (_vm?.Conversation.Peer is { } peer) _chat.SetBlocked(peer, false);
    }

    private void OnSaveGroupInfoClicked(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _chat.RenameGroup(_vm.Conversation, GroupNameBox.Text);
        _chat.SetGroupDescription(_vm.Conversation, GroupDescBox.Text);
        Keyboard.ClearFocus();
        RefreshInfo();
    }

    private void OnOnlyAdminsSendClicked(object sender, RoutedEventArgs e)
    {
        if (_vm != null && !_refreshingInfo) _chat.SetOnlyAdminsCanSend(_vm.Conversation, OnlyAdminsSendToggle.IsChecked == true);
    }

    private void OnOnlyAdminsEditClicked(object sender, RoutedEventArgs e)
    {
        if (_vm != null && !_refreshingInfo) _chat.SetOnlyAdminsCanEdit(_vm.Conversation, OnlyAdminsEditToggle.IsChecked == true);
    }

    private async void OnAddMembersClicked(object sender, RoutedEventArgs e)
    {
        if (_vm?.Conversation.Group is not { } g || FindShell() is not { } shell) return;
        var picker = new PeerPicker(_chat.Peers.Where(p => !g.IsMember(p.Id)));
        var ok = await shell.DialogHost.ShowAsync("Üye ekle", picker, "Ekle",
            canSubmit: () => picker.Selected.Count > 0,
            bindRefresh: refresh => picker.SelectionChanged += refresh);
        if (ok) _chat.AddMembers(_vm.Conversation, picker.Selected);
    }

    private void OnMemberMenuClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MemberItem member } button || _vm?.Conversation.Group is not { } g) return;
        var conv = _vm.Conversation;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        void Add(string header, SymbolRegular icon, Action action)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon(icon) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (_chat.FindPeer(member.Id) is { } peer)
            Add($"{member.Name} ile mesajlaş", SymbolRegular.Chat20, () => FindShell()?.OpenConversation(_chat.OpenDirect(peer)));

        if (_vm.IsAdmin && !member.IsOwner)
        {
            var targetIsAdmin = g.Admins.Contains(member.Id);
            if (!targetIsAdmin)
                Add("Yönetici yap", SymbolRegular.ShieldPerson20, () => _chat.SetAdmin(conv, member.Id, true));
            else if (_vm.IsOwner)
                Add("Yöneticilikten çıkar", SymbolRegular.ShieldDismiss20, () => _chat.SetAdmin(conv, member.Id, false));

            if (!targetIsAdmin)
            {
                if (member.IsMuted) Add("Susturmayı kaldır", SymbolRegular.Speaker220, () => _chat.SetMuted(conv, member.Id, false));
                else Add("Sustur", SymbolRegular.SpeakerMute20, () => _chat.SetMuted(conv, member.Id, true));
            }

            if (!targetIsAdmin || _vm.IsOwner)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                Add("Gruptan çıkar", SymbolRegular.PersonDelete20, async () =>
                {
                    if (FindShell() is { } shell && await shell.ConfirmAsync("Üye çıkarılsın mı?",
                            $"{member.Name} gruptan çıkarılacak.", "Çıkar", danger: true))
                        _chat.RemoveMember(conv, member.Id);
                });
            }
        }

        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    private async void OnLeaveClicked(object sender, RoutedEventArgs e)
    {
        if (_vm == null || FindShell() is not { } shell) return;
        if (await shell.ConfirmAsync("Gruptan ayrılınsın mı?", "Artık bu gruptan mesaj almayacaksınız. Sohbet geçmişi bu cihazda kalır.",
                "Ayrıl", danger: true))
            _chat.LeaveGroup(_vm.Conversation);
    }

    private async void OnDisbandClicked(object sender, RoutedEventArgs e)
    {
        if (_vm == null || FindShell() is not { } shell) return;
        if (await shell.ConfirmAsync("Grup dağıtılsın mı?", "Grup tüm üyeler için kapatılacak ve kimse mesaj gönderemeyecek.",
                "Dağıt", danger: true))
            _chat.DisbandGroup(_vm.Conversation);
    }

    private async void OnClearClicked(object sender, RoutedEventArgs e)
    {
        if (_vm == null || FindShell() is not { } shell) return;
        if (await shell.ConfirmAsync("Sohbet temizlensin mi?", "Bu sohbetteki tüm mesajlar bu cihazdan silinecek.", "Temizle", danger: true))
            _chat.ClearConversation(_vm.Conversation);
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_vm == null || FindShell() is not { } shell) return;
        var conv = _vm.Conversation;
        await shell.DeleteConversationAsync(conv);
        if (!_chat.Conversations.Contains(conv))
        {
            ToggleInfo(false);
            CloseRequested?.Invoke();
        }
    }
}
