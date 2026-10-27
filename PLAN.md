# RadialShortcut — Proje Planı

Windows için, **sağ tuşa basılı tutunca** imlecin olduğu yerde açılan, yöne kaydırıp bırakınca aksiyon çalıştıran radial (pasta) menü.

---

## 1. Hedefler ve ilkeler

- **Normal sağ tık asla bozulmamalı.** Kısa sağ tık her yerde eskisi gibi çalışır, menü sadece *basılı tutunca* açılır.
- **Hızlı:** Halka 16 ms (1 kare) içinde ekrana gelmeli. Boştayken CPU ~%0, RAM < 60 MB.
- **Kas hafızası:** Kullanıcı halkaya bakmadan "sağ tık + yukarı kaydır" ile işini yapabilmeli. Yönler sabit kalır.
- **Yönetici izni gerekmez,** kullanıcı başına kurulur.
- **Tek JSON ayar dosyası,** elle de düzenlenebilir.
- **Telemetri yok,** her şey yerelde.

---

## 2. Teknoloji

| Katman | Seçim | Neden |
|---|---|---|
| Dil / runtime | C# · .NET 10 (LTS) | Win32 erişimi kolay, hızlı başlar. .NET 8 desteği Kasım 2026'da bittiği için 10 |
| UI | WPF | Şeffaf, katmanlı pencere ve vektör çizim için olgun |
| Girdi | `SetWindowsHookEx(WH_MOUSE_LL / WH_KEYBOARD_LL)` | Global sağ tuş yakalama |
| Girdi gönderme | `SendInput` | Kısayol ve tıklama enjeksiyonu |
| Ayar | `System.Text.Json` | Ek bağımlılık gerektirmez |
| Tepsi ikonu | `H.NotifyIcon.Wpf` | WPF'de tray desteği |
| Dağıtım | Tek dosya self-contained exe + Inno Setup, güncelleme için Velopack | MSIX imza ister, sonraya |

---

## 3. Mimari

```
RadialShortcut.sln
├─ src/RadialShortcut.App          WPF: tray, halka penceresi, ayarlar penceresi
├─ src/RadialShortcut.Core         Model, ayar, aksiyon motoru (UI'dan bağımsız)
├─ src/RadialShortcut.Input        Hook'lar, jest durum makinesi, SendInput
└─ tests/RadialShortcut.Tests      Durum makinesi, açı→dilim, ayar testleri
```

**Akış:**
`MouseHook` → `GestureStateMachine` → (halka aç) → `RingWindow` → (seçim) → `ActionExecutor`

- Hook kendi thread'inde, kendi mesaj döngüsüyle çalışır. Callback'ler mikro saniyeler içinde dönmeli, yoksa Windows hook'u sessizce düşürür (`LowLevelHooksTimeout`). Ağır işler kuyruğa atılıp UI thread'ine gönderilir.

---

## 4. Sağ tuş jesti — en kritik kısım

### Durum makinesi

```
Idle ──RButtonDown──▶ ön kontroller (hepsi önbellekten, < 50 µs):
        enjekte mi? / hariç uygulama mı? / hedef pencere yönetici mi? / tam ekran mı?
        → herhangi biri evet ise: olayı GEÇİR → Passthrough
        → hepsi hayır ise: olayı YUT, zaman + konum kaydet, HoldDelay zamanlayıcısını kur → Pending

Pending:  (fare hareketleri geçer, sadece sağ tuş tutuluyor)
  • RButtonUp, hareket < DragSlop
        → "Normal sağ tık": orijinal konumda RDown+RUp oynat → Idle
  • Hareket ≥ DragSlop (zamanlayıcıdan önce)
        → "Normal sağ sürükleme": orijinal konumda RDown, ardından mevcut
          konuma bir hareket olayı oynat → Passthrough
  • Başka tuş (sol/orta) basıldı
        → RDown'u oynat, olayı geçir → Passthrough
  • Zamanlayıcı doldu (HoldDelay, varsayılan 180 ms)
        → RingOpen

RingOpen:  (fare hareketleri halkaya gider, uygulamaya gitmez; tekerlek yutulur)
  • Hareket → seçili dilimi güncelle
  • Sol tık / Esc → iptal → Swallowing
  • RButtonUp → seçili dilim varsa çalıştır, merkezdeyse iptal → Idle

Swallowing:  (iptal edildi ama sağ tuş hâlâ basılı)
  • RButtonUp'ı yut → Idle   (aksi halde uygulamaya "sahipsiz" bir RUp gider)

Passthrough:
  • RButtonUp'a kadar her şeyi geçir → Idle
```

