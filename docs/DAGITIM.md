# GitHub ile dağıtım

Bu rehber, uygulamayı GitHub üzerinden herkese ücretsiz dağıtmanın adımlarını anlatır. İlk kurulum 1–2 saat sürer. Sonraki her sürüm birkaç dakikalık iştir.

**Neden GitHub:** Açık kaynak projeler için her şey ücretsizdir:
- Kodun barındırılması
- İndirme trafiği (sınır yok, dosya başına 2 GB'a kadar)
- Kurulum dosyasını otomatik derleyen sunucular (GitHub Actions)
- Hata bildirimleri (Issues)

```
Siz: kodu gönder ──▶ GitHub deposu ──(v1.0.0 etiketi)──▶ GitHub Actions kurulum dosyasını derler
                                                              │
Kullanıcı ◀──── Releases sayfası: KickCeviri-Setup.exe ◀──────┘
Uygulama  ◀──── Releases "modeller-v1": çeviri modelleri (ilk açılışta iner)
```

---

## 1. Hesap ve depo

1. **github.com** adresinden ücretsiz hesap açın. Kullanıcı adınız bağlantılarda görünecek (ör. `github.com/haluk/kick-ceviri`).
2. Visual Studio'da projeyi açın ve menüden **Git → Git Deposu Oluştur...** (Create Git Repository) seçin.
   - Sol tarafta **GitHub** seçili olsun, gerekirse GitHub hesabınızla giriş yapın.
   - Depo adı: `kick-ceviri`
   - Açıklama: `Kick yayınlarına anlık Türkçe ⇄ İngilizce altyazı`
   - **Özel (Private)** kutusunun **işaretli olmadığından** emin olun. Depo herkese açık olmalı.
   - **Oluştur ve Gönder** (Create and Push).
3. Tarayıcıda deponuzu açın. Ana sayfada README görünüyorsa tamamdır.

> `bin`, `obj`, `dist` klasörleri `.gitignore` sayesinde gönderilmez. Depoya sadece kaynak kod gider.

---

## 2. Çeviri modellerini Releases'e yükle

Bu adım bir kez yapılır. Modeller değişmedikçe tekrarlanmaz.

**a) Modelleri hazırlayın** (Python 3.10+ gerekir, 20–40 dakika sürer):

```powershell
cd <proje klasörü>
powershell -ExecutionPolicy Bypass -File tools\build-models.ps1
```

Sonunda `dist\models` klasöründe 3 dosya olur: `opus-mt-en-tr.zip`, `opus-mt-tr-en.zip`, `models.json`.

**b) GitHub'a yükleyin:**
1. Depo sayfasında sağdaki **Releases → Create a new release** (veya *Draft a new release*) bağlantısına tıklayın.
2. **Choose a tag** kutusuna `modeller-v1` yazın ve **Create new tag** seçin.
3. Başlık: `Çeviri modelleri v1`
4. Açıklama: `Uygulamanın otomatik indirdiği çeviri modelleri. Elle indirmenize gerek yok.`
5. 3 dosyayı **Attach binaries** alanına sürükleyin.
6. **Set as a pre-release** kutusunu **işaretleyin.** Bu önemli: işaretlemezseniz "Son sürümü indir" bağlantısı kurulum dosyası yerine bu modellere gider.
7. **Publish release**.

---

## 3. Model adresini uygulamaya yaz

`src\KickTranslator.Service\appsettings.json` dosyasında `KULLANICI_ADINIZ` yerine GitHub kullanıcı adınızı yazın:

```json
"TranslationModelsBaseUrl": "https://github.com/haluk/kick-ceviri/releases/download/modeller-v1/",
```

Depo adını `kick-ceviri` dışında bir şey yaptıysanız onu da değiştirin.

Kontrol: adresin sonuna `models.json` ekleyip tarayıcıda açın. Dosya inmeli.

Değişikliği gönderin: Visual Studio → **Git → Commit or Stash** → mesaj: `Model adresi` → **Commit All and Push**.

---

## 4. İlk sürümü yayınla

Sürüm çıkarmak için koda bir **etiket** (tag) eklemek yeterli. Gerisini GitHub Actions yapar: kodu derler, ffmpeg'i indirir, kurulum dosyasını üretir ve Releases sayfasına koyar.

**Komut satırıyla** (proje klasöründe PowerShell):

```powershell
git tag v1.0.0
git push origin v1.0.0
```

**Visual Studio ile:**
1. **Git → Git Deposunu Görüntüle** (View Git Repository) penceresini açın.
2. En üstteki commit'e sağ tıklayın → **Yeni Etiket** (New Tag) → `v1.0.0`.
3. **Git → Gönder** (Push). Etiketin gitmesi için Git ayarlarında *Push tags* seçeneği açık olmalı. Emin değilseniz komut satırı yolunu kullanın.

**Takip edin:** Depo sayfasında **Actions** sekmesinde "Sürüm derle" işi çalışır (5–10 dakika).
- Yeşil tik çıkarsa **Releases** sayfasında `Kick Çeviri 1.0.0` ve içinde `KickCeviri-Setup-1.0.0.exe` görünür.
- Kırmızı çarpı çıkarsa işe tıklayın, kırmızı adımı açın ve son satırları bana gönderin.

> **Önce denemek isterseniz:** Actions → *Sürüm derle* → **Run workflow** seçin. Sürüm yayınlamadan sadece derler. Kurulum dosyası işin sayfasında en altta *Artifacts* bölümünden iner.

---

## 5. Temiz bir bilgisayarda dene

