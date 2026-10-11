using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Emoji.Wpf;
using NetChat.Core;

namespace NetChat.Controls;

public sealed record EmojiItem(string Text, string Name);

public partial class EmojiGroupItem : ObservableObject
{
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "";
    public List<EmojiItem> Items { get; init; } = [];
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Renkli emoji seçici (Emoji.Wpf verisiyle). Satır bazlı sanallaştırma ile hızlıdır.</summary>
public partial class EmojiPicker : UserControl
{
    private const int PerRow = 8;
    private const int MaxRecent = 24;

    private static List<EmojiGroupItem>? _groups;
    // Son kullanılanlar profil ayarlarında saklanır (cihazda dosya bırakılmaz).
    private static List<string> Recent => App.Settings.RecentEmoji;

    private static readonly Dictionary<string, string> GroupTitles = new()
    {
        ["Smileys & Emotion"] = "İfadeler",
        ["People & Body"] = "İnsanlar",
        ["Animals & Nature"] = "Hayvanlar ve doğa",
        ["Food & Drink"] = "Yiyecek ve içecek",
        ["Travel & Places"] = "Seyahat ve yerler",
        ["Activities"] = "Etkinlikler",
        ["Objects"] = "Nesneler",
        ["Symbols"] = "Semboller",
        ["Flags"] = "Bayraklar",
    };

    public event Action<string>? EmojiSelected;

    public EmojiPicker()
    {
        InitializeComponent();
        _groups ??= BuildGroups();
        var tabs = new List<EmojiGroupItem>();
        var recent = new EmojiGroupItem { Title = "Son kullanılanlar", Icon = "🕘" };
        tabs.Add(recent);
        tabs.AddRange(_groups);
        foreach (var tab in tabs)
        {
            tab.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(EmojiGroupItem.IsSelected) && ((EmojiGroupItem)s!).IsSelected) ShowGroup((EmojiGroupItem)s!);
            };
        }
        Groups.ItemsSource = tabs;
        var initial = Recent.Count > 0 ? recent : _groups.FirstOrDefault();
        if (initial != null) initial.IsSelected = true;
    }

    private static List<EmojiGroupItem> BuildGroups()
    {
        var list = new List<EmojiGroupItem>();
        try
        {
            foreach (var g in EmojiData.AllGroups)
            {
                if (g.Name == "Component") continue;
                list.Add(new EmojiGroupItem
                {
                    Title = GroupTitles.TryGetValue(g.Name, out var t) ? t : g.Name,
                    Icon = g.Icon,
                    Items = g.EmojiList.Where(e => e.Renderable).Select(e => new EmojiItem(e.Text, e.Name)).ToList(),
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Emoji verisi yüklenemedi", ex);
        }
        return list;
    }

    private void ShowGroup(EmojiGroupItem group)
    {
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            return;
        }
        var items = group.Title == "Son kullanılanlar"
            ? Recent.Select(t => new EmojiItem(t, "")).ToList()
            : group.Items;
        SectionTitle.Text = group.Title;
        SetItems(items);
    }

    private void SetItems(IReadOnlyList<EmojiItem> items)
    {
        Rows.ItemsSource = items.Chunk(PerRow).ToList();
        if (Rows.Items.Count > 0) Rows.ScrollIntoView(Rows.Items[0]);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0)
        {
            var selected = (Groups.ItemsSource as IEnumerable<EmojiGroupItem>)?.FirstOrDefault(g => g.IsSelected);
            if (selected != null) ShowGroup(selected);
            return;
        }
        SectionTitle.Text = "Arama sonuçları";
        SetItems(_groups!.SelectMany(g => g.Items)
            .Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(400).ToList());
    }

    private void OnEmojiClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text }) return;
        Recent.Remove(text);
        Recent.Insert(0, text);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        SaveRecent();
        EmojiSelected?.Invoke(text);
    }

    public void FocusSearch() => SearchBox.Focus();

    private static void SaveRecent() => App.Settings.RecentEmoji = [.. Recent];
}