- **Zamanlayıcı şart:** Kullanıcı fareyi hiç kıpırdatmadan tutarsa hook'a hiç olay gelmez. HoldDelay, hook thread'inde bir timer ile ölçülür, olay beklenmez.

### Detaylar

- **Kendi enjeksiyonunu tanıma:** `SendInput` ile gönderilen olaylarda `dwExtraInfo = 0x52534843` ("RSHC") imzası olur. Hook bu olayları yutmaz.
- **Varsayılan değerler** (hepsi ayarlanabilir): `HoldDelay = 180 ms`, `DragSlop = 6 px`, `DeadZone = 22 px` (merkez yarıçapı, bırakınca iptal).
- **Hariç tutulanlar:**
  - Kullanıcı listesi (`*.exe`)
  - Tam ekran oyunlar (`SHQueryUserNotificationState` veya pencere/monitör boyutu karşılaştırması)
  - Uzak masaüstü / VM pencereleri
- **Geçici kapatma:** Kısayol (`Ctrl+Alt+R`) ve tepsi menüsünden "Duraklat".
- **Yönetici pencereleri (UIPI):** Normal yetkiyle çalışan uygulama, yönetici olarak çalışan bir pencereye girdi enjekte edemez. Sağ tıkı önce yutup sonra tekrar oynatmaya çalışırsak, **o pencerelerde sağ tık tamamen bozulur.** Bu yüzden hedef pencerenin yetki seviyesi RDown anında kontrol edilir (`OpenProcessToken` → `TokenIntegrityLevel`, sonuç PID başına önbelleğe alınır). Pencere yönetici ise olay hiç yutulmaz, menü o pencerede açılmaz. Ayarlarda "Yönetici olarak başlat" seçeneği sunulur (Görev Zamanlayıcı ile, UAC sormadan).
- **Hook içinde ağır iş yok:** Süreç adı, yetki seviyesi ve hariç tutma sonucu `hwnd → PID → bilgi` olarak önbelleklenir. `OpenProcess` gibi çağrılar sadece önbellekte olmayan ilk durumda yapılır.
- **Sol elle kullanım:** Windows'ta "birincil tuşu değiştir" açıksa hook mantıksal mı fiziksel mi tuş görüyor, test edilip ona göre eşlenecek.
- **Alternatif tetikleyiciler (v1.1):** Orta tuş, X1/X2 yan tuşları, klavye (örn. `Ctrl+Space` basılı tut).

---

## 5. Seçim mantığı

- `açı = atan2(dy, dx)`, 0° yukarıda olacak şekilde döndürülür.
- `dilim = floor((açı + dilimAçısı/2) / dilimAçısı) mod N`
- Merkezde, yani `DeadZone` içindeyken seçim yok.
- **Referans noktası basış noktasıdır, halka merkezi değil.** Açı, sağ tuşa basılan noktaya göre hesaplanır. Ekran kenarında halka içeri kaydırılsa bile seçim bozulmaz, sadece görsel konum değişir.
- **Mesafe sınırı yok:** İmleç halkanın dışına çıksa bile yön geçerlidir.
- **Flick (halka görünmeden kaydırıp bırakma) v1'de yok.** Normal sağ sürüklemeyle karıştırılması kaçınılmaz. Halka açıldıktan sonra hızlı kaydırıp bırakmak zaten aynı hissi verir.
- **Alt halka (nested):** Alt halka içeren dilim seçiliyken 350 ms beklenirse alt halka açılır. Mesafe sınırı olmadığı için "kenara gelince aç" kullanılmaz. Alt halkanın referans noktası, açıldığı andaki imleç konumudur. Merkeze dönüp beklemek bir üst halkaya döner.
- **Klavyeyle seçim:** Halka açıkken `1–9`, `0` tuşları dilimi seçer, `Esc` iptal eder. Klavye hook'u sadece halka açıkken tuş yutar.

