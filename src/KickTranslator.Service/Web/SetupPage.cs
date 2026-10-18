namespace KickTranslator.Web;

/// <summary>Kurulumdan sonra açılan yerel "ilk adımlar" sayfası (http://127.0.0.1:5757/setup).</summary>
internal static class SetupPage
{
    public const string Html = """
<!doctype html>
<html lang="tr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Kick Çeviri · Kurulum</title>
<style>
  :root { --bg:#0e0f10; --card:#17191b; --line:#26292c; --text:#eceeef; --muted:#9aa1a7; --green:#53fc18; --red:#ff6b6b; }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--text); font:15px/1.55 "Segoe UI", system-ui, sans-serif; }
  main { max-width:760px; margin:0 auto; padding:40px 20px 60px; }
  h1 { font-size:26px; margin:0 0 4px; }
  h1 b { color:var(--green); }
  .sub { color:var(--muted); margin:0 0 28px; }
  .step { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:20px 22px; margin-bottom:16px; }
  .step h2 { font-size:17px; margin:0 0 10px; display:flex; align-items:center; gap:10px; }
  .num { width:26px; height:26px; border-radius:50%; background:#24272a; color:var(--green); display:inline-flex; align-items:center; justify-content:center; font-size:14px; font-weight:700; }
  .done .num { background:var(--green); color:#000; }
  ol { margin:8px 0 12px; padding-left:22px; } li { margin:4px 0; }
  code, .path { font-family:Consolas, monospace; background:#0b0c0d; border:1px solid var(--line); border-radius:6px; padding:2px 6px; }
  .path { display:block; padding:10px 12px; margin:10px 0; word-break:break-all; }
  button { font:inherit; font-weight:600; border:0; border-radius:8px; padding:9px 16px; cursor:pointer; background:var(--green); color:#000; }
  button.ghost { background:#24272a; color:var(--text); }
  button:disabled { opacity:.5; cursor:default; }
  .row { display:flex; gap:10px; flex-wrap:wrap; align-items:center; }
  input { font:inherit; background:#0b0c0d; color:var(--text); border:1px solid var(--line); border-radius:8px; padding:9px 12px; min-width:0; flex:1; }
  .muted { color:var(--muted); } .ok { color:var(--green); } .bad { color:var(--red); }
  .status { display:grid; grid-template-columns:auto 1fr; gap:4px 12px; font-size:14px; }
  .msg { min-height:20px; margin-top:8px; font-size:14px; }
  kbd { background:#24272a; border-radius:4px; padding:1px 6px; font-family:inherit; font-size:13px; }
</style>
</head>
<body>
<main>
  <h1><b>Kick</b> Çeviri</h1>
  <p class="sub">Yayınları ve yayın tekrarlarını anlık Türkçe ⇄ İngilizce altyazıyla izleyin. Kurulumu bitirmek için tarayıcı eklentisini eklemeniz yeterli.</p>

  <section class="step" id="s1">
    <h2><span class="num">1</span> Uygulama</h2>
    <div class="status" id="status"><span class="muted">Durum alınıyor...</span></div>
  </section>

  <section class="step" id="s2">
    <h2><span class="num">2</span> Tarayıcı eklentisini ekle</h2>
    <ol>
      <li><button class="ghost" id="openExt">Eklentiler sayfasını aç</button> &nbsp;<span class="muted">(açılmazsa adres çubuğuna <code>chrome://extensions</code> yazın)</span></li>
      <li>Sağ üstteki <b>Geliştirici modu</b> anahtarını açın.</li>
      <li><b>Paketlenmemiş öğe yükle</b> düğmesine basın ve şu klasörü seçin:</li>
    </ol>
    <span class="path" id="extPath">...</span>
    <div class="row">
      <button class="ghost" id="copyPath">Yolu kopyala</button>
      <button class="ghost" id="openFolder">Klasörü aç</button>
      <span class="muted" style="font-size:13px">İpucu: klasör seçme penceresinin adres çubuğuna yolu yapıştırıp Enter'a basın.</span>
    </div>
  </section>

  <section class="step">
    <h2><span class="num">✓</span> Kullanım</h2>
    <p style="margin:0">Kick'te bir yayın veya yayın tekrarı açın; altyazı birkaç saniye içinde başlar. Dil yönünü (EN→TR / TR→EN) eklenti simgesinden seçin. Videodayken <kbd>Alt</kbd>+<kbd>T</kbd> altyazıyı açıp kapatır. Uygulama saatin yanındaki simgede çalışır.</p>
  </section>
</main>

<script>
const $ = (id) => document.getElementById(id);
const esc = (t) => String(t ?? "").replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
async function post(url, body) {
  const r = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {}) });
  let j = {}; try { j = await r.json(); } catch {}
  if (!r.ok || j.ok === false) throw new Error(j.message || ("Hata " + r.status));
  return j;
}

function trText(t) {
  const on = [];
  if (t.enTr) on.push("EN→TR");
  if (t.trEn) on.push("TR→EN");
  const errs = Object.values(t.errors || {});
  if (on.length) return '<span class="ok">' + on.join(", ") + ' hazır</span>';
  if (errs.length) return '<span class="bad">' + esc(errs[0]) + '</span>';
  return '<span class="muted">İlk kullanımda yüklenir</span>';
}

async function refresh() {
  try {
    const h = await (await fetch("/health", { cache: "no-store" })).json();
    const asr = h.asr.ready ? '<span class="ok">Hazır</span>'
              : h.asr.error ? `<span class="bad">${esc(h.asr.error)}</span>` : '<span class="muted">Hazırlanıyor...</span>';
    $("status").innerHTML = `
      <span class="muted">Durum</span><span class="ok">Çalışıyor (sürüm ${esc(h.version)})</span>
      <span class="muted">Donanım</span><span>${esc(h.asr.description)}</span>
      <span class="muted">Konuşma tanıma</span><span>${asr}</span>
      <span class="muted">Çeviri</span><span>${trText(h.translation)}</span>
      ${h.downloads ? `<span class="muted">İndiriliyor</span><span>${esc(h.downloads)}</span>` : ""}`;
    $("s1").classList.toggle("done", h.asr.ready);
    $("extPath").textContent = h.extensionPath || "(bulunamadı)";
  } catch {
    $("status").innerHTML = '<span class="bad">Uygulama çalışmıyor.</span><span class="muted">Başlat menüsünden Kick Çeviri\'yi açın.</span>';
  }
}

$("openExt").onclick = () => post("/setup/open-chrome-extensions").catch(() => {});
$("openFolder").onclick = () => post("/setup/open-extension-folder").catch(() => {});
$("copyPath").onclick = async () => {
  try { await navigator.clipboard.writeText($("extPath").textContent); $("copyPath").textContent = "Kopyalandı ✓"; }
  catch { $("copyPath").textContent = "Elle seçip kopyalayın"; }
  setTimeout(() => $("copyPath").textContent = "Yolu kopyala", 2000);
};

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}
