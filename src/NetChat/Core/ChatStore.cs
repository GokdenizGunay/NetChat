using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NetChat.Models;

namespace NetChat.Core;

public sealed class StoreData
{
    public List<Peer> Peers { get; set; } = [];
    public List<Conversation> Conversations { get; set; } = [];
    /// <summary>Çevrimdışı alıcılara bağlandıklarında gönderilecek paketler (silme, ifade, okundu, grup ayrılma/ret).</summary>
    public List<OutboxItem> Outbox { get; set; } = [];
    /// <summary>
    /// Alınmış mesajların kimlikleri (içerik değil). Geçmiş tutulmasa da aynı mesaj yeniden gönderilirse
    /// (ör. "iletildi" onayı kaybolduysa) ikinci kez gösterilmesin diye saklanır.
    /// </summary>
    public List<Guid> SeenMessageIds { get; set; } = [];
}

/// <summary>Giden kutusundaki tek paket (alıcı bağlanınca gönderilir).</summary>
public sealed class OutboxItem
{
    public string To { get; set; } = "";
    public NetChat.Net.Packet Packet { get; set; } = null!;
}

/// <summary>
/// Sohbet geçmişi ve bilinen kişilerin serileştirilmesi. Veri, şifreli profil paketinin
/// (<see cref="ProfileSession"/>) içinde saklanır.
/// </summary>
public static class ChatStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static StoreData Deserialize(byte[]? json)
    {
        if (json is not { Length: > 0 }) return new StoreData();
        try
        {
            return JsonSerializer.Deserialize<StoreData>(json, Json) ?? new StoreData();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Giden kutusunda tanınmayan bir paket türü tüm sohbetleri kaybettirmesin: kutuyu atıp yeniden dene.
            Log.Error("Sohbet verisi tam okunamadı; giden kutusu atlanıyor", ex);
            try
            {
                if (JsonNode.Parse(json) is JsonObject root && root.Remove(nameof(StoreData.Outbox)))
                    return root.Deserialize<StoreData>(Json) ?? new StoreData();
            }
            catch (Exception retry)
            {
                Log.Error("Sohbet geçmişi okunamadı", retry);
            }
            return new StoreData();
        }
    }

    /// <summary>
    /// UI iş parçacığında çağrılır (modeller orada yaşar). <paramref name="keepHistory"/> kapalıysa sohbetlerden
    /// yalnızca saklanması gereken mesajlar yazılır: henüz iletilemeyen giden mesajlar ve okunmamış gelen mesajlar.
    /// Kişiler, gruplar, sohbet listesi ve giden kutusu her durumda saklanır.
    /// </summary>
    public static byte[] Serialize(StoreData data, bool keepHistory = true)
    {
        if (keepHistory) return JsonSerializer.SerializeToUtf8Bytes(data, Json);
        var root = JsonSerializer.SerializeToNode(data, Json)!;
        foreach (var conv in root[nameof(StoreData.Conversations)]?.AsArray() ?? [])
        {
            if (conv?[nameof(Conversation.Messages)] is not JsonArray messages) continue;
            for (int i = messages.Count - 1; i >= 0; i--)
                if (!MustKeep(messages[i])) messages.RemoveAt(i);
        }
        return JsonSerializer.SerializeToUtf8Bytes(root, Json);
    }

    private static bool MustKeep(JsonNode? m)
    {
        if (m == null) return false;
        if (m[nameof(ChatMessage.IsDeleted)]?.GetValue<bool>() == true) return false;
        if (m[nameof(ChatMessage.IsOutgoing)]?.GetValue<bool>() == true)
            return m[nameof(ChatMessage.PendingRecipients)] is JsonArray { Count: > 0 };
        var system = m[nameof(ChatMessage.Kind)]?.GetValue<string>() == nameof(MessageKind.System);
        return !system && m[nameof(ChatMessage.IsRead)]?.GetValue<bool>() != true;
    }
}