---

## 6. Görünüm ve tasarım

### Halka anatomisi
```
            ┌───────┐
        ╱  Kopyala  ╲            Dış yarıçap  : 140 px (ayarlanabilir 100–220)
   Geri │    ⬆      │ İleri     İç yarıçap   : 46 px
        │  ( ✕ )    │           Dilim boşluğu: 3 px
        ╲   Yapıştır ╱           İkon: 24 px, etiket: 12 px
            └───────┘
```
- **Dilimler:** Halka segmentleri (donut). Her dilimde ikon, altında kısa etiket.
- **Merkez:** Seçili aksiyonun tam adı ve kısayolu. Boştayken uygulamanın ikonu ve halka adı.
- **Yön göstergesi:** Merkezden seçili dilime doğru ince bir yay ya da ok.
- **Seçili dilim:** Vurgu rengiyle dolar, %6 büyür, gölgesi belirginleşir.
- **Ekran kenarı:** Halka ekrana sığmıyorsa pencere içeri kaydırılır. İmleç `SetCursorPos` ile taşınmaz, seçim basış noktasına göre hesaplanır (bkz. bölüm 5). DPI farklı birden fazla monitör desteklenir (Per-Monitor V2).

### Animasyonlar (kapatılabilir, Windows'ta "animasyonları azalt" açıksa kapalı)
- **Açılış:** 90 ms ölçek 0.85→1, opaklık 0→1, ease-out
- **Seçim değişimi:** 60 ms
- **Kapanış:** Seçimle olursa seçili dilim 120 ms "pulse", diğerleri solar. İptal ile olursa 70 ms solma.

### Temalar
- **Varsayılanlar:** Açık, Koyu, Sistemi takip et, Akrilik/Mica arka plan (Windows 11).
- **Tema yapısı:** JSON'da `background`, `slice`, `sliceHover`, `accent`, `text`, `shadow`, `cornerRadius`, `opacity`.
- **Vurgu rengi:** Varsayılan olarak Windows vurgu rengi.
- **İkonlar:** Segoe Fluent Icons glifleri, uygulamanın kendi exe ikonu (`SHGetFileInfo`) veya kullanıcının seçtiği PNG/SVG.

### Ayarlar penceresi (WPF, Fluent stil)
- **Halkalar:** Solda halka listesi (Genel + uygulamaya özel olanlar). Ortada canlı halka önizlemesi, dilime tıklayınca düzenlenir. Sağda dilim özellikleri paneli. Sürükle-bırak ile dilim yeri değiştirilir.
- **Tetikleyici:** Tuş seçimi, HoldDelay, DragSlop, hariç uygulamalar.
- **Görünüm:** Tema, boyut, animasyonlar, etiket göster/gizle.
- **Genel:** Windows ile başlat, dil (TR/EN), ayarları içe/dışa aktar, ayar klasörünü aç.

---

## 7. Araçlar (ana odak)

Uygulamanın değeri basit kısayollarda değil, **ekran araçlarında**.

**Varsayılan 8 dilim** (yukarıdan saat yönünde): Metin al (OCR) · Renk seçici · Ekrandan çevir · Kırp · Kaydırmalı görüntü · Ekran kaydı · Görselle ara · QR oku.
**12 dilimde ek olarak:** Ölç · Üstte tut · Web ara · Hesapla.

