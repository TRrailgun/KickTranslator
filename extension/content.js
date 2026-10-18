// Kick sayfalarında videoyu bulur, yerel Kick Çeviri uygulamasına bağlanır ve altyazıyı çizer.
//  - Yayın tekrarı (VOD): uygulama sesi akış adresinden kendisi çeker, oynatmanın önünden çevirir.
//  - Canlı yayın: videonun çaldığı ses 16 kHz olarak uygulamaya gönderilir, gelen altyazı hemen gösterilir.
(() => {
  "use strict";

  const SERVER = "ws://127.0.0.1:5757/ws";
  const VOD_RE = /^\/[^/]+\/videos\/([0-9a-f-]{36})/i;
  const CHANNEL_RE = /^\/([A-Za-z0-9_-]{2,30})\/?$/;
  const RESERVED = new Set([
    "browse", "categories", "category", "following", "search", "dashboard", "settings", "terms",
    "privacy", "subscriptions", "messages", "notifications", "about", "help", "signup", "login",
  ]);

  let settings = { enabled: true, showOriginal: false, fontSize: 26, offset: 0, direction: "en-tr" };

  const s = {
    key: null,
    mode: null,          // "vod" | "live"
    navTime: Date.now(),
    video: null,
    url: null,
    ws: null,
    cues: [],
    liveCue: null,
    liveUntil: 0,
    layer: null,
    sub: null,
    badge: null,
    badgeTimer: null,
    renderTimer: null,
    reconnectTimer: null,
    retry: 0,
    lastTimeSent: 0,
    listeners: [],
    setupToken: 0,
    audio: null,         // { ctx, node, src, sink }
  };

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  // ---------- Ayarlar ----------
  chrome.storage.sync.get(settings).then((v) => {
    settings = { ...settings, ...v };
    applyStyle();
  });

  chrome.storage.onChanged.addListener((ch, area) => {
    if (area !== "sync") return;
    const needRestart = ("enabled" in ch) || ("direction" in ch);
    for (const k of Object.keys(ch)) settings[k] = ch[k].newValue;
    applyStyle();
    if (needRestart) restart();
    render();
  });

  document.addEventListener("keydown", (e) => {
    if (e.altKey && (e.key === "t" || e.key === "T")) {
      chrome.storage.sync.set({ enabled: !settings.enabled });
    }
  });

  function restart() {
    teardown();
    s.key = null; // bir sonraki kontrolde yeniden kurulur
  }

  // ---------- Sayfa takibi (Kick tek sayfalı uygulama, URL değişince yeniden kur) ----------
  setInterval(() => {
    // Oynatıcı yeniden çizildiyse (video öğesi değişti) baştan kur
    if (s.video && !s.video.isConnected) restart();

    const path = location.pathname;
    let key = null, mode = null, id = null;
    const vod = path.match(VOD_RE);
    const ch = path.match(CHANNEL_RE);
    if (vod) { mode = "vod"; id = vod[1]; }
    else if (ch && !RESERVED.has(ch[1].toLowerCase())) { mode = "live"; id = ch[1]; }
    if (settings.enabled && mode) key = `${mode}:${id}:${settings.direction}`;

    if (key !== s.key) {
      teardown();
      s.key = key;
      s.navTime = Date.now();
      if (key) setup(mode, id);
    }
  }, 1000);

  async function setup(mode, id) {
    const token = ++s.setupToken;
    s.mode = mode;

    if (mode === "live" && !(await isChannelLive(id))) {
      // Kanal çevrimdışı: bir dakika sonra tekrar bak (yayın başlarsa kendiliğinden açılsın)
      setTimeout(() => { if (token === s.setupToken) s.key = null; }, 60000);
      return;
    }
    if (token !== s.setupToken) return;

    // Video öğesini bekle
    let video = null;
    for (let i = 0; i < 60 && !video; i++) {
      video = [...document.querySelectorAll("video")].find((v) => v.offsetWidth > 200) || null;
      if (!video) await sleep(500);
      if (token !== s.setupToken) return;
    }
    if (!video) return;
    s.video = video;
    createOverlay(video);
    s.renderTimer = setInterval(render, 150);

    if (mode === "vod") {
      showBadge("Video adresi aranıyor...");
      const url = await findStreamUrl(id, token);
      if (token !== s.setupToken) return;
      if (!url) { showBadge("Video akış adresi bulunamadı", true); return; }
      s.url = url;
      on(video, "timeupdate", onTimeUpdate);
      on(video, "seeking", onSeek);
    }
    connect();
  }

  async function isChannelLive(slug) {
    try {
      const r = await fetch(`/api/v2/channels/${encodeURIComponent(slug)}`, { credentials: "include" });
      if (!r.ok) return true; // API değiştiyse: video varsa dene
      const j = await r.json();
      return !!(j && (j.livestream || j.is_live));
    } catch {
      return true;
    }
  }

  async function findStreamUrl(id, token) {
    for (let i = 0; i < 10; i++) {
      try {
        const res = await fetch(`/api/v1/video/${id}`, { credentials: "include" });
        if (res.ok) {
          const j = await res.json();
          const u = j?.source || j?.video?.source || j?.livestream?.source;
          if (u) return u;
        }
      } catch {}
      try {
        const r = await chrome.runtime.sendMessage({ type: "getM3u8" });
        if (r?.url && r.ts >= s.navTime - 3000) return r.url;
      } catch {}
      await sleep(1000);
      if (token !== s.setupToken) return null;
    }
    return null;
  }

  function teardown() {
    s.setupToken++;
    clearTimeout(s.reconnectTimer);
    clearInterval(s.renderTimer);
    for (const [el, ev, fn, opts] of s.listeners) el.removeEventListener(ev, fn, opts);
    s.listeners = [];
    stopAudio();
    if (s.ws) {
      s.ws.onclose = null;
      try { s.ws.close(); } catch {}
    }
    s.ws = null;
    s.layer?.remove();
    s.layer = s.sub = s.badge = null;
    s.video = null;
    s.url = null;
    s.cues = [];
    s.liveCue = null;
    s.retry = 0;
  }

  function on(el, ev, fn, opts) {
    el.addEventListener(ev, fn, opts);
    s.listeners.push([el, ev, fn, opts]);
  }

  // ---------- Uygulama bağlantısı ----------
  function connect() {
    if (!s.video) return;
    const ws = new WebSocket(SERVER);
    ws.binaryType = "arraybuffer";
    s.ws = ws;

    ws.onopen = () => {
      s.retry = 0;
      if (s.mode === "vod") {
        send({ type: "start", url: s.url, position: s.video.currentTime, direction: settings.direction });
      } else {
        send({ type: "live-start", direction: settings.direction });
        startAudio().catch((e) => showBadge("Ses yakalanamadı: " + e.message, true));
      }
    };

    ws.onmessage = (e) => {
      let m;
      try { m = JSON.parse(e.data); } catch { return; }
      if (m.type === "cue") addCue(m);
      else if (m.type === "status") onStatus(m);
      else if (m.type === "error") showBadge(m.message, true);
    };

    ws.onclose = () => {
      s.ws = null;
      stopAudio();
      if (!s.video) return;
      showBadge("Kick Çeviri uygulaması çalışmıyor — Başlat menüsünden açın", true);
      const delay = Math.min(15000, 1000 * 2 ** s.retry++);
      s.reconnectTimer = setTimeout(connect, delay);
    };
  }

  function send(obj) {
    if (s.ws?.readyState === WebSocket.OPEN) s.ws.send(JSON.stringify(obj));
  }

  function onTimeUpdate() {
    const now = Date.now();
    if (now - s.lastTimeSent < 1000) return;
    s.lastTimeSent = now;
    send({ type: "time", position: s.video.currentTime });
  }

  function onSeek() {
    send({ type: "seek", position: s.video.currentTime });
  }

  function onStatus(m) {
    if (m.state === "running" || m.state === "connected") return; // sessiz çalış
    if (m.state === "live") { showBadge("Canlı çeviri açık"); return; }
    if (m.message) showBadge(m.message, false, m.state === "loading");
  }

  // ---------- Canlı yayın: ses yakalama ----------
  async function startAudio() {
    if (s.audio || !s.video) return;
    const video = s.video;
    const stream = video.captureStream ? video.captureStream() : null;
    if (!stream) throw new Error("tarayıcı desteklemiyor");

    // Ses izi oynatma başlayınca gelir
    for (let i = 0; i < 40 && stream.getAudioTracks().length === 0; i++) await sleep(250);
    if (stream.getAudioTracks().length === 0) throw new Error("videoda ses izi yok");

    const ctx = new AudioContext();
    const src = ctx.createMediaStreamSource(stream);
    const sink = ctx.createGain();
    sink.gain.value = 0; // sesi ikinci kez çalmamak için
    sink.connect(ctx.destination);

    let node;
    try {
      await ctx.audioWorklet.addModule(chrome.runtime.getURL("pcm-worklet.js"));
      node = new AudioWorkletNode(ctx, "pcm-capture", { processorOptions: { targetRate: 16000 } });
      node.port.onmessage = (e) => onPcm(e.data);
    } catch {
      // Yedek: ScriptProcessor (eski ama her yerde çalışır)
      node = ctx.createScriptProcessor(4096, 2, 1);
      const ratio = ctx.sampleRate / 16000;
      let pos = 0;
      node.onaudioprocess = (e) => {
        const b = e.inputBuffer;
        const ch0 = b.getChannelData(0), ch1 = b.numberOfChannels > 1 ? b.getChannelData(1) : ch0;
        const out = [];
        for (; pos < ch0.length; pos += ratio) {
          const i = Math.floor(pos);
          out.push((ch0[i] + ch1[i]) / 2);
        }
        pos -= ch0.length;
        onPcm(new Float32Array(out));
      };
    }
    src.connect(node);
    node.connect(sink);
    s.audio = { ctx, node, src, sink };

    if (ctx.state !== "running") {
      ctx.resume().catch(() => {});
      const resume = () => ctx.resume().catch(() => {});
      on(document, "click", resume, { capture: true });
      on(document, "keydown", resume, { capture: true });
      showBadge("Canlı çeviri için sayfaya bir kez tıklayın");
    }
  }

  function stopAudio() {
    const a = s.audio;
    s.audio = null;
    if (!a) return;
    try { a.src.disconnect(); a.node.disconnect(); a.sink.disconnect(); } catch {}
    a.ctx.close().catch(() => {});
  }

  function onPcm(samples) {
    const v = s.video;
    if (!v || v.paused || !s.ws || s.ws.readyState !== WebSocket.OPEN || !samples.length) return;
    const start = v.currentTime - samples.length / 16000;
    const buf = new ArrayBuffer(8 + samples.length * 4);
    new DataView(buf).setFloat64(0, start, true);
    new Float32Array(buf, 8).set(samples);
    s.ws.send(buf);
  }

  // ---------- Altyazılar ----------
  function addCue(c) {
    if (c.live) {
      // Kesinleşmiş altyazı gelmişken, aynı konuşmanın geç kalan taslağı onu ezmesin
      if (c.partial && s.liveCue && !s.liveCue.partial && s.liveCue.end > c.start + 0.2) return;
      s.liveCue = c;
      const dur = (c.end - c.start) * 1000;
      s.liveUntil = performance.now() + (c.partial ? 4000 : Math.min(8000, Math.max(2500, dur + 1500)));
      render();
      return;
    }
    const dur = Math.max(0.2, c.end - c.start);
    // Yeniden işlenen bölgeden gelen çakışan eski altyazıları at
    s.cues = s.cues.filter((x) => {
      const overlap = Math.min(x.end, c.end) - Math.max(x.start, c.start);
      return overlap < dur * 0.5;
    });
    let i = s.cues.length;
    while (i > 0 && s.cues[i - 1].start > c.start) i--;
    s.cues.splice(i, 0, c);
  }

  function activeCue(t) {
    let lo = 0, hi = s.cues.length - 1, idx = -1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (s.cues[mid].start <= t) { idx = mid; lo = mid + 1; } else hi = mid - 1;
    }
    if (idx < 0) return null;
    const c = s.cues[idx];
    return t <= Math.max(c.end, c.start + 1.2) + 0.4 ? c : null;
  }

  function render() {
    if (!s.sub || !s.video) return;
    let c = null;
    if (settings.enabled) {
      if (s.mode === "live") c = s.liveCue && performance.now() < s.liveUntil ? s.liveCue : null;
      else c = activeCue(s.video.currentTime - (Number(settings.offset) || 0));
    }
    const key = c ? `${c.start}|${c.src}|${c.partial ? 1 : 0}|${settings.showOriginal}` : "";
    if (s.sub.dataset.key === key) return;
    s.sub.dataset.key = key;
    s.sub.textContent = "";
    s.sub.classList.toggle("kt-partial", !!(c && c.partial));
    if (!c) return;

    const main = document.createElement("span");
    main.className = "kt-main";
    main.textContent = c.dst || c.src;
    s.sub.appendChild(main);

    if (settings.showOriginal && c.dst) {
      const orig = document.createElement("span");
      orig.className = "kt-orig";
      orig.textContent = c.src;
      s.sub.appendChild(orig);
    }
  }

  // ---------- Arayüz ----------
  function createOverlay(video) {
    const host = video.parentElement;
    if (getComputedStyle(host).position === "static") host.style.position = "relative";

    const layer = document.createElement("div");
    layer.className = "kt-layer";
    const sub = document.createElement("div");
    sub.className = "kt-sub";
    const badge = document.createElement("div");
    badge.className = "kt-badge kt-hidden";

    layer.append(sub, badge);
    host.appendChild(layer);
    s.layer = layer;
    s.sub = sub;
    s.badge = badge;
    applyStyle();
  }

  function applyStyle() {
    s.layer?.style.setProperty("--kt-size", `${Number(settings.fontSize) || 26}px`);
  }

  function showBadge(text, isError = false, sticky = false) {
    if (!s.badge) return;
    const dir = settings.direction === "tr-en" ? "TR→EN" : "EN→TR";
    s.badge.textContent = `${dir} · ${text}`;
    s.badge.classList.toggle("kt-error", isError);
    s.badge.classList.remove("kt-hidden");
    clearTimeout(s.badgeTimer);
    if (!isError && !sticky) {
      s.badgeTimer = setTimeout(() => s.badge?.classList.add("kt-hidden"), 3500);
    }
  }
})();
