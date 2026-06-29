const HOST_NAME = "com.ibrhub.dlp";
const MENU_ID = "dlp-download";
const MAX_TAB_CANDIDATES = 50;
const CANDIDATE_TTL_MS = 10 * 60 * 1000;
const MEDIA_URL_RE = /\.(m3u8|m3u|mpd|mp4|webm|m4v|mov)(?:[?#]|$)/i;
const STREAM_URL_RE = /(?:playlist|manifest|master|index)\.(?:m3u8|m3u|mpd)(?:[?#]|$)/i;
const MEDIA_QUERY_RE = /[?&](?:file|filename|name|src|url)=[^&#]+\.(?:m3u8|m3u|mpd|mp4|webm|m4v|mov)(?:[&#]|$)/i;
const AUDIO_ITAG_RE = /(?:^|[?&#])itag=(?:139|140|141|249|250|251)(?:[&#]|$)/i;

const DEFAULT_SETTINGS = {
  silentDownload: false,
  autoHideOverlay: true,
  overlayPosition: "auto",
  experimentalAllSites: false,
  deepScanner: false,
  streamOverlay: false,
  browserCookies: false,
  cookieBrowser: "brave"
};

const COOKIE_BROWSERS = new Set(["brave", "chrome", "edge", "firefox", "opera", "vivaldi", "chromium", "whale"]);

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

function getUrlOrigin(url) {
  try {
    return new URL(url).origin;
  } catch {
    return "";
  }
}

function getCookieBrowser(settings) {
  const value = String(settings.cookieBrowser || DEFAULT_SETTINGS.cookieBrowser).toLowerCase();
  return COOKIE_BROWSERS.has(value) ? value : DEFAULT_SETTINGS.cookieBrowser;
}

function getContextMenuUrl(info, tab) {
  return info.linkUrl
    || getSafeHttpsUrl(info.srcUrl)
    || info.pageUrl
    || (tab && tab.url);
}

function getCandidateType(url) {
  const mediaRole = getMediaRole(url);
  const extension = getMediaExtension(url);

  if (mediaRole === "audio") {
    return "direct-audio";
  }

  if (extension === "m3u8" || extension === "m3u") {
    return "hls";
  }

  if (extension === "mpd") {
    return "dash";
  }

  if (extension === "mp4") {
    return "direct-mp4";
  }

  if (extension === "webm") {
    return "direct-webm";
  }

  if (extension === "m4v" || extension === "mov") {
    return "direct-video";
  }

  return "unknown";
}

function getMediaExtension(url) {
  try {
    const parsed = new URL(url);
    const path = decodeURIComponent(parsed.pathname).replace(/\/+$/, "");
    const pathMatch = path.match(/\.(m3u8|m3u|mpd|mp4|webm|m4v|mov)$/i);

    if (pathMatch) {
      return pathMatch[1].toLowerCase();
    }

    const fileName = getQueryMediaFileName(parsed);
    const queryMatch = fileName.match(/\.(m3u8|m3u|mpd|mp4|webm|m4v|mov)$/i);
    return queryMatch ? queryMatch[1].toLowerCase() : "";
  } catch {
    return "";
  }
}

function getQueryMediaFileName(parsedUrl) {
  for (const name of ["file", "filename", "name", "src"]) {
    const value = parsedUrl.searchParams.get(name);

    if (!value) {
      continue;
    }

    const cleanValue = decodeURIComponent(value).split(/[?#]/)[0].replace(/\/+$/, "");
    const fileName = cleanValue.split(/[\\/]/).pop() || "";

    if (/\.(?:m3u8|m3u|mpd|mp4|webm|m4v|mov)$/i.test(fileName)) {
      return fileName.toLowerCase();
    }
  }

  return "";
}

function getMediaRole(url) {
  try {
    const parsed = new URL(url);
    const path = decodeURIComponent(parsed.pathname).toLowerCase();
    const query = decodeURIComponent(parsed.search).toLowerCase();
    const queryFileName = getQueryMediaFileName(parsed);
    const mediaText = `${path} ${query} ${queryFileName}`;

    if (
      /(?:^|[._/-])(?:audio|bestaudio|dash_audio|mp4a|aac|opus)(?:[._/-]|$)/.test(mediaText)
      || /(?:mime|mimetype|type|contenttype)=audio(?:%2f|\/|&|$)/.test(query)
      || AUDIO_ITAG_RE.test(parsed.search)
    ) {
      return "audio";
    }

    if (
      /(?:^|[._/-])(?:video|source|dash_video|avc|h264|h265|vp9|av01)(?:[._/-]|$)/.test(mediaText)
      || /(?:^|[._/-])(?:144|240|360|480|720|1080|1440|2160)p(?:[._/-]|$)/.test(mediaText)
      || /(?:mime|mimetype|type|contenttype)=video(?:%2f|\/|&|$)/.test(query)
    ) {
      return "video";
    }
  } catch {
    return "unknown";
  }

  return "unknown";
}

function isLikelyMediaUrl(url) {
  return MEDIA_URL_RE.test(url)
    || STREAM_URL_RE.test(url)
    || MEDIA_QUERY_RE.test(url)
    || Boolean(getMediaExtension(url));
}

function mediaUrlShapeScore(url) {
  try {
    const parsed = new URL(url);
    const queryFileName = getQueryMediaFileName(parsed);
    const cleanPath = parsed.pathname.replace(/\/+$/, "");
    const pathLooksMedia = MEDIA_URL_RE.test(cleanPath);
    let score = 0;

    if (queryFileName) {
      score += 180;
    }

    if (queryFileName && !pathLooksMedia) {
      score += 40;
    }

    if (pathLooksMedia && /\/$/i.test(parsed.pathname)) {
      score -= 80;
    }

    return score;
  } catch {
    return 0;
  }
}

function toCandidate(url, source, time, context) {
  const safeUrl = getSafeHttpsUrl(url);

  if (!safeUrl) {
    return null;
  }

  const type = getCandidateType(safeUrl);

  return {
    url: safeUrl,
    type,
    source: source || "unknown",
    time: time || Date.now(),
    pageUrl: context?.pageUrl || "",
    origin: context?.origin || "",
    userAgent: context?.userAgent || ""
  };
}

function normalizeCandidate(item) {
  const candidate = item && toCandidate(item.url, item.source, item.time, item);

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

function getMediaPairKey(url) {
  try {
    const parsed = new URL(url);
    const pathParts = parsed.pathname.split("/");
    const fileName = getQueryMediaFileName(parsed)
      || decodeURIComponent(pathParts.pop() || "").toLowerCase();
    const directory = pathParts.join("/");
    const stem = fileName
      .replace(/\.(?:mp4|webm|m4v|mov)$/i, "")
      .replace(/(?:^|[._-])(?:source|video|audio|dash_audio|dash_video|bestaudio|mp4a|aac|opus|avc|h264|h265|vp9|av01|(?:144|240|360|480|720|1080|1440|2160)p)(?=$|[._-])/g, "");

    return stem ? `${parsed.origin}${directory}/${stem}` : null;
  } catch {
    return null;
  }
}

function findMediaPair(items) {
  const groups = new Map();

  for (const item of rankCandidates(items)) {
    const key = getMediaPairKey(item.url);

    if (!key) {
      continue;
    }

    const group = groups.get(key) || {};

    if (item.type === "direct-audio") {
      group.audio ||= item;
    } else if (item.type === "direct-mp4" || item.type === "direct-webm" || item.type === "direct-video") {
      group.video ||= item;
    }

    groups.set(key, group);
  }

  for (const group of groups.values()) {
    if (group.video?.url && group.audio?.url) {
      return {
        videoUrl: group.video.url,
        audioUrl: group.audio.url
      };
    }
  }

  return null;
}

function candidateScore(item) {
  let score = 0;
  const ageMs = Date.now() - (item.time || 0);
  const url = item.url || "";

  if (item.type === "direct-audio") {
    score -= 80;
  } else if (item.type === "direct-mp4") {
    score += 130;
  } else if (item.type === "direct-webm" || item.type === "direct-video") {
    score += 125;
  } else if (item.type === "hls") {
    score += 120;
  } else if (item.type === "dash") {
    score += 85;
  } else if (item.type === "html5-video") {
    score += 70;
  } else {
    score += 30;
  }

  if (item.source === "video.currentSrc") {
    score += 140;
  } else if (item.source === "video.src" || item.source === "source.src") {
    score += 100;
  } else if (item.source === "network.redirect") {
    score += 90;
  } else if (item.source === "clappr.source" || item.source === "hls.loadSource") {
    score += 130;
  } else if (item.source === "network") {
    score += 24;
  } else if (item.source === "performance") {
    score += 16;
  } else if (String(item.source).startsWith("meta.")) {
    score += 4;
  }

  score += Math.min(20, Math.max(0, 20 - (ageMs / 30000)));

  if ((item.source === "network" || item.source === "performance") && ageMs > 120000) {
    score -= ageMs > 300000 ? 100 : 50;
  }

  score += mediaUrlShapeScore(url);

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

  if (details.preservePreferredUrl && preferredSafeUrl) {
    return preferredSafeUrl;
  }

  const preferredCandidate = preferredSafeUrl && isLikelyMediaUrl(preferredSafeUrl)
    ? toCandidate(preferredSafeUrl, "preferred")
    : null;
  const bestCandidate = rankCandidates([
    ...(details.candidates || []),
    preferredCandidate
  ])[0];

  if (bestCandidate?.type === "direct-audio" && preferredSafeUrl && !isLikelyMediaUrl(preferredSafeUrl)) {
    return preferredSafeUrl;
  }

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
      experimentalAllSites: true,
      deepScanner: Boolean(settings.deepScanner)
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

function rememberNetworkCandidate(details, source = "network") {
  if (details.tabId < 0 || !isLikelyMediaUrl(details.url)) {
    return;
  }

  const candidate = toCandidate(details.url, source, undefined, {
    pageUrl: details.documentUrl || details.initiator || "",
    origin: details.initiator || "",
    userAgent: navigator.userAgent || ""
  });

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
  const audioUrl = getSafeHttpsUrl(details.audioUrl);

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
    ...(audioUrl ? { audioUrl } : {}),
    title: details.title || "",
    pageUrl: details.pageUrl || "",
    userAgent: details.userAgent || navigator.userAgent || "",
    browserCookies: Boolean(settings.browserCookies),
    cookieBrowser: getCookieBrowser(settings),
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

function getHlsFetchOptions(candidate, extraHeaders) {
  const headers = {
    ...(extraHeaders || {})
  };

  for (const key of Object.keys(headers)) {
    if (!headers[key]) {
      delete headers[key];
    }
  }

  return {
    cache: "no-store",
    credentials: "omit",
    headers
  };
}

async function fetchText(url, candidate) {
  const response = await fetch(url, getHlsFetchOptions(candidate, {
    Accept: "application/vnd.apple.mpegurl, application/x-mpegURL, */*"
  }));

  if (!response.ok) {
    throw new Error(`playlist_http_${response.status}`);
  }

  return response.text();
}

function getAbsoluteHlsLine(line, baseUrl) {
  const trimmed = line.trim();

  if (!trimmed || trimmed.startsWith("#")) {
    return line.replace(/URI="([^"]+)"/g, (match, uri) => {
      try {
        return `URI="${new URL(uri, baseUrl).href}"`;
      } catch {
        return match;
      }
    });
  }

  try {
    return new URL(trimmed, baseUrl).href;
  } catch {
    return trimmed;
  }
}

function selectBestVariantUrl(playlistText, baseUrl) {
  const lines = playlistText.split(/\r?\n/);
  let pendingBandwidth = 0;
  let best = null;

  for (const line of lines) {
    const trimmed = line.trim();

    if (trimmed.startsWith("#EXT-X-STREAM-INF:")) {
      const match = trimmed.match(/BANDWIDTH=(\d+)/i);
      pendingBandwidth = match ? Number(match[1]) : 1;
      continue;
    }

    if (pendingBandwidth && trimmed && !trimmed.startsWith("#")) {
      try {
        const url = new URL(trimmed, baseUrl).href;

        if (!best || pendingBandwidth > best.bandwidth) {
          best = { url, bandwidth: pendingBandwidth };
        }
      } catch {
        // Keep scanning other variants.
      }

      pendingBandwidth = 0;
    }
  }

  return best?.url || "";
}

function findMpegTsOffset(bytes) {
  const maxOffset = Math.min(256, bytes.length - 376);

  for (let offset = 0; offset <= maxOffset; offset += 1) {
    if (bytes[offset] === 0x47 && bytes[offset + 188] === 0x47 && bytes[offset + 376] === 0x47) {
      return offset;
    }
  }

  return 0;
}

async function fetchSegmentProbe(url, candidate) {
  const response = await fetch(url, getHlsFetchOptions(candidate, {
    Accept: "*/*",
    Range: "bytes=0-1199"
  }));

  if (!response.ok && response.status !== 206) {
    return 0;
  }

  return findMpegTsOffset(new Uint8Array(await response.arrayBuffer()));
}

async function fetchContentLength(url, candidate) {
  try {
    const response = await fetch(url, {
      ...getHlsFetchOptions(candidate, { Accept: "*/*" }),
      method: "HEAD"
    });
    const length = Number(response.headers.get("content-length"));

    if (response.ok && Number.isFinite(length) && length > 0) {
      return length;
    }
  } catch {
    // Some hosts reject HEAD. Fall back to a one-byte range request.
  }

  const response = await fetch(url, getHlsFetchOptions(candidate, {
    Accept: "*/*",
    Range: "bytes=0-0"
  }));
  const contentRange = response.headers.get("content-range") || "";
  const match = contentRange.match(/\/(\d+)$/);

  return match ? Number(match[1]) : 0;
}

async function buildVlcReadyHlsPlaylist(candidate) {
  let playlistUrl = candidate.url;
  let playlistText = await fetchText(playlistUrl, candidate);
  const variantUrl = selectBestVariantUrl(playlistText, playlistUrl);

  if (variantUrl) {
    playlistUrl = variantUrl;
    playlistText = await fetchText(playlistUrl, candidate);
  }

  if (!playlistText.trimStart().startsWith("#EXTM3U")) {
    throw new Error("not_hls_playlist");
  }

  const lines = playlistText.split(/\r?\n/);
  const mediaUrls = lines
    .map((line) => line.trim())
    .filter((line) => line && !line.startsWith("#"))
    .map((line) => {
      try {
        return new URL(line, playlistUrl).href;
      } catch {
        return "";
      }
    })
    .filter(Boolean);

  const prefixBytes = mediaUrls.length ? await fetchSegmentProbe(mediaUrls[0], candidate) : 0;
  const output = [];
  const hasVersion = lines.some((line) => line.trim().toUpperCase().startsWith("#EXT-X-VERSION:"));

  for (const line of lines) {
    const absoluteLine = getAbsoluteHlsLine(line, playlistUrl);
    const trimmed = absoluteLine.trim();

    if (prefixBytes > 0 && trimmed.toUpperCase().startsWith("#EXT-X-VERSION:")) {
      const currentVersion = Number(trimmed.split(":")[1]);
      output.push(`#EXT-X-VERSION:${Math.max(4, Number.isFinite(currentVersion) ? currentVersion : 4)}`);
      continue;
    }

    if (prefixBytes > 0 && trimmed && !trimmed.startsWith("#")) {
      const length = await fetchContentLength(trimmed, candidate);

      if (length > prefixBytes) {
        output.push(`#EXT-X-BYTERANGE:${length - prefixBytes}@${prefixBytes}`);
      }
    }

    output.push(absoluteLine);

    if (prefixBytes > 0 && !hasVersion && trimmed === "#EXTM3U") {
      output.push("#EXT-X-VERSION:4");
    }
  }

  return {
    playlist: output.join("\n"),
    transformed: prefixBytes > 0,
    prefixBytes
  };
}

function buildSimplePlaylist(candidate) {
  const referrer = candidate.pageUrl || "";
  const userAgent = candidate.userAgent || navigator.userAgent || "";

  return [
    "#EXTM3U",
    "#EXTINF:-1,DLP Stream",
    referrer ? `#EXTVLCOPT:http-referrer=${referrer}` : "",
    userAgent ? `#EXTVLCOPT:http-user-agent=${userAgent}` : "",
    candidate.url
  ].filter(Boolean).join("\n");
}

async function buildStreamPlaylist(candidate) {
  if (candidate.type === "hls") {
    return buildVlcReadyHlsPlaylist(candidate);
  }

  return {
    playlist: buildSimplePlaylist(candidate),
    transformed: false,
    prefixBytes: 0
  };
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
      const mediaPair = findMediaPair(candidates);

      const url = chooseDownloadUrl(preferredUrl, {
        settings,
        pageUrl: tab?.url || info.pageUrl,
        candidates,
        preservePreferredUrl: Boolean(info.linkUrl && !isLikelyMediaUrl(info.linkUrl))
      });

      sendDownloadWithSettings(url, {
        title: tab?.title || "",
        pageUrl: tab?.url || info.pageUrl || "",
        userAgent: navigator.userAgent || "",
        audioUrl: mediaPair?.videoUrl === getSafeHttpsUrl(url) ? mediaPair.audioUrl : ""
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
      const mediaPair = findMediaPair(candidates);
      const url = chooseDownloadUrl(preferredUrl, {
        settings,
        pageUrl: message.pageUrl || sender.tab?.url,
        candidates
      });

      sendDownloadWithSettings(url, {
        title,
        pageUrl: message.pageUrl || sender.tab?.url || "",
        userAgent: message.userAgent || navigator.userAgent || "",
        audioUrl: mediaPair?.videoUrl === getSafeHttpsUrl(url) ? mediaPair.audioUrl : ""
      }, settings, sendResponse);
    });
    return true;
  }

  if (message.type === "dlp-remember-stream-candidate") {
    const tabId = sender.tab?.id;
    const sourceUrl = sender.url || sender.tab?.url || "";
    const candidate = normalizeCandidate({
      ...(message.candidate || {}),
      pageUrl: message.candidate?.pageUrl || sourceUrl,
      origin: message.candidate?.origin || getUrlOrigin(sourceUrl),
      userAgent: message.candidate?.userAgent || navigator.userAgent || ""
    });

    if (typeof tabId === "number" && tabId >= 0 && candidate) {
      const list = getTabCandidates(tabId);
      list.push(candidate);
      tabCandidates.set(tabId, pruneCandidates(list));
    }

    sendResponse({ ok: Boolean(candidate) });
    return false;
  }

  if (message.type === "dlp-get-stream-candidates") {
    const candidates = rankCandidates([
      ...(Array.isArray(message.candidates) ? message.candidates : []),
      ...getTabCandidates(sender.tab?.id)
    ]).filter((candidate) =>
      ["hls", "dash", "direct-mp4", "direct-webm", "direct-video", "direct-audio"].includes(candidate.type));

    sendResponse({
      ok: true,
      candidates
    });
    return false;
  }

  if (message.type === "dlp-open-stream") {
    const candidate = normalizeCandidate({
      ...(message.candidate || {}),
      pageUrl: message.candidate?.pageUrl || sender.url || sender.tab?.url || "",
      origin: message.candidate?.origin || getUrlOrigin(sender.url || sender.tab?.url || ""),
      userAgent: message.candidate?.userAgent || navigator.userAgent || ""
    });

    if (!candidate || candidate.type !== "hls") {
      sendResponse({
        ok: false,
        message: "Live playback needs an HLS stream"
      });
      return false;
    }

    sendNativePayload({
      action: "open_stream",
      url: candidate.url,
      title: message.title || sender.tab?.title || "",
      pageUrl: candidate.pageUrl || sender.url || sender.tab?.url || "",
      userAgent: candidate.userAgent || navigator.userAgent || "",
      experimental: true,
      source: "chrome-extension",
      timestamp: new Date().toISOString()
    }, sendResponse);
    return true;
  }

  if (message.type === "dlp-build-stream-playlist") {
    const candidate = normalizeCandidate({
      ...(message.candidate || {}),
      pageUrl: message.candidate?.pageUrl || sender.url || sender.tab?.url || "",
      origin: message.candidate?.origin || getUrlOrigin(sender.url || sender.tab?.url || ""),
      userAgent: message.candidate?.userAgent || navigator.userAgent || ""
    });

    if (!candidate) {
      sendResponse({
        ok: false,
        message: "Missing stream URL"
      });
      return false;
    }

    buildStreamPlaylist(candidate).then(
      (result) => {
        sendResponse({
          ok: true,
          playlist: result.playlist,
          transformed: result.transformed,
          prefixBytes: result.prefixBytes
        });
      },
      (error) => {
        sendResponse({
          ok: false,
          message: error?.message || "Could not build stream playlist"
        });
      }
    );
    return true;
  }

  return false;
});

if (chrome.webRequest?.onBeforeRequest) {
  chrome.webRequest.onBeforeRequest.addListener(
    (details) => {
      if (settingsCache.experimentalAllSites || settingsCache.streamOverlay) {
        rememberNetworkCandidate(details);
        return;
      }

      if (settingsCacheLoaded) {
        return;
      }

      getSettings((settings) => {
        if (settings.experimentalAllSites || settings.streamOverlay) {
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

if (chrome.webRequest?.onBeforeRedirect) {
  chrome.webRequest.onBeforeRedirect.addListener(
    (details) => {
      if (!details.redirectUrl) {
        return;
      }

      const redirectedDetails = {
        ...details,
        url: details.redirectUrl
      };

      if (settingsCache.experimentalAllSites || settingsCache.streamOverlay) {
        rememberNetworkCandidate(redirectedDetails, "network.redirect");
        return;
      }

      if (settingsCacheLoaded) {
        return;
      }

      getSettings((settings) => {
        if (settings.experimentalAllSites || settings.streamOverlay) {
          rememberNetworkCandidate(redirectedDetails, "network.redirect");
        }
      });
    },
    {
      urls: ["https://*/*"],
      types: ["main_frame", "sub_frame", "media", "xmlhttprequest", "other"]
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
