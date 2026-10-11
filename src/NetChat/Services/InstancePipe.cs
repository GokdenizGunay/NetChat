using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using NetChat.Core;

namespace NetChat.Services;

/// <summary>
/// Tek örnek iletişimi: uygulama ikinci kez açıldığında (ör. bir .nprofile dosyasına çift tıklanınca)
/// isteği adlandırılmış kanal üzerinden çalışan örneğe iletir. Boş mesaj yalnızca "pencereyi göster" demektir.
/// </summary>
public static class InstancePipe
{
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    /// <summary>Başka bir örneğe aktarılan bildirim etkileşimi mesajlarının öneki.</summary>
    public const string ToastPrefix = "toast:";

    private static string NameFor(string instance) => $"{AppPaths.AppId}-{instance}-{Environment.UserName}";

    public static void Send(string message) => SendTo(AppPaths.Profile, message);

    public static void SendTo(string instance, string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", NameFor(instance), PipeDirection.Out);
            client.Connect(2000);
            // Yeni açılan örnek ön plandadır; çalışan örneğin penceresini öne getirebilmesi için izni ona devret.
            // (Aksi halde Windows pencereyi arkada bırakıp yalnızca görev çubuğunda yanıp söndürür.)
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid)) AllowSetForegroundWindow((int)pid);
            var bytes = Encoding.UTF8.GetBytes(message);
            client.Write(bytes);
            client.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("Çalışan örneğe ulaşılamadı", ex);
        }
    }

    public static void Listen(Action<string> received)
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(NameFor(AppPaths.Profile), PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var message = reader.ReadToEnd();
                    received(message);
                }
                catch (Exception ex)
                {
                    Log.Error("Örnek kanalı hatası", ex);
                    Thread.Sleep(1000);
                }
            }
        }) { IsBackground = true, Name = "InstancePipe" }.Start();
    }
}
