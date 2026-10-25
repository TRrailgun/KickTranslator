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

let currentEngine = "auto";

function setEngine(e, save = true) {
  currentEngine = e;
  if (lastHealth) renderLlm(lastHealth.translation?.llm, lastHealth.downloads);
  for (const b of $("engine").querySelectorAll("button")) b.classList.toggle("on", b.dataset.v === e);
  if (save) chrome.storage.sync.set({ engine: e });
}
$("engine").addEventListener("click", (e) => {
  const v = e.target?.dataset?.v;
  if (v) setEngine(v);
});

function setDirection(d, save = true) {
  currentDirection = d;
  if (lastHealth) renderPack(lastHealth.packs?.ja, d);
  $("direction").value = d;
  if (save) chrome.storage.sync.set({ direction: d });
}
$("direction").addEventListener("change", (e) => setDirection(e.target.value));

// ---------- Japonca paketi (isteğe bağlı, kullanıcı isterse iner) ----------
async function packAction(action) {
  try {
    const r = await fetch(`${API}/packs/ja/${action}`, { method: "POST" });
    const j = await r.json().catch(() => ({}));
    if (j.ok === false && j.message) alertBox(j.message);
  } catch {
    alertBox("Uygulamaya ulaşılamadı.");
  }
  refresh();
}
function alertBox(msg) {
  $("jaPack").innerHTML = `<span class="bad">${esc(msg)}</span>`;
}

function renderPack(p, direction) {
  const box = $("jaPack");
  const jaSelected = direction === "ja-tr" || direction === "tr-ja";
  if (!p) { box.innerHTML = ""; return; }
  const size = p.sizeMb ? ` <span style="white-space:nowrap">(~${Number(p.sizeMb)} MB)</span>` : "";
  if (p.state === "installed") {
    box.innerHTML = jaSelected
      ? `<span class="ok">● Japonca paketi kurulu</span><button class="btn ghost" id="jaRemove">Kaldır</button>` : "";
  } else if (p.state === "downloading") {
    box.innerHTML = `<span class="muted">● Japonca paketi indiriliyor...</span>`;
  } else {
    const err = p.state === "failed" ? `<span class="bad">İndirilemedi: ${esc(p.error || "")}</span><br>` : "";
    box.innerHTML = jaSelected
      ? `<span>${err}Japonca için paket gerekli${size}</span><button class="btn" id="jaDownload">İndir</button>`
      : "";
  }
  $("jaDownload")?.addEventListener("click", () => packAction("download"));
  $("jaRemove")?.addEventListener("click", () => {
    if (confirm("Japonca paketi kaldırılsın mı? Diskte yer açılır, istediğinizde tekrar indirebilirsiniz.")) packAction("remove");
  });
}

// ---------- Kaliteli çeviri modeli (isteğe bağlı, ~2.5 GB, kullanıcı isterse iner) ----------
async function llmAction(action) {
  try {
    const r = await fetch(`${API}/llm/${action}`, { method: "POST" });
    const j = await r.json().catch(() => ({}));
    if (j.ok === false && j.message) $("llmPack").innerHTML = `<span class="bad">${esc(j.message)}</span>`;
  } catch {
    $("llmPack").innerHTML = `<span class="bad">Uygulamaya ulaşılamadı.</span>`;
  }
  refresh();
}

function renderLlm(L, downloads) {
  const box = $("llmPack");
  if (!L) { box.innerHTML = ""; return; }
  const wanted = currentEngine === "llm" || (currentEngine === "auto" && L.recommended);
  const busy = L.state === "downloading" || L.state === "starting";
  if (busy) {
    const dl = (downloads || "").split(", ").find((x) => x.startsWith("Kaliteli")) || "";
    box.innerHTML = `<span class="muted">● ${L.state === "starting" ? "Kaliteli çeviri başlatılıyor..." : "İndiriliyor " + esc(dl.replace(/^Kaliteli çeviri (modeli|sunucusu)\s*/, ""))}</span>`;
  } else if (L.installed || L.state === "ready") {
    box.innerHTML = wanted
      ? `<span class="ok">● Kaliteli çeviri ${L.state === "ready" ? "açık" : "kurulu"}</span><button class="btn ghost" id="llmRemove">Kaldır</button>`
      : "";
  } else if (wanted) {
    const err = L.state === "failed" ? `<span class="bad">Açılamadı: ${esc(L.error || "")}</span><br>` : "";
    const warn = L.recommended ? "" : " · bu bilgisayarda yavaş olabilir";
    box.innerHTML = `<span>${err}Kaliteli çeviri için model gerekli <span style="white-space:nowrap">(~2.5 GB)</span>${warn}</span><button class="btn" id="llmDownload">İndir</button>`;
  } else {
    box.innerHTML = "";
  }
  $("llmDownload")?.addEventListener("click", () => llmAction("download"));
  $("llmRemove")?.addEventListener("click", () => {
    if (confirm("Kaliteli çeviri modeli kaldırılsın mı? Yaklaşık 2.5 GB yer açılır, istediğinizde tekrar indirebilirsiniz.")) llmAction("remove");
  });
}

let currentDirection = "en-tr";
let lastHealth = null;

// ---------- Uygulama durumu ----------
async function refresh() {
  const box = $("status");
  try {
    const h = await (await fetch(API + "/health", { cache: "no-store" })).json();
    lastHealth = h;
    renderPack(h.packs?.ja, currentDirection);
    renderLlm(h.translation?.llm, h.downloads);
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
    box.innerHTML = `${asr}<br>${mt}<br><span class="muted small">${esc(h.asr.description)}${h.asr.model ? " · " + esc(h.asr.model.replace(/^ggml-|\.bin$/g, "")) : ""} · sürüm ${esc(h.version)}</span>${dl}`;
  } catch {
    box.innerHTML = `<span class="bad">● Kick Çeviri uygulaması çalışmıyor</span><br>
      <span class="muted small">Başlat menüsünden "Kick Çeviri"yi açın. Saatin yanında simgesi görünür.</span>`;
  }
}

refresh();
setInterval(refresh, 2000);