Kendi bilgisayarınızda her şey zaten kurulu olduğu için gerçek kullanıcı deneyimini göremezsiniz. Bunun için Windows Sandbox'ı kullanın:

1. Başlat → **Windows özelliklerini aç veya kapat** → **Windows Sandbox** kutusunu işaretleyin, yeniden başlatın. Bu özellik Windows Pro'da var.
2. Sandbox'ı açın, içinde Edge ile Releases sayfasından kurulum dosyasını indirip kurun.
3. Akışı baştan sona deneyin: ilk adımlar sayfası → eklenti → Kick'te video.

Sandbox'ta ekran kartı olmadığı için uygulama CPU modunda çalışır. Bu da APU'lu bilgisayarlardaki davranışı görmek için iyi bir test olur.

---

## 6. Paylaş

Kullanıcılara vereceğiniz tek bağlantı şu:

```
https://github.com/haluk/kick-ceviri/releases/latest
```

Bu bağlantı her zaman en son sürüme gider. Depo ana sayfası (README) zaten indirme ve kurulum anlatımını içeriyor.

**Deponun bulunmasını kolaylaştırmak için** ana sayfada sağdaki ⚙ (About) simgesine tıklayın:
- **Topics:** `kick`, `altyazi`, `ceviri`, `whisper`, `subtitles`, `translation`, `turkish`
- **Website:** releases/latest bağlantısı

---

## Yeni sürüm çıkarma

1. Kodda değişiklik yapın, commit edip gönderin.
2. Yeni etiket gönderin:
   ```powershell
   git tag v1.0.1
   git push origin v1.0.1
   ```
3. Actions yeni sürümü derleyip yayınlar. Sürüm notları, son sürümden beri yapılan commit'lerden otomatik oluşturulur. İsterseniz Releases sayfasından düzenleyebilirsiniz.

**Sürüm numarası önerisi:**

| Değişiklik | Örnek |
|---|---|
| Hata düzeltme | 1.0.0 → 1.0.1 |
| Yeni özellik | 1.0.1 → 1.1.0 |
| Büyük değişiklik | 1.1.0 → 2.0.0 |

**Eklenti dosyaları değiştiyse** sürüm notuna şunu ekleyin: *"Güncelledikten sonra `chrome://extensions` sayfasında Kick Çeviri'nin ↻ simgesine basın."* Kurulum dosyaları yeniler, ama Chrome eklentiyi kendiliğinden yeniden yüklemez.

**Modelleri değiştirirseniz:** Yeni bir `modeller-v2` sürümü açın (yine pre-release olarak), yeni 3 dosyayı yükleyin ve `appsettings.json` içindeki adresi `modeller-v2` yapın. `modeller-v1`'i silmeyin, eski sürümü kullananlar hâlâ ona ihtiyaç duyar. Yeni sürümü kuran kullanıcıların uygulaması, `models.json` içindeki sha256 değişmişse yeni modeli arka planda kendiliğinden indirir ve bir sonraki açılışta kullanır.

**Disk temizliği:** Uygulama her açılışta kullanılmayan dosyaları kendiliğinden siler: yarım kalmış indirmeler, artık kullanılmayan konuşma tanıma modelleri (ör. donanım değiştiyse), eski LLM modeli ve sunucu sürümleri. Ek bir şey yapmanız gerekmez.

---

## Bilinmesi iyi olanlar

- **Güvenlik önlemleri:** Yerel servis sadece bu bilgisayardan, sadece kendi eklentisinden (kimliği `manifest.json` içindeki `key` ile sabit) ve şifreli (https) site sayfalarından gelen bağlantıları kabul eder. LLM sunucusu her açılışta yeni bir erişim anahtarıyla başlar ve uygulama kapanınca Windows tarafından kapatılır. İndirilen program ve modellerin sha256 değerleri kodda sabittir; uymayan dosya kullanılmaz. LLM sunucusunun ya da modelin adresini `appsettings.json`'da değiştirirseniz `LlmServerSha256` / `LlmModelSha256` değerlerini de güncelleyin (değer, GitHub sürüm sayfasında dosyanın yanında, Hugging Face'te dosya sayfasında yazar).
- **Eklenti kimliği:** `manifest.json` içindeki `key` alanını silmeyin ya da değiştirmeyin; servis sadece bu kimlikteki eklentiyle konuşur.

- **SmartScreen uyarısı:** İmzasız dosyalarda Windows ilk başta uyarı gösterir. README ve sürüm notları kullanıcıya nasıl geçileceğini anlatıyor. Ücretsiz bir seçenek olarak açık kaynak projelere kod imzalama hizmeti veren **SignPath Foundation**'a başvurabilirsiniz. Onaylanırsa uyarı zamanla kalkar.
- **Antivirüs yanlış alarmı:** Az indirilen yeni programlar bazen işaretlenebilir. Olursa dosyayı Microsoft'a "yanlış pozitif" olarak bildirebilirsiniz: Microsoft Security Intelligence → *Submit a file*.
- **Kick markası:** Proje Kick'in resmi ürünü değil. README'nin sonunda bunu belirten bir not var. Kick logosunu simgede veya sayfalarda kullanmayın.
- **Lisanslar:** Kod MIT lisanslı (`LICENSE`). Modeller ve FFmpeg'in kaynak gösterimi `docs\LISANSLAR.txt` dosyasında ve kurulumla birlikte dağıtılıyor. Ek bir şey yapmanız gerekmez.
- **Maliyet:** Açık depolarda GitHub Actions ve indirme trafiği ücretsizdir.
