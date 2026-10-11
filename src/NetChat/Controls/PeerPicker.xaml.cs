using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using NetChat.Models;

namespace NetChat.Controls;

public partial class PeerChoice : ObservableObject
{
    public Peer Peer { get; init; } = null!;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Seçilebilir mi? Grup seçiminde, kişinin gizlilik ayarı gruplara eklenmeye izin vermiyorsa false.</summary>
    public bool IsAllowed { get; init; } = true;

    public string Subtitle => IsAllowed ? Peer.PresenceText : "Gruplara eklenmeye izin vermiyor";
}

/// <summary>Çoklu kişi seçimi (grup oluşturma / üye ekleme / gizlilik listeleri).</summary>
public partial class PeerPicker : UserControl
{
    private readonly List<PeerChoice> _all;

    public event Action? SelectionChanged;

    /// <param name="peers">Listelenecek kişiler.</param>
    /// <param name="forGroup">Grup üyesi seçimi: gruplara eklenmeye izin vermeyenler soluk ve seçilemez gösterilir.</param>
    /// <param name="preselected">Başlangıçta seçili kişilerin kimlikleri.</param>
    public PeerPicker(IEnumerable<Peer> peers, bool forGroup = true, IEnumerable<string>? preselected = null)
    {
        InitializeComponent();
        var selected = preselected?.ToHashSet() ?? [];
        _all = peers
            .Select(p => new PeerChoice { Peer = p, IsAllowed = !forGroup || p.CanBeAddedToGroups, IsSelected = selected.Contains(p.Id) })
            .OrderByDescending(c => c.IsAllowed)
            .ThenByDescending(c => c.Peer.IsOnline)
            .ThenBy(c => c.Peer.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        List.ItemsSource = _all;
        EmptyText.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = _all.Count > 6 ? Visibility.Visible : Visibility.Collapsed;
    }

    public IReadOnlyList<Peer> Selected => _all.Where(c => c.IsSelected && c.IsAllowed).Select(c => c.Peer).ToList();

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        List.ItemsSource = q.Length == 0
            ? _all
            : _all.Where(c => c.Peer.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void OnCheckedChanged(object sender, RoutedEventArgs e) => SelectionChanged?.Invoke();
}
