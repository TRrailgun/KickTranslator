# Kick Çeviri

**Kick yayınlarına anlık Türkçe ⇄ İngilizce altyazı.** Ücretsiz ve açık kaynak.

- 🎥 Canlı yayınlarda ve yayın tekrarlarında çalışır
- 🔁 İngilizce → Türkçe ve Türkçe → İngilizce
- 🔒 Her şey sizin bilgisayarınızda çalışır. Ses hiçbir sunucuya gönderilmez, hesap gerekmez.
- 💻 NVIDIA ekran kartlarında çok hızlı çalışır. APU'lu ve entegre ekran kartlı bilgisayarlarda da çalışır.

## İndir

**[⬇ Son sürümü indir](../../releases/latest)** (`KickCeviri-Setup-x.y.z.exe`)

Gereksinimler: Windows 10 veya 11 (64 bit), Chrome ya da Edge, ilk kurulumda yaklaşık 1 GB indirme.

## Kurulum

1. İndirdiğiniz `KickCeviri-Setup` dosyasını çalıştırın.
   > Windows **"Bilgisayarınız korundu"** derse **Ek bilgi → Yine de çalıştır**'a basın. Bu uyarı, dosya ücretli bir sertifikayla imzalanmadığı için çıkar.
2. Kurulum bitince bir **ilk adımlar** sayfası açılır. Oradaki adımlarla tarayıcı eklentisini ekleyin:
   - `chrome://extensions` sayfasını açın.
   - Sağ üstteki **Geliştirici modu** anahtarını açın.
   - **Paketlenmemiş öğe yükle**'ye basıp sayfada yazan klasörü seçin.
3. İlk açılışta konuşma tanıma modeli arka planda iner. Bittiğinde saatin yanındaki simgeden "Hazır" bildirimi gelir.

## Kullanım

- Kick'te bir yayın ya da yayın tekrarı açın. Altyazı birkaç saniye içinde başlar.
- Çeviri yönünü, yazı boyutunu ve orijinal metni gösterme seçeneğini eklenti simgesinden değiştirin.
- Videodayken **Alt+T** altyazıyı açıp kapatır.
- Uygulama saatin yanındaki simgede çalışır. Kapatmak için simgeye sağ tıklayıp **Çıkış**'ı seçin.

## Sık sorulanlar

**Canlı yayında altyazı neden biraz geç geliyor?**
Konuşmanın söylenmesi beklenmeden çevrilemiyor. Konuşma sürerken ekranda soluk renkli bir taslak altyazı görünür, cümle bitince kesin hâli onun yerine geçer. Yayın tekrarlarında altyazı tam zamanında çıkar.

**Bilgisayarım yavaş, ne yapabilirim?**
Uygulama donanımınıza göre model seçer. Entegre ekran kartlarında daha küçük ve hızlı bir model kullanılır. Yine de geride kalırsa yayın tekrarlarında uygulama otomatik olarak ileri atlar.

**Altyazı çıkmıyor, "Videodan ses gelmiyor" yazıyor.**
Oynatıcının sesi kapalıysa açın; tarayıcı kapalı sesi yakalayamayabilir. Sorun sürerse sayfayı yenileyin ve günlük dosyasıyla hata bildirimi açın.

**Altyazı daha erken gelebilir mi?**
Eklenti simgesinden gecikmeyi **Anlık** yapın. Uygulama cümlenin bitmesini beklemeden, konuşma sürerken yaklaşık her yarım saniyede bir taslak çeviri gösterir (soluk/italik yazı). Cümle bitince kesin çeviri onun yerine geçer. Taslaklar ek işlem gerektirdiği için en iyi sonuç ekran kartlı bilgisayarlarda alınır; bilgisayar yetişemezse taslaklar kendiliğinden kapanır.

**"Hızlı" ve "Kaliteli" çeviri arasındaki fark ne?**
Eklenti simgesinden seçilir. *Hızlı*, her bilgisayarda çalışan küçük bir çeviri modelidir. *Kaliteli*, bilgisayarınızda çalışan bir yapay zekâ dil modelidir (Gemma 3 4B): konuşma dilini, argoyu ve yarım cümleleri önceki cümlelere bakarak çok daha doğal çevirir. 6 GB ve üstü ekran belleği ister, ilk seçildiğinde yaklaşık 2.5 GB indirilir. *Otomatik*, güçlü ekran kartında Kaliteli'yi, diğerlerinde Hızlı'yı kullanır; Kaliteli çeviri yetişemezse kendiliğinden Hızlı'ya geçer.

**Ekran kartım güçlü ama küçük model kullanılıyor.**
Uygulama 6 GB ve üstü ekran belleği olan kartları (NVIDIA, Intel Arc, AMD Radeon RX) güçlü sayar. Algılama yanılırsa kurulum klasöründeki `appsettings.json` içinde `"GpuTier": "high"` yapın ve uygulamayı yeniden başlatın.

**"Kick Çeviri uygulaması çalışmıyor" yazıyor.**
Başlat menüsünden **Kick Çeviri**'yi açın.

**Bir sorun buldum.**
[Hata bildirimi açın](../../issues/new/choose). Simge menüsündeki **Günlük klasörünü aç** ile bulacağınız günlük dosyasını eklerseniz çok yardımcı olur.

---

## Geliştiriciler için

```
src/KickTranslator.Service/   Windows uygulaması (.NET 8, saat yanında simge, yerel servis 127.0.0.1:5757)
extension/                    Chrome/Edge eklentisi (altyazı çizimi, canlı yayında ses yakalama)
installer/KickCeviri.iss      Kurulum dosyası (Inno Setup)
tools/build_models.py         Çeviri modellerini ONNX'e dönüştürme
build.ps1                     Kurulum dosyası üretme
.github/workflows/            Etiket gönderilince otomatik sürüm derleme
```

**Nasıl çalışır:** Yayın tekrarlarında ses, video akışından (m3u8) ffmpeg ile oynatmanın önünden çekilir. Canlı yayında ise eklenti videonun çaldığı sesi 16 kHz'e indirip uygulamaya yollar. Konuşma, Whisper ([whisper.cpp](https://github.com/ggerganov/whisper.cpp) / [Whisper.net](https://github.com/sandrohanea/whisper.net)) ile yazıya dökülür; donanıma göre CUDA, Vulkan veya CPU kullanılır. Çeviri, [OPUS-MT](https://huggingface.co/Helsinki-NLP) modelleriyle ONNX Runtime üzerinde yapılır.

**Geliştirme ortamı:** Visual Studio 2022'de `KickTranslator.sln` dosyasını açıp F5'e basın. Eklenti için `chrome://extensions` → Paketlenmemiş öğe yükle → `extension` klasörünü seçin.

**Sürüm çıkarma ve dağıtım:** [docs/DAGITIM.md](docs/DAGITIM.md)

Katkılarınızı bekliyoruz. Pull request göndermeden önce büyük değişiklikler için bir issue açmanız iyi olur.

## Lisans

Kaynak kod [MIT](LICENSE) lisanslıdır. Uygulamanın kullandığı modeller ve FFmpeg kendi lisanslarına tabidir: [docs/LISANSLAR.txt](docs/LISANSLAR.txt)

---

<sub>Bu proje Kick ile bağlantılı değildir; Kick tarafından onaylanmamış, bağımsız bir topluluk projesidir.</sub>