| Araç | Nasıl çalışır | Teknik | v1 |
|---|---|---|---|
| **Ekrandan metin (OCR)** | Ekran kararır, bölge seçilir, metin panoya kopyalanır, küçük bir önizleme kartı çıkar | `Windows.Media.Ocr` (Windows'un dahili OCR'ı, Türkçe dil paketi varsa Türkçe de okur), yoksa Tesseract yedek | ✅ |
| **Renk seçici** | İmlecin yanında 8× büyüteç ve canlı HEX / RGB / HSL. Tıklayınca kopyalar, `Tab` ile format değişir, `Shift` ile son renkler | Ekran DC'sinden piksel okuma (`BitBlt`), DPI uyumlu | ✅ |
| **Bölge ekran görüntüsü** | Bölge, pencere veya tam ekran seçimi. Panoya ve `Resimler\RadialShortcut` klasörüne kaydeder | `Graphics.CopyFromScreen` / DXGI | ✅ |
| **Ekrandan çevir** | Bölge seç, metin okunur, seçilen bölgenin yanında çeviri kartı çıkar (kaynak dil otomatik, hedef varsayılan Türkçe). Kartta "Kopyala" ve "Orijinal" | OCR + çeviri. Varsayılan: kullanıcının girdiği DeepL Free API anahtarı (ayda 500 bin karakter ücretsiz) ile kart içinde gösterilir. Anahtar yoksa Google Çeviri sayfası metinle açılır. ⚠️ İnternete metin gönderir | ✅ |
| **Kaydırmalı ekran görüntüsü** | Pencere ya da bölge seçilir, uygulama sayfayı kendisi aşağı kaydırır, kareleri birleştirip tek uzun görsel yapar. `Esc` ile istenen yerde durdurulur | Yakala → `SendInput` ile tekerlek kaydırma → bekle → yakala. Kareler, örtüşen satırların piksel karşılaştırmasıyla hizalanır. Sabit başlık/alt bilgi (sticky header) algılanıp tekrar edilmez. ⚠️ En zor araç: bazı uygulamalar düzensiz kaydırır, bu durumda kullanıcı elle kaydırabilir | ✅ (Faz 3'ün sonu) |
| **Ekran kaydı** | Bölge veya pencere seçilir, 3-2-1 sonra kayıt başlar. Köşede küçük durdur butonu ve süre. Çıktı MP4 ya da GIF, opsiyonel sistem sesi + mikrofon | `Windows.Graphics.Capture` + Media Foundation H.264 kodlayıcı (MP4). GIF için önce MP4 alınır, sonra palet optimizasyonuyla dönüştürülür. 30 fps, GIF için 15 fps ve en fazla 30 sn | ✅ |
| **Görselle ara** | Bölge seç, tarayıcıda Google Lens sonuçları açılır. Ayarlardan Bing Görsel Arama veya Yandex seçilebilir | Görüntü Lens'in yükleme adresine gönderilir ve dönen sonuç sayfası açılır. Bu adres resmi bir API değil, değişebilir. Çalışmazsa yedek yol: görüntü panoya kopyalanır, lens.google.com açılır ve kullanıcı `Ctrl+V` yapar. ⚠️ İnternete görüntü gönderir | ✅ |
| **QR / barkod oku** | Bölge seç, içerik panoya. URL ise "Aç" butonu | ZXing.Net | ✅ |
| **Ekran cetveli** | İki nokta arası piksel mesafesi, yatay/dikey kılavuz | Overlay pencere | ✅ |
| **Seçili ifadeyi hesapla** | `12*4+3` seçip çalıştırınca sonuç yerine yazılır ya da gösterilir | Basit ifade ayrıştırıcı (`eval` yok) | v1.1 |
| **Seçili metni webde ara** | Tarayıcıda arama | URL açma | v1.1 |
| **Pencereyi üstte tut** | Aç / kapat | `SetWindowPos(HWND_TOPMOST)` | ✅ |

**İnternet kullanan araçlar:** Ekrandan çevir ve görselle ara. Her biri ilk kullanımda bir kez onay ister. Diğer her şey yerelde çalışır.

**Ortak "yakalama katmanı":** OCR, çevir, kırp, kaydırmalı görüntü, ekran kaydı, görselle ara ve QR aynı bölge seçme overlay'ini kullanır. Overlay tüm monitörleri kaplar, `Esc` ile iptal edilir, pencere üstüne gelince pencereyi önerir. Bir kere iyi yapılır, yedi araç birden kazanır. Faz 3'ün ana işi budur.

**Sonuç kartı:** OCR / çeviri / QR / renk sonucunda seçilen bölgenin yanında küçük bir kart çıkar: önizleme + "Kopyala" (+ çeviride "Orijinal"). Kart üzerine gelince kapanmaz, dışarı tıklayınca kapanır.

## 7b. Temel aksiyon türleri

| Tür | Örnek | v1 |
|---|---|---|
| `launch` | Uygulama/dosya/klasör aç, argümanlarla | ✅ |
| `url` | Web adresi aç | ✅ |
| `hotkey` | `Ctrl+Shift+T`, `Win+V` gönder | ✅ |
| `text` | Metin yapıştır (pano üzerinden; eski pano ~400 ms sonra geri yüklenir, çünkü hedef uygulama yapıştırmayı geç okuyabilir. Pano geçmişine düşmemesi için `ExcludeClipboardContentFromMonitorProcessing` biçimi eklenir) | ✅ |
| `media` | Oynat/duraklat, ses +/−, sessiz | ✅ |
| `window` | Küçült, büyüt, kapat, her zaman üstte, sanal masaüstü değiştir | ✅ |
| `submenu` | İç içe halka | ✅ |
| `system` | Kilitle, ekran görüntüsü (`Win+Shift+S`), uyku | ✅ |
| `explorerSelection` | Explorer'da seçili dosyalarda komut çalıştır (yolu kopyala, terminalde aç) | v1.1 |
| `macro` | Aksiyon zinciri + bekleme | v1.2 |
| `script` | PowerShell / komut çalıştır | v1.2 |

**Odak kuralı:**
- Halka penceresi `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST` ile açılır, odağı hiç almaz. Sağ tık da yutulduğu için odak normalde hiç değişmez.
- **Hedef pencere = sağ tıkın yapıldığı pencere** (bölüm 8 ile aynı). Bu pencere o an odakta değilse (arka plandaki pencereye sağ tık), aksiyondan önce o pencere öne getirilir. Normal bir sağ tık da zaten bunu yapardı. Ön plana getirme engellenirse, kendi `SendInput` olayımızdan hemen sonra `SetForegroundWindow` çağrılır. Bu, Windows'un izin verdiği yoldur, `AttachThreadInput` hilesine gerek kalmaz.
- Halka kapanır, bir kare beklenir, sonra aksiyon çalışır. Böylece kısayol halkaya değil hedef pencereye gider.

---

## 8. Uygulamaya özel halkalar

- Sağ tıkın yapıldığı penceredeki süreç adı, imlecin altındaki pencereden alınır (`WindowFromPoint` → `GetAncestor` → `GetWindowThreadProcessId`). Odaktaki pencere kullanılmaz, çünkü kullanıcı arka plandaki bir pencereye sağ tıklamış olabilir.
- **Eşleşme sırası:**
  1. `process` + `windowTitle` regex
  2. `process`
  3. `Global` halka
- **Hazır şablonlar (v1.1):** Chrome/Edge, Explorer, VS Code, Photoshop, Office. Kullanıcı tek tıkla ekler. Şablonları kendimiz yazarız.

---

## 9. Ayar dosyası

`%APPDATA%\RadialShortcut\config.json`, dosya değişince canlı yeniden yüklenir.

```json
{
  "version": 1,
  "trigger": { "button": "Right", "holdDelayMs": 180, "dragSlopPx": 6 },
  "excludedProcesses": ["valorant.exe", "mstsc.exe"],
  "appearance": { "theme": "system", "radius": 140, "showLabels": true, "animations": true },
  "rings": [
    {
      "id": "global",
      "name": "Genel",
      "match": null,
      "slices": [
        { "label": "Kopyala",  "icon": "fluent:Copy",  "action": { "type": "hotkey", "keys": "Ctrl+C" } },
        { "label": "Yapıştır", "icon": "fluent:Paste", "action": { "type": "hotkey", "keys": "Ctrl+V" } },
        { "label": "Uygulamalar", "icon": "fluent:Apps", "action": { "type": "submenu", "ring": "apps" } }
      ]
    },
    {
      "id": "chrome",
      "name": "Chrome",
      "match": { "process": "chrome.exe" },
      "slices": [
        { "label": "Yeni sekme", "icon": "fluent:Add", "action": { "type": "hotkey", "keys": "Ctrl+T" } }
      ]
    }
  ]
}
```

- Halka başına 2–12 dilim olabilir. Varsayılan 8, yön sırası: yukarı, sağ üst, sağ, ...
- Bozuk JSON yüklenmez, son geçerli ayar kullanılır ve tepsiden uyarı gösterilir.
- Her kaydetmede `config.json.bak` alınır.

---

## 10. Yol haritası

### Faz 0 — İskelet (1 gün)
- `git init`, `.gitignore`, solution, projeler, tepsi ikonu, tek örnek kontrolü (mutex), Windows ile başlat.

### Faz 1 — Jest çekirdeği (2–3 gün) ⚠️ en riskli kısım, önce bu
- Mouse hook + durum makinesi + sağ tık replay
- Test: Explorer, Chrome, VS Code, masaüstünde sağ tık ve sağ sürükle bozulmuyor olmalı.
- Debug overlay: durum ve süreler ekranda görünür.

### Faz 2 — Halka UI (2–3 gün)
- Şeffaf, odak çalmayan pencere, donut çizimi, açı→dilim seçimi, animasyonlar, multi-monitor/DPI

### Faz 3 — Araçlar ve ayar (8–10 gün)
- Ortak bölge seçme overlay'i + sonuç kartı
- Kolaylar: OCR, renk seçici, kırp, QR, görselle ara, ekran cetveli, üstte tut
- Ekrandan çevir (OCR + DeepL / tarayıcı)
- Ekran kaydı (MP4, sonra GIF)
- Kaydırmalı ekran görüntüsü (en son, en riskli)
- Temel aksiyonlar: `launch`, `url`, `hotkey`, `text`, `submenu`
- JSON yükleme, canlı yeniden yükleme, varsayılan halka

### Faz 4 — Uygulamaya özel halkalar + hariç tutma (1–2 gün)

### Faz 5 — Ayarlar penceresi (4–5 gün)
- Görsel halka editörü, ikon seçici, tema seçimi

### Faz 6 — Cila ve dağıtım (2 gün)
- Temalar, TR/EN dil, installer, otomatik güncelleme kontrolü (GitHub Releases)

### Sonrası
- Metin dönüşümleri, Explorer seçimi, makrolar, pano geçmişi, uygulama şablonları, CLI (`radialshortcut.exe --open ring-id`)

---

## 11. Riskler

| Risk | Önlem |
|---|---|
| Sağ tık replay'i bazı uygulamalarda bozulur (oyunlar, CAD, uzak masaüstü) | Hariç tutma listesi + tam ekran algılama + hızlı duraklatma |
| Hook timeout ile Windows hook'u düşürür | Callback'te sıfır ağır iş, ayrı thread, periyodik sağlık kontrolü ve gerekirse yeniden bağlanma |
| Yönetici pencereleri (UIPI) | Geçir moduna düş, "Yönetici olarak çalıştır" seçeneği |
| Antivirüs global hook'u şüpheli bulur | Açık kaynak, ileride kod imzalama. MSIX imza gerektirdiği için v1'de zip + Inno Setup, güncellemeler için Velopack |
| Yönetici pencerelerinde sağ tık bozulur | RDown anında yetki kontrolü, yönetici pencerede hiç yutma (bölüm 4) |
| Odak geri verilemez | `AttachThreadInput` + `WS_EX_NOACTIVATE`, testlerde doğrulanacak |

---

## 12. Kapsam dışı

- RadialDeck'in kodu, ikonları, temaları ya da aksiyon listesi kopyalanmayacak. Tasarım ve içerik bize ait olacak.
