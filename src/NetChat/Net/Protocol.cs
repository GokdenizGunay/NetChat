using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetChat.Models;

namespace NetChat.Net;

/// <summary>Şifreli kanal üzerinden taşınan tüm uygulama paketlerinin temeli.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(ChatPacket), "chat")]
[JsonDerivedType(typeof(AckPacket), "ack")]
[JsonDerivedType(typeof(TypingPacket), "typing")]
[JsonDerivedType(typeof(DeletePacket), "delete")]
[JsonDerivedType(typeof(FileDonePacket), "filedone")]
[JsonDerivedType(typeof(FileCancelPacket), "filecancel")]
[JsonDerivedType(typeof(GroupStatePacket), "group")]
[JsonDerivedType(typeof(PingPacket), "ping")]
[JsonDerivedType(typeof(ReactionPacket), "react")]
[JsonDerivedType(typeof(AvatarRequestPacket), "avreq")]
[JsonDerivedType(typeof(AvatarPacket), "avatar")]
public abstract class Packet
{
}

public sealed class ChatPacket : Packet
{
    public Guid MessageId { get; set; }
    /// <summary>Grup mesajıysa grubun kimliği; bire bir mesajda null.</summary>
    public Guid? GroupId { get; set; }
    public MessageKind Kind { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string SenderName { get; set; } = "";
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public Guid? ReplyToId { get; set; }
    public string? ReplySenderId { get; set; }
    public string? ReplySenderName { get; set; }
    public string? ReplyText { get; set; }
    public List<GroupMember>? Mentions { get; set; }
}

/// <summary>Mesaja ifade bırakma; Emoji null ise kullanıcının ifadesi kaldırılır.</summary>
public sealed class ReactionPacket : Packet
{
    public Guid MessageId { get; set; }
    public Guid? GroupId { get; set; }
    public string? Emoji { get; set; }
}

public enum AckKind
{
    Delivered,
    Read,
}

public sealed class AckPacket : Packet
{
    public AckKind Kind { get; set; }
    public Guid? GroupId { get; set; }
    public List<Guid> MessageIds { get; set; } = [];
}

public sealed class TypingPacket : Packet
{
    public Guid? GroupId { get; set; }
}

public sealed class DeletePacket : Packet
{
    public Guid MessageId { get; set; }
    public Guid? GroupId { get; set; }
}

public sealed class FileDonePacket : Packet
{
    public Guid MessageId { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class FileCancelPacket : Packet
{
    public Guid MessageId { get; set; }
}

public sealed class GroupStatePacket : Packet
{
    public GroupState Group { get; set; } = new();
    /// <summary>Değişikliği açıklayan olay; her alıcı kendi bakış açısına göre metne çevirir.</summary>
    public GroupEvent? Event { get; set; }
    /// <summary>Son sürümlerin imzalı geçmişi (eskiden yeniye): geride kalan üye eksik sürümleri sırayla doğrular.</summary>
    public List<GroupState>? History { get; set; }
}

public sealed class PingPacket : Packet
{
}

/// <summary>Karşı tarafın profil resmini ister (duyurudaki parmak izi bizdekinden farklıysa).</summary>
public sealed class AvatarRequestPacket : Packet
{
}

/// <summary>Profil resmi (Base64 JPEG); null = resim yok.</summary>
public sealed class AvatarPacket : Packet
{
    public string? Data { get; set; }
}

/// <summary>Şifreli çerçevelerin içeriğini kodlar/çözer.</summary>
public static class Wire
{
    public const byte KindJson = 1;
    public const byte KindChunk = 2;
    public const int ChunkSize = 256 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Encode(Packet packet)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
        var buf = new byte[1 + json.Length];
        buf[0] = KindJson;
        json.CopyTo(buf, 1);
        return buf;
    }

    public static byte[] EncodeChunk(Guid messageId, long offset, ReadOnlySpan<byte> data)
    {
        var buf = new byte[1 + 16 + 8 + data.Length];
        buf[0] = KindChunk;
        messageId.TryWriteBytes(buf.AsSpan(1, 16));
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(17, 8), offset);
        data.CopyTo(buf.AsSpan(25));
        return buf;
    }

    public static Packet? DecodeJson(byte[] frame) =>
        JsonSerializer.Deserialize<Packet>(frame.AsSpan(1), Json);

    public static (Guid MessageId, long Offset, ReadOnlyMemory<byte> Data) DecodeChunk(byte[] frame) =>
        (new Guid(frame.AsSpan(1, 16)), BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(17, 8)), frame.AsMemory(25));
}
