# NetChat 26

Yerel ağdaki diğer cihazlarla sunucusuz, şifreli mesajlaşma. Windows 10 ve Windows 11 için.

## Özellikler

- **Sunucu gerekmez:** Aynı ağdaki NetChat kullanıcıları otomatik olarak bulunur ve "Ağdaki kişiler" listesinde görünür.
- **Şifreli profiller:** Her profil, şifreyle korunan tek bir `.nprofile` dosyasıdır. Bilgisayarda ya da seçtiğiniz bir klasörde (ör. USB bellek) saklanabilir ve başka bir bilgisayarda açılabilir.
- **Kilit (Ctrl+L):** Oturumu kapatıp giriş ekranına döner.
- **Sohbet:** Bire bir ve grup sohbetleri, yanıtlama, ifade bırakma, gruplarda @etiketleme, renkli emoji, okundu bilgisi.
- **Dosya gönderme:** Ekle düğmesiyle ya da sohbete sürükleyip bırakarak.
- **Gruplar:** Yöneticiler, susturma, üye ekleme/çıkarma, "yalnızca yöneticiler yazabilir" gibi ayarlar. Grup bilgileri yönetici çevrimdışıyken de üyeler arasında güncel kalır.
- **Çevrimdışı kişiler:** Çevrimdışı birine gönderilen mesajlar, o kişi ağa bağlandığında iletilir.
- **Gizlilik:** Çevrimiçi durumu, son aktiflik, okundu bilgisi, sizi gruplara kimlerin ekleyebileceği ve mesaj geçmişinin saklanması ayarlanabilir. Engelleme ve sessize alma desteklenir.
- **Otomatik güncelleme:** Uygulama açılışta yeni bir sürüm olup olmadığını denetler; varsa giriş ekranında sorar, indirip kendini günceller ve yeniden başlar.
- **Arka planda çalışma:** Pencere kapatılınca uygulama bildirim alanında çalışmaya devam eder; yeni mesajlarda Windows bildirimi gösterilir.

## Kurulum

[Releases](https://github.com/GokdenizGunay/NetChat/releases) sayfasından `NetChat.exe` dosyasını indirip çalıştırın. Kurulum gerekmez.

- **SmartScreen:** "Windows bilgisayarınızı korudu" uyarısı çıkarsa *Ek bilgi › Yine de çalıştır*'a tıklayın.
- **Güvenlik duvarı:** İlk açılışta Windows ağ erişimi için izin isteyebilir. Diğer cihazların sizi görebilmesi için **özel ağlarda** izin verin.

## Kaynak koddan derleme

Gereksinim: .NET 10 SDK.

```bash
dotnet run --project src/NetChat/NetChat.csproj
```

Tek dosyalık sürüm:

```bash
dotnet publish src/NetChat/NetChat.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

## Kullanılan kütüphaneler

- [WPF-UI](https://github.com/lepoco/wpfui)
- [Emoji.Wpf](https://github.com/samhocevar/emoji.wpf)
- [Microsoft.Toolkit.Uwp.Notifications](https://learn.microsoft.com/windows/apps/design/shell/tiles-and-notifications/send-local-toast)
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/)

## Lisans

MIT — ayrıntılar için [LICENSE](LICENSE).
