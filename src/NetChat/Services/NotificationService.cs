using System.IO;
using System.Text.Json;
using Microsoft.Toolkit.Uwp.Notifications;
using NetChat.Controls;
using NetChat.Core;
using NetChat.Models;

namespace NetChat.Services;

/// <summary>Windows bildirimleri (toast). Yanıtla düğmesi ve satır içi yanıt kutusu içerir.</summary>
public sealed class NotificationService
{
    private const string ToastGroup = "chats";
    private readonly AppSettings _settings;

    /// <summary>Bildirim etkileşimi: (eylem: open/reply/markread, konuşma Id, yanıt metni). Arka plan iş parçacığında gelir.</summary>
    public event Action<string, string, string?>? Activated;

    /// <summary>Toast gösterilemezse (ör. sistem kısıtlaması) tepsi balonu için.</summary>
    public event Action<string, string>? Fallback;

    public NotificationService(AppSettings settings)
    {
        _settings = settings;
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
        }
        catch (Exception ex)
        {
            Log.Error("Bildirim sistemi başlatılamadı", ex);
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var text = e.UserInput.TryGetValue("reply", out var value) ? value as string : null;
        var args = ToastArguments.Parse(e.Argument);
        // Aynı exe'den birden fazla örnek (--profile) çalışıyorsa Windows bildirimi herhangi birine iletebilir:
        // bildirimi gösteren örneğe aktar.
        if (args.TryGetValue("inst", out var inst) && inst != AppPaths.Profile)
        {
            InstancePipe.SendTo(inst, InstancePipe.ToastPrefix + JsonSerializer.Serialize(new[] { e.Argument, text }));
            return;
        }
        Dispatch(args, text);
    }

    /// <summary>Başka bir örneğin aldığı ve bu örneğe aktardığı bildirim etkileşimi.</summary>
    public void HandleForwarded(string payload)
    {
        try
        {
            var parts = JsonSerializer.Deserialize<string?[]>(payload);
            if (parts is [{ } argument, var text]) Dispatch(ToastArguments.Parse(argument), text);
        }
        catch (Exception ex)
        {
            Log.Error("Aktarılan bildirim etkileşimi okunamadı", ex);
        }
    }

    private void Dispatch(ToastArguments args, string? text)
    {
        if (!args.TryGetValue("conv", out var convId)) return;
        if (!args.TryGetValue("action", out var action)) action = "open";
        if (action == "reply" && string.IsNullOrWhiteSpace(text)) action = "open";
        Activated?.Invoke(action, convId, text);
    }

    public static string TagFor(string convId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(convId)), 0, 8);

    public void ShowMessage(Conversation conv, ChatMessage msg)
    {
        // Zil (bildirim alanı rayı) ile tüm bildirimler geçici olarak sessize alınabilir.
        if (!_settings.NotificationsEnabled || _settings.NotificationsMuted) return;

        var title = conv.IsGroup ? $"{msg.SenderName} @ {conv.Title}" : conv.Title;
        var body = !_settings.ShowMessagePreview
            ? "Yeni mesaj"
            : msg.Kind == MessageKind.File ? $"🧾 {msg.FileName} ({msg.FileSizeText})" : msg.Text ?? "";
        if (body.Length > 300) body = body[..300] + "…";

        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddArgument("conv", conv.Id)
                .AddArgument("inst", AppPaths.Profile)
                .AddText(title)
                .AddText(body)
                .AddCustomTimeStamp(msg.Timestamp.LocalDateTime);

            var avatar = AvatarPalette.RenderPng(conv.IsGroup ? conv.Id : msg.SenderId,
                conv.IsGroup ? conv.Initials : Conversation.MakeInitials(msg.SenderName), AppPaths.TempDir);
            if (avatar != null) builder.AddAppLogoOverride(new Uri(avatar), ToastGenericAppLogoCrop.Circle);

            var canReply = App.Chat?.SendBlockedReason(conv) == null;
            if (canReply)
            {
                builder.AddInputTextBox("reply", "Yanıt yazın…")
                    .AddButton(new ToastButton()
                        .SetContent("Yanıtla")
                        .AddArgument("action", "reply")
                        .AddArgument("conv", conv.Id)
                        .AddArgument("inst", AppPaths.Profile)
                        .SetTextBoxId("reply")
                        .SetBackgroundActivation());
            }
            builder.AddButton(new ToastButton()
                .SetContent("Okundu")
                .AddArgument("action", "markread")
                .AddArgument("conv", conv.Id)
                .AddArgument("inst", AppPaths.Profile)
                .SetBackgroundActivation());

            if (!_settings.NotificationSound) builder.AddAudio(new ToastAudio { Silent = true });

            builder.Show(toast =>
            {
                toast.Tag = TagFor(conv.Id);
                toast.Group = ToastGroup;
            });
        }
        catch (Exception ex)
        {
            Log.Error("Toast gösterilemedi", ex);
            Fallback?.Invoke(title, body);
        }
    }

    public void ShowInfo(string title, string text)
    {
        try
        {
            new ToastContentBuilder().AddText(title).AddText(text).AddAudio(new ToastAudio { Silent = true }).Show();
        }
        catch (Exception ex)
        {
            Log.Error("Toast gösterilemedi", ex);
            Fallback?.Invoke(title, text);
        }
    }

    /// <summary>
    /// Oturum kapanırken: bildirim merkezindeki mesajları ve bildirim görsellerini siler;
    /// böylece cihazda profile ait hiçbir şey kalmaz.
    /// </summary>
    public static void ClearAll()
    {
        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch (Exception ex)
        {
            Log.Error("Bildirim geçmişi temizlenemedi", ex);
        }
        try
        {
            if (Directory.Exists(AppPaths.TempDir)) Directory.Delete(AppPaths.TempDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Error("Geçici dosyalar silinemedi", ex);
        }
    }

    public static void ClearFor(string convId)
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove(TagFor(convId), ToastGroup);
        }
        catch
        {
        }
    }
}
