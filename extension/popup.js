const API = "http://127.0.0.1:5757";
const defaults = { enabled: true, showOriginal: false, fontSize: 26, offset: 0, direction: "en-tr", engine: "auto", latency: "normal" };
const $ = (id) => document.getElementById(id);
const esc = (t) => String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ---------- Ayarlar ----------
chrome.storage.sync.get(defaults).then((v) => {
  for (const [k, val] of Object.entries(v)) {
    const el = $(k);
    if (!el || k === "direction" || k === "engine" || k === "latency") continue;
    if (el.type === "checkbox") el.checked = val;
    else el.value = val;
    el.addEventListener("input", () => {
      chrome.storage.sync.set({ [k]: el.type === "checkbox" ? el.checked : Number(el.value) });
    });
  }
  setDirection(v.direction, false);
  setEngine(v.engine, false);
  setSeg("latency", v.latency, false);
});

function setSeg(id, val, save = true) {
  for (const b of $(id).querySelectorAll("button")) b.classList.toggle("on", b.dataset.v === val);
  if (save) chrome.storage.sync.set({ [id]: val });
}
$("latency").addEventListener("click", (e) => {
  const v = e.target?.dataset?.v;
  if (v) setSeg("latency", v);
});

function setEngine(e, save = true) {
  for (const b of $("engine").querySelectorAll("button")) b.classList.toggle("on", b.dataset.v === e);
  if (save) chrome.storage.sync.set({ engine: e });
}
$("engine").addEventListener("click", (e) => {
  const v = e.target?.dataset?.v;
  if (v) setEngine(v);
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
    const L = h.translation.llm || {};
    const llmText = {
      ready: `<span class="ok">● Kaliteli çeviri (LLM) hazır</span>`,
      downloading: `<span class="muted">● Kaliteli çeviri indiriliyor...</span>`,
      starting: `<span class="muted">● Kaliteli çeviri başlatılıyor...</span>`,
      failed: `<span class="bad">● Kaliteli çeviri açılamadı: ${esc(L.error || "")}</span>`,
      notstarted: L.recommended ? `<span class="muted">● Kaliteli çeviri ilk kullanımda indirilir</span>`
                                : `<span class="muted">● Kaliteli çeviri: bu bilgisayarda yavaş olabilir</span>`,
    }[L.state] || "";
    const dl = h.downloads ? `<br><span class="muted small">İndiriliyor: ${esc(h.downloads)}</span>` : "";
    box.innerHTML = `${asr}<br>${mt}${llmText ? "<br>" + llmText : ""}<br><span class="muted small">${esc(h.asr.description)}${h.asr.model ? " · " + esc(h.asr.model.replace(/^ggml-|\.bin$/g, "")) : ""} · sürüm ${esc(h.version)}</span>${dl}`;
  } catch {
    box.innerHTML = `<span class="bad">● Kick Çeviri uygulaması çalışmıyor</span><br>
      <span class="muted small">Başlat menüsünden "Kick Çeviri"yi açın. Saatin yanında simgesi görünür.</span>`;
  }
}

refresh();
setInterval(refresh, 2000);
