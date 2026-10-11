using System.Security.Cryptography;
using NetChat.Core;
using NetChat.Models;

namespace NetChat.Services;

/// <summary>
/// Grup durumunun ağdan bağımsız kuralları: imzalama, doğrulama ve bir sürümden diğerine geçişin yetki denetimi.
/// Durum tüm üyeler arasında aktarıldığı için her sürüm, değişikliği yapanın kimlik anahtarıyla imzalanır.
/// İzinli geçişler:
///  - Yönetici: her değişiklik (dağıtılmamış grupta).
///  - Üye: kendi ayrılması / reddi; yöneticiler kısıtlamadıysa yalnızca grup adı ve açıklaması.
/// </summary>
public static class GroupRules
{
    public static void Sign(GroupState state, GroupEvent? ev, Identity identity)
    {
        state.LastEvent = ev;
        state.SignerId = identity.Id;
        state.Signature = null;
        state.Signature = Convert.ToBase64String(identity.SigningKey.SignData(state.SigningBytes(), HashAlgorithmName.SHA256));
    }

    /// <param name="publicKeyOf">Kimliğe göre açık anahtar (SPKI); bilinmiyorsa null.</param>
    public static bool Verify(GroupState state, Func<string, byte[]?> publicKeyOf)
    {
        if (state.SignerId is not { Length: > 0 } signer || state.Signature is not { Length: > 0 } signature) return false;
        try
        {
            var spki = publicKeyOf(signer);
            // Kimlik açık anahtarın özetidir: başka birinin anahtarıyla imzalanmış durum kabul edilmez.
            if (spki is not { Length: > 0 } || Identity.IdFromPublicKey(spki) != signer) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.VerifyData(state.SigningBytes(), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Bir önceki sürüme göre bu değişikliği imzalayanın yapmaya yetkisi var mı?</summary>
    public static bool IsValidTransition(GroupState prev, GroupState next, Func<string, byte[]?> publicKeyOf)
    {
        if (next.Id != prev.Id || next.Version != prev.Version + 1 || !Verify(next, publicKeyOf)) return false;
        var signer = next.SignerId!;
        if (!prev.IsMember(signer)) return false;
        if (prev.IsAdmin(signer) && !prev.Disbanded) return true;
        var key = next.ContentKey();
        if (ApplyLeave(prev, signer).ContentKey() == key) return true;
        if (prev.OnlyAdminsCanEdit || prev.Disbanded) return false;
        var edited = prev.Clone();
        edited.Name = next.Name;
        edited.Description = next.Description;
        return edited.ContentKey() == key;
    }

    /// <summary>
    /// Sürümleri atlayan durum (aradakiler bilinmiyor): yalnızca bilinen durumda zaten yönetici olan birinin imzası.
    /// </summary>
    public static bool IsValidJump(GroupState known, GroupState next, Func<string, byte[]?> publicKeyOf) =>
        next.Id == known.Id && next.Version > known.Version && Verify(next, publicKeyOf)
        && known.IsAdmin(next.SignerId!) && known.IsMember(next.SignerId!) && !known.Disbanded;

    /// <summary>Yeni üyenin kabul edeceği durum: beni ekleyen bir yöneticinin imzası.</summary>
    public static bool IsValidInvite(GroupState state, string myId, Func<string, byte[]?> publicKeyOf) =>
        !state.Disbanded && state.IsMember(myId) && Verify(state, publicKeyOf)
        && state.IsAdmin(state.SignerId!) && state.IsMember(state.SignerId!);

    /// <summary>
    /// Aynı sürümde iki farklı durum: tüm istemciler aynısını seçsin diye imzası sıralamada önce gelen kazanır.
    /// </summary>
    public static bool Wins(GroupState candidate, GroupState current) =>
        candidate.Signature != null && (current.Signature == null || string.CompareOrdinal(candidate.Signature, current.Signature) < 0);

    /// <summary>Üyenin ayrılması (kurucu ayrılırsa kurucu bir yöneticiye ya da ilk üyeye devredilir).</summary>
    public static GroupState ApplyLeave(GroupState g, string memberId)
    {
        var next = g.Clone();
        next.Members.RemoveAll(m => m.Id == memberId);
        next.Admins.Remove(memberId);
        next.Muted.Remove(memberId);
        if (next.OwnerId == memberId)
        {
            next.OwnerId = next.Admins.FirstOrDefault() ?? next.Members.FirstOrDefault()?.Id ?? "";
            if (next.OwnerId.Length > 0 && !next.Admins.Contains(next.OwnerId)) next.Admins.Add(next.OwnerId);
        }
        next.Version = g.Version + 1;
        return next;
    }
}
