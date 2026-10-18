// Video oynatıcının istediği HLS (m3u8) adreslerini sekme bazında not eder.
// İçerik betiği Kick API'sinden adresi alamazsa bu kayda başvurur.

const key = (tabId) => `m3u8_${tabId}`;

chrome.webRequest.onBeforeRequest.addListener(
  (d) => {
    if (d.tabId < 0 || !/\.m3u8(\?|$)/i.test(d.url)) return;
    const isMaster = /master\.m3u8/i.test(d.url);
    chrome.storage.session.get(key(d.tabId)).then((o) => {
      const cur = o[key(d.tabId)];
      // Ana (master) liste her zaman öncelikli; yoksa ilk görülen kalite listesi.
      if (!cur || isMaster || !cur.master) {
        chrome.storage.session.set({
          [key(d.tabId)]: { url: d.url, master: isMaster, ts: Date.now() },
        });
      }
    });
  },
  {
    urls: [
      "https://*.kick.com/*",
      "https://*.cloudfront.net/*",
      "https://*.live-video.net/*",
    ],
  }
);

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  const tabId = sender.tab?.id;
  if (tabId == null) return;

  if (msg?.type === "getM3u8") {
    chrome.storage.session.get(key(tabId)).then((o) => sendResponse(o[key(tabId)] || null));
    return true; // yanıt asenkron
  }
  if (msg?.type === "resetM3u8") {
    chrome.storage.session.remove(key(tabId));
  }
});

chrome.tabs.onRemoved.addListener((tabId) => chrome.storage.session.remove(key(tabId)));
