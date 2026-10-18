const API = "http://127.0.0.1:5757";
const defaults = { enabled: true, showOriginal: false, fontSize: 26, offset: 0, direction: "en-tr" };
const $ = (id) => document.getElementById(id);
const esc = (t) => String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ---------- Ayarlar ----------
chrome.storage.sync.get(defaults).then((v) => {
  for (const [k, val] of Object.entries(v)) {
    const el = $(k);
    if (!el || k === "direction") continue;
    if (el.type === "checkbox") el.checked = val;
    else el.value = val;
    el.addEventListener("input", () => {
      chrome.storage.sync.set({ [k]: el.type === "checkbox" ? el.checked : Number(el.value) });
    });
  }
  setDirection(v.direction, false);
});

function setDirection(d, save = true) {
  for (const b of $("direction").querySelectorAll("button")) b.classList.toggle("on", b.dataset.v === d);
  if (save) chrome.storage.sync.set({ direction: d });
}
$("direction").addEventListener("click", (e) => {
  const v = e.target?.dataset?.v;
  if (v) setDirection(v);
});

// ---------- Uygulama durumu ----------
async function refresh() {
  const box = $("status");
  try {
    const h = await (await fetch(API + "/health", { cache: "no-store" })).json();
    const asr = h.asr.ready ? `<span class="ok">● Konuşma tanıma hazır</span>`
              : h.asr.error ? `<span class="bad">● ${esc(h.asr.error)}</span>`
              : `<span class="muted">● Konuşma tanıma hazırlanıyor...</span>`;
    const errs = h.translation.errors || {};
    const dirLine = (key, label, ready) =>
      ready ? `<span class="ok">● ${label} çeviri hazır</span>`
      : errs[key] ? `<span class="bad">● ${label} çeviri modeli yok</span>`
      : `<span class="muted">● ${label} çeviri ilk kullanımda yüklenir</span>`;
    const mt = dirLine("en-tr", "EN→TR", h.translation.enTr) + "<br>" + dirLine("tr-en", "TR→EN", h.translation.trEn);
    const dl = h.downloads ? `<br><span class="muted small">İndiriliyor: ${esc(h.downloads)}</span>` : "";
    box.innerHTML = `${asr}<br>${mt}<br><span class="muted small">${esc(h.asr.description)} · sürüm ${esc(h.version)}</span>${dl}`;
  } catch {
    box.innerHTML = `<span class="bad">● Kick Çeviri uygulaması çalışmıyor</span><br>
      <span class="muted small">Başlat menüsünden "Kick Çeviri"yi açın. Saatin yanında simgesi görünür.</span>`;
  }
}

refresh();
setInterval(refresh, 2000);
