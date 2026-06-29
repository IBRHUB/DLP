const HOST_NAME = "com.ibrhub.dlp";
const MENU_ID = "dlp-download";
const MAX_TAB_CANDIDATES = 50;
const CANDIDATE_TTL_MS = 10 * 60 * 1000;
const MEDIA_URL_RE = /\.(m3u8|mpd|mp4|webm|m4v|mov)(?:[?#]|$)/i;
const STREAM_URL_RE = /(?:playlist|manifest|master|index)\.(?:m3u8|mpd)(?:[?#]|$)/i;

const DEFAULT_SETTINGS = {
  silentDownload: false,
  autoHideOverlay: true,
  overlayPosition: "auto",
  experimentalAllSites: false
};

const tabCandidates = new Map();
let settingsCache = { ...DEFAULT_SETTINGS };
let settingsCacheLoaded = false;

const SUPPORTED_DOCUMENT_URL_PATTERNS = [
  "*://youtube.com/*",
  "*://www.youtube.com/*",
  "*://m.youtube.com/*",
  "*://youtu.be/*",
  "*://tiktok.com/*",
  "*://www.tiktok.com/*",
  "*://m.tiktok.com/*",
  "*://vm.tiktok.com/*",
  "*://vt.tiktok.com/*",
  "*://instagram.com/*",
  "*://www.instagram.com/*",
  "*://m.instagram.com/*",
  "*://x.com/*",
  "*://www.x.com/*",
  "*://mobile.x.com/*",
  "*://twitter.com/*",
  "*://www.twitter.com/*",
  "*://mobile.twitter.com/*",
  "*://soundcloud.com/*",
  "*://www.soundcloud.com/*",
  "*://m.soundcloud.com/*",
  "*://on.soundcloud.com/*"
];

const EXPERIMENTAL_DOCUMENT_URL_PATTERNS = [
  "https://*/*"
];

function getSettings(callback) {
  chrome.storage.local.get(DEFAULT_SETTINGS, (storedSettings) => {
    settingsCache = { ...DEFAULT_SETTINGS, ...storedSettings };
    settingsCacheLoaded = true;
    callback(settingsCache);
  });
}

function getSafeHttpsUrl(url) {
  if (!url) {
    return null;
  }

  try {
    const parsed = new URL(url);
    return parsed.protocol === "https:" ? parsed.href : null;
  } catch {
    return null;
  }
}

function getContextMenuUrl(info, tab) {
  return info.linkUrl
    || getSafeHttpsUrl(info.srcUrl)
    || info.pageUrl
    || (tab && tab.url);
}

function getCandidateType(url) {
  if (/\.m3u8(?:[?#]|$)/i.test(url)) {
    return "hls";
  }

  if (/\.mpd(?:[?#]|$)/i.test(url)) {
    return "dash";
  }

  if (/\.mp4(?:[?#]|$)/i.test(url)) {
    return "direct-mp4";
  }

  if (/\.webm(?:[?#]|$)/i.test(url)) {
    return "direct-webm";
  }

  if (/\.(m4v|mov)(?:[?#]|$)/i.test(url)) {
    return "direct-video";
  }

  return "unknown";
}

function isLikelyMediaUrl(url) {
  return MEDIA_URL_RE.test(url) || STREAM_URL_RE.test(url);
}

function toCandidate(url, source, time) {
  const safeUrl = getSafeHttpsUrl(url);

  if (!safeUrl) {
    return null;
  }

  const type = getCandidateType(safeUrl);

  return {
    url: safeUrl,
    type,
    source: source || "unknown",
    time: time || Date.now()
  };
}

function normalizeCandidate(item) {
  const candidate = item && toCandidate(item.url, item.source, item.time);

  if (candidate && item.type) {
    candidate.type = item.type;
  }

  return candidate;
}

function dedupeCandidates(items) {
  const seen = new Set();
  const result = [];

  for (const item of items) {
    if (!item || !item.url || seen.has(item.url)) {
      continue;
    }

    seen.add(item.url);
    result.push(item);
  }

  return result;
}

function candidateScore(item) {
  let score = 0;

  if (item.type === "hls") {
    score += 120;
  } else if (item.type === "dash") {
    score += 115;
  } else if (item.type === "direct-mp4") {
    score += 95;
  } else if (item.type === "direct-webm" || item.type === "direct-video") {
    score += 90;
  } else if (item.type === "html5-video") {
    score += 70;
  } else {
    score += 30;
  }

  if (item.source === "network") {
    score += 10;
  } else if (String(item.source).startsWith("video.")) {
    score += 8;
  } else if (String(item.source).startsWith("meta.")) {
    score += 4;
  }

  score += Math.min(8, Math.max(0, (Date.now() - (item.time || 0)) / -30000 + 8));

  return score;
}

function rankCandidates(items) {
  return dedupeCandidates(items.map(normalizeCandidate).filter(Boolean))
    .sort((first, second) => candidateScore(second) - candidateScore(first))
    .slice(0, 20);
}

function pruneCandidates(items) {
  const cutoff = Date.now() - CANDIDATE_TTL_MS;
  return dedupeCandidates(items.filter((item) => item.time >= cutoff)).slice(-MAX_TAB_CANDIDATES);
}

function getTabCandidates(tabId) {
  if (typeof tabId !== "number" || tabId < 0) {
    return [];
  }

  const candidates = pruneCandidates(tabCandidates.get(tabId) || []);

  if (candidates.length) {
    tabCandidates.set(tabId, candidates);
  } else {
    tabCandidates.delete(tabId);
  }

  return candidates;
}

function isSupportedPageUrl(url) {
  const safeUrl = getSafeHttpsUrl(url);

  if (!safeUrl) {
    return false;
  }

  const host = new URL(safeUrl).hostname.toLowerCase();

  return [
    "youtube.com",
    "www.youtube.com",
    "m.youtube.com",
    "youtu.be",
    "tiktok.com",
    "www.tiktok.com",
    "m.tiktok.com",
    "vm.tiktok.com",
    "vt.tiktok.com",
    "instagram.com",
    "www.instagram.com",
    "m.instagram.com",
    "x.com",
    "www.x.com",
    "mobile.x.com",
    "twitter.com",
    "www.twitter.com",
    "mobile.twitter.com",
    "soundcloud.com",
    "www.soundcloud.com",
    "m.soundcloud.com",
    "on.soundcloud.com"
  ].includes(host);
}

function chooseDownloadUrl(preferredUrl, details) {
  const preferredSafeUrl = getSafeHttpsUrl(preferredUrl);
  const pageUrl = details.pageUrl || preferredSafeUrl;

  if (!details.settings?.experimentalAllSites || isSupportedPageUrl(pageUrl)) {
    return preferredSafeUrl || preferredUrl;
  }

  if (preferredSafeUrl && isLikelyMediaUrl(preferredSafeUrl)) {
    return preferredSafeUrl;
  }

  if (details.preservePreferredUrl && preferredSafeUrl) {
    return preferredSafeUrl;
  }

  const bestCandidate = rankCandidates(details.candidates || [])[0];

  return bestCandidate?.url || preferredSafeUrl || preferredUrl;
}

function scanPageCandidates(tab, settings, callback) {
  if (!tab?.id || !settings.experimentalAllSites || isSupportedPageUrl(tab.url)) {
    callback([]);
    return;
  }

  chrome.tabs.sendMessage(
    tab.id,
    {
      type: "dlp-scan-candidates",
      experimentalAllSites: true
    },
    (response) => {
      if (chrome.runtime.lastError) {
        callback([]);
        return;
      }

      callback(Array.isArray(response?.candidates) ? response.candidates : []);
    }
  );
}

function rememberNetworkCandidate(details) {
  if (details.tabId < 0 || !isLikelyMediaUrl(details.url)) {
    return;
  }

  const candidate = toCandidate(details.url, "network");

  if (!candidate) {
    return;
  }

  const list = getTabCandidates(details.tabId);
  list.push(candidate);
  tabCandidates.set(details.tabId, pruneCandidates(list));
}

function sendNativePayload(payload, callback) {
  chrome.runtime.sendNativeMessage(HOST_NAME, payload, (response) => {
    if (chrome.runtime.lastError) {
      const error = {
        ok: false,
        error: "native_host_error",
        message: chrome.runtime.lastError.message
      };

      console.log("DLP native host error:", chrome.runtime.lastError.message);

      if (callback) {
        callback(error);
      }

      return;
    }

    console.log("DLP native host response:", response);

    if (callback) {
      callback(response);
    }
  });
}

function sendDownloadWithSettings(url, options, settings, callback) {
  const details = options || {};

  if (!url) {
    const error = {
      ok: false,
      error: "missing_url",
      message: "DLP could not determine a supported video URL"
    };

    console.log(error.message);

    if (callback) {
      callback(error);
    }

    return;
  }

  sendNativePayload({
    action: "download",
    url,
    title: details.title || "",
    source: "chrome-extension",
    timestamp: new Date().toISOString(),
    silent: Boolean(settings.silentDownload),
    experimental: Boolean(settings.experimentalAllSites)
  }, callback);
}

function sendNativeCommand(action, details, callback) {
  if (typeof details === "function") {
    callback = details;
    details = {};
  }

  sendNativePayload({
    action,
    ...(details || {}),
    source: "chrome-extension",
    timestamp: new Date().toISOString()
  }, callback);
}

function createContextMenu() {
  getSettings((settings) => {
    const documentUrlPatterns = settings.experimentalAllSites
      ? EXPERIMENTAL_DOCUMENT_URL_PATTERNS
      : SUPPORTED_DOCUMENT_URL_PATTERNS;

    chrome.contextMenus.create({
      id: MENU_ID,
      title: "Download with DLP",
      contexts: ["page", "link", "video"],
      documentUrlPatterns
    });
  });
}

function refreshContextMenu() {
  chrome.contextMenus.removeAll(() => {
    if (chrome.runtime.lastError) {
      console.log("DLP context menu cleanup failed:", chrome.runtime.lastError.message);
    }

    createContextMenu();
  });
}

chrome.runtime.onInstalled.addListener(() => {
  refreshContextMenu();
});

getSettings(() => {});

chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName === "local") {
    for (const key of Object.keys(DEFAULT_SETTINGS)) {
      if (Object.prototype.hasOwnProperty.call(changes, key)) {
        settingsCache[key] = changes[key].newValue ?? DEFAULT_SETTINGS[key];
      }
    }
  }

  if (areaName === "local" && Object.prototype.hasOwnProperty.call(changes, "experimentalAllSites")) {
    refreshContextMenu();
  }
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== MENU_ID) {
    return;
  }

  const preferredUrl = getContextMenuUrl(info, tab);
  const clickedUrl = getSafeHttpsUrl(info.linkUrl) || getSafeHttpsUrl(info.srcUrl);

  getSettings((settings) => {
    scanPageCandidates(tab, settings, (pageCandidates) => {
      const candidates = [
        ...pageCandidates,
        ...getTabCandidates(tab?.id),
        ...(clickedUrl ? [toCandidate(clickedUrl, "context")] : [])
      ].filter(Boolean);

      const url = chooseDownloadUrl(preferredUrl, {
        settings,
        pageUrl: tab?.url || info.pageUrl,
        candidates,
        preservePreferredUrl: Boolean(info.linkUrl && !isLikelyMediaUrl(info.linkUrl))
      });

      sendDownloadWithSettings(url, {
        title: tab?.title || ""
      }, settings);
    });
  });
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message !== "object") {
    return false;
  }

  if (message.type === "dlp-native-command") {
    sendNativeCommand(message.action, {
      fileName: message.fileName || ""
    }, sendResponse);
    return true;
  }

  if (message.type === "dlp-download-current-video") {
    const preferredUrl = message.url || (sender.tab && sender.tab.url);
    const title = message.title || (sender.tab && sender.tab.title) || "";

    getSettings((settings) => {
      const candidates = [
        ...(Array.isArray(message.candidates) ? message.candidates : []),
        ...getTabCandidates(sender.tab?.id)
      ];
      const url = chooseDownloadUrl(preferredUrl, {
        settings,
        pageUrl: message.pageUrl || sender.tab?.url,
        candidates
      });

      sendDownloadWithSettings(url, { title }, settings, sendResponse);
    });
    return true;
  }

  return false;
});

if (chrome.webRequest?.onBeforeRequest) {
  chrome.webRequest.onBeforeRequest.addListener(
    (details) => {
      if (settingsCache.experimentalAllSites) {
        rememberNetworkCandidate(details);
        return;
      }

      if (settingsCacheLoaded) {
        return;
      }

      getSettings((settings) => {
        if (settings.experimentalAllSites) {
          rememberNetworkCandidate(details);
        }
      });
    },
    {
      urls: ["https://*/*"],
      types: ["media", "xmlhttprequest", "other"]
    }
  );
}

chrome.tabs.onRemoved.addListener((tabId) => {
  tabCandidates.delete(tabId);
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === "loading" || changeInfo.url) {
    tabCandidates.delete(tabId);
  }
});
