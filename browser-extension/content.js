(function () {
  const BUTTON_ID = "dlp-video-download-button";
  const STYLE_ID = "dlp-video-download-style";
  const TOAST_ID = "dlp-video-download-toast";
  const BUTTON_WIDTH = 58;
  const BUTTON_HEIGHT = 24;
  const BUTTON_OFFSET = 12;
  const AUTO_HIDE_DELAY_MS = 2600;
  const DEEP_SCAN_WAIT_MS = 1600;
  const EXPERIMENTAL_POLL_MS = 120;
  const MEDIA_URL_RE = /\.(m3u8|mpd|mp4|webm|m4v|mov)(?:[?#]|$)/i;
  const STREAM_URL_RE = /(?:playlist|manifest|master|index)\.(?:m3u8|mpd)(?:[?#]|$)/i;
  const MEDIA_QUERY_RE = /[?&](?:file|filename|name|src)=[^&#]+\.(?:m3u8|mpd|mp4|webm|m4v|mov)(?:[&#]|$)/i;
  const AUDIO_ITAG_RE = /(?:^|[?&#])itag=(?:139|140|141|249|250|251)(?:[&#]|$)/i;
  const DEFAULT_SETTINGS = {
    silentDownload: false,
    autoHideOverlay: true,
    overlayPosition: "auto",
    experimentalAllSites: false,
    deepScanner: false,
    browserCookies: false,
    cookieBrowser: "brave"
  };

  let lastUrl = location.href;
  let refreshTimer = null;
  let hideTimer = null;
  let toastTimer = null;
  let lastActivityAt = 0;
  let extensionActive = true;
  let observer = null;
  let settings = { ...DEFAULT_SETTINGS };
  let tikTokScriptUrlCache = null;
  let tikTokScriptUrlCacheAt = 0;
  let tikTokItemsCache = null;
  let tikTokItemsCacheAt = 0;

  function hasRuntime() {
    try {
      return Boolean(
        extensionActive
          && globalThis.chrome
          && chrome.runtime
          && chrome.runtime.id
          && typeof chrome.runtime.sendMessage === "function"
      );
    } catch {
      return false;
    }
  }

  function deactivateExtensionUi() {
    extensionActive = false;
    window.clearTimeout(refreshTimer);
    window.clearTimeout(hideTimer);
    window.clearTimeout(toastTimer);
    removeButton();

    if (observer) {
      observer.disconnect();
    }
  }

  function loadSettings(callback) {
    if (!hasRuntime() || !chrome.storage || !chrome.storage.local) {
      if (callback) {
        callback();
      }

      return;
    }

    chrome.storage.local.get(DEFAULT_SETTINGS, (storedSettings) => {
      if (chrome.runtime.lastError) {
        console.log("DLP settings error:", chrome.runtime.lastError.message);
      } else {
        settings = { ...DEFAULT_SETTINGS, ...storedSettings };
      }

      if (callback) {
        callback();
      }
    });
  }

  function watchSettingsChanges() {
    if (!hasRuntime() || !chrome.storage || !chrome.storage.onChanged) {
      return;
    }

    chrome.storage.onChanged.addListener((changes, areaName) => {
      if (areaName !== "local") {
        return;
      }

      let changed = false;

      for (const key of Object.keys(DEFAULT_SETTINGS)) {
        if (Object.prototype.hasOwnProperty.call(changes, key)) {
          settings[key] = changes[key].newValue ?? DEFAULT_SETTINGS[key];
          changed = true;
        }
      }

      if (changed) {
        showButtonForInteraction();
        scheduleRefresh();
      }
    });
  }

  function getPlatform() {
    const host = location.hostname.toLowerCase();

    if (["youtube.com", "www.youtube.com", "m.youtube.com"].includes(host)) {
      return "youtube";
    }

    if (["tiktok.com", "www.tiktok.com", "m.tiktok.com", "vm.tiktok.com", "vt.tiktok.com"].includes(host)) {
      return "tiktok";
    }

    if (["instagram.com", "www.instagram.com", "m.instagram.com"].includes(host)) {
      return "instagram";
    }

    if (["x.com", "www.x.com", "mobile.x.com", "twitter.com", "www.twitter.com", "mobile.twitter.com"].includes(host)) {
      return "x";
    }

    if (["soundcloud.com", "www.soundcloud.com", "m.soundcloud.com", "on.soundcloud.com"].includes(host)) {
      return "soundcloud";
    }

    return null;
  }

  function isYouTubeShortsPage() {
    return getPlatform() === "youtube" && location.pathname.startsWith("/shorts/");
  }

  function toAbsoluteUrl(href) {
    try {
      return new URL(href, location.origin).href;
    } catch {
      return null;
    }
  }

  function isTikTokVideoUrl(url) {
    try {
      const parsed = new URL(url);
      return /\/@[^/]+\/video\/\d+/i.test(parsed.pathname)
        || ["vm.tiktok.com", "vt.tiktok.com"].includes(parsed.hostname.toLowerCase());
    } catch {
      return false;
    }
  }

  function normalizeTikTokVideoUrl(url) {
    try {
      const parsed = new URL(url, location.origin);

      if (["vm.tiktok.com", "vt.tiktok.com"].includes(parsed.hostname.toLowerCase())) {
        return parsed.href;
      }

      const match = parsed.pathname.match(/^\/@([^/]+)\/video\/(\d+)/i);

      if (!match) {
        return null;
      }

      return `https://www.tiktok.com/@${match[1]}/video/${match[2]}`;
    } catch {
      return null;
    }
  }

  function findTikTokVideoUrlInText(text) {
    if (!text) {
      return null;
    }

    const normalizedText = text
      .replace(/\\u002F/g, "/")
      .replace(/\\\//g, "/")
      .replace(/&amp;/g, "&");

    const absoluteMatch = normalizedText.match(/https?:\/\/(?:www\.)?tiktok\.com\/@[A-Za-z0-9._-]+\/video\/\d+/i);

    if (absoluteMatch) {
      return normalizeTikTokVideoUrl(absoluteMatch[0]);
    }

    const relativeMatch = normalizedText.match(/\/@[A-Za-z0-9._-]+\/video\/\d+/i);

    if (relativeMatch) {
      return normalizeTikTokVideoUrl(relativeMatch[0]);
    }

    return null;
  }

  function findTikTokVideoUrlInScripts() {
    const now = Date.now();

    if (tikTokScriptUrlCache && now - tikTokScriptUrlCacheAt < 2000) {
      return tikTokScriptUrlCache;
    }

    const scripts = Array.from(document.scripts);

    for (const script of scripts) {
      const text = script.textContent || "";

      if (!text.includes("/video/") && !text.includes("\\/video\\/")) {
        continue;
      }

      const url = findTikTokVideoUrlInText(text);

      if (url) {
        tikTokScriptUrlCache = url;
        tikTokScriptUrlCacheAt = now;
        return url;
      }
    }

    tikTokScriptUrlCacheAt = now;
    return null;
  }

  function readStringProperty(value, names) {
    if (!value || typeof value !== "object") {
      return null;
    }

    for (const name of names) {
      const propertyValue = value[name];

      if (typeof propertyValue === "string" && propertyValue.trim()) {
        return propertyValue.trim();
      }

      if (typeof propertyValue === "number") {
        return String(propertyValue);
      }
    }

    return null;
  }

  function normalizeTikTokUsername(username) {
    return username ? username.replace(/^@/, "").trim().toLowerCase() : null;
  }

  function getTikTokItemId(item) {
    const directId = readStringProperty(item, ["id", "aweme_id", "awemeId", "itemId"]);

    if (directId && /^\d{10,}$/.test(directId)) {
      return directId;
    }

    const videoId = readStringProperty(item.video, ["id"]);

    return videoId && /^\d{10,}$/.test(videoId) ? videoId : null;
  }

  function getTikTokItemUsername(item) {
    const directUsername = readStringProperty(item, ["authorUniqueId", "author_unique_id", "uniqueId", "unique_id"]);

    if (directUsername) {
      return directUsername;
    }

    const author = item.author || item.authorInfo || item.authorStats;

    return readStringProperty(author, ["uniqueId", "unique_id", "nickname", "name"]);
  }

  function looksLikeTikTokVideoItem(item) {
    return Boolean(
      item
        && typeof item === "object"
        && (item.video || item.author || item.music || item.stats || item.desc || item.createTime)
    );
  }

  function collectTikTokItems(value, items, depth) {
    if (!value || depth > 18 || items.length > 300) {
      return;
    }

    if (Array.isArray(value)) {
      for (const item of value) {
        collectTikTokItems(item, items, depth + 1);
      }

      return;
    }

    if (typeof value !== "object") {
      return;
    }

    const itemId = looksLikeTikTokVideoItem(value) ? getTikTokItemId(value) : null;
    const username = itemId ? getTikTokItemUsername(value) : null;

    if (itemId && username) {
      items.push({
        id: itemId,
        username: normalizeTikTokUsername(username)
      });
    }

    for (const child of Object.values(value)) {
      collectTikTokItems(child, items, depth + 1);
    }
  }

  function getTikTokItemsFromPageData() {
    const now = Date.now();

    if (tikTokItemsCache && now - tikTokItemsCacheAt < 2500) {
      return tikTokItemsCache;
    }

    const items = [];

    for (const script of Array.from(document.scripts)) {
      const text = script.textContent?.trim();

      if (!text || (!text.includes("uniqueId") && !text.includes("aweme") && !text.includes("itemStruct"))) {
        continue;
      }

      try {
        collectTikTokItems(JSON.parse(text), items, 0);
      } catch {
        // TikTok mixes JSON and non-JSON scripts; only JSON state blocks are useful here.
      }
    }

    tikTokItemsCache = items;
    tikTokItemsCacheAt = now;

    return items;
  }

  function findTikTokVideoUrlInPageData(username) {
    const normalizedUsername = normalizeTikTokUsername(username);

    if (!normalizedUsername) {
      return null;
    }

    const item = getTikTokItemsFromPageData()
      .find((candidate) => candidate.username === normalizedUsername);

    return item ? buildTikTokVideoUrl(item.username, item.id) : null;
  }

  function isXStatusUrl(url) {
    try {
      const parsed = new URL(url);
      return /\/[^/]+\/status\/\d+/i.test(parsed.pathname);
    } catch {
      return false;
    }
  }

  function normalizeXStatusUrl(url) {
    try {
      const parsed = new URL(url);
      const match = parsed.pathname.match(/^\/([^/]+)\/status\/(\d+)/i);

      if (!match) {
        return null;
      }

      return `${parsed.origin}/${match[1]}/status/${match[2]}`;
    } catch {
      return null;
    }
  }

  function findFirstMatchingLink(container, predicate) {
    if (!container) {
      return null;
    }

    const links = Array.from(container.querySelectorAll("a[href]"));

    for (const link of links) {
      const url = toAbsoluteUrl(link.getAttribute("href"));

      if (url && predicate(url)) {
        return url;
      }
    }

    return null;
  }

  function getElementAncestors(element, maxDepth) {
    const ancestors = [];
    let current = element;

    while (current && ancestors.length < maxDepth) {
      ancestors.push(current);
      current = current.parentElement;
    }

    return ancestors;
  }

  function getAttributeValue(element, names) {
    for (const name of names) {
      const value = element.getAttribute?.(name);

      if (!value) {
        continue;
      }

      return value;
    }

    return null;
  }

  function findTikTokVideoId(container) {
    if (!container) {
      return null;
    }

    const attributeNames = ["data-id", "data-item-id", "data-aweme-id", "data-video-id", "data-videoid", "itemid"];
    const containerValue = getAttributeValue(container, attributeNames);
    const containerMatch = containerValue?.match(/\d{10,}/);

    if (containerMatch) {
      return containerMatch[0];
    }

    const elements = Array.from(container.querySelectorAll("*"));

    for (const element of elements) {
      const value = getAttributeValue(element, attributeNames);
      const match = value?.match(/\d{10,}/);

      if (match) {
        return match[0];
      }
    }

    const html = container.outerHTML || "";
    const htmlMatch = html.match(/\/video\/(\d{10,})/i)
      || html.match(/"videoId"\s*:\s*"(\d{10,})"/i)
      || html.match(/"aweme_id"\s*:\s*"(\d{10,})"/i)
      || html.match(/"awemeId"\s*:\s*"(\d{10,})"/i);

    return htmlMatch ? htmlMatch[1] : null;
  }

  function findTikTokUsername(container) {
    if (!container) {
      return null;
    }

    const links = Array.from(container.querySelectorAll('a[href^="/@"]'));

    for (const link of links) {
      const match = link.getAttribute("href")?.match(/^\/@([^/?#]+)/);

      if (match) {
        return match[1];
      }
    }

    const usernameElement = container.querySelector('[data-e2e="video-author-uniqueid"]')
      || container.querySelector('[data-e2e="browse-username"]')
      || container.querySelector('[data-e2e="feed-user-name"]')
      || container.querySelector('[data-e2e="user-title"]')
      || container.querySelector('a[href^="/@"] span')
      || container.querySelector('a[href^="/@"]');

    const text = usernameElement?.textContent?.trim() || "";
    const usernameMatch = text.match(/@?([A-Za-z0-9._-]{2,24})/);

    return usernameMatch ? usernameMatch[1] : null;
  }

  function buildTikTokVideoUrl(username, videoId) {
    if (!username || !videoId) {
      return null;
    }

    return `https://www.tiktok.com/@${username}/video/${videoId}`;
  }

  function isSupportedVideoPage() {
    const platform = getPlatform();

    if (platform === "youtube") {
      if (location.pathname === "/watch") {
        return new URLSearchParams(location.search).has("v");
      }

      return isYouTubeShortsPage();
    }

    if (platform === "tiktok") {
      const path = location.pathname.toLowerCase();

      return path.includes("/video/")
        || location.hostname.toLowerCase() === "vm.tiktok.com"
        || location.hostname.toLowerCase() === "vt.tiktok.com"
        || Boolean(getTikTokVideoUrl());
    }

    if (platform === "instagram") {
      const path = location.pathname.toLowerCase();

      return path.startsWith("/reel/")
        || path.startsWith("/p/")
        || path.startsWith("/tv/");
    }

    if (platform === "x") {
      return location.pathname.toLowerCase().includes("/status/")
        || Boolean(getXStatusUrl());
    }

    if (platform === "soundcloud") {
      const path = location.pathname.toLowerCase();
      const ignoredPaths = ["/", "/discover", "/stream", "/you", "/upload", "/search"];

      return !ignoredPaths.some((ignoredPath) => path === ignoredPath || path.startsWith(`${ignoredPath}/`));
    }

    return Boolean(settings.experimentalAllSites && location.protocol === "https:" && getVisibleVideo());
  }

  function getDownloadUrl() {
    const platform = getPlatform();

    if (platform === "tiktok") {
      return getTikTokVideoUrl() || location.href;
    }

    if (platform === "x") {
      return getXStatusUrl() || location.href;
    }

    if (!platform && settings.experimentalAllSites) {
      return getExperimentalVideoUrl() || location.href;
    }

    return location.href;
  }

  function getMetaContent(selector) {
    return document.querySelector(selector)?.getAttribute("content")?.trim() || "";
  }

  function getMediaTitle() {
    const platform = getPlatform();
    let title = "";

    if (platform === "youtube") {
      title = document.querySelector("h1 yt-formatted-string")?.textContent?.trim()
        || document.querySelector("h1")?.textContent?.trim()
        || "";
    } else if (platform === "soundcloud") {
      title = document.querySelector(".soundTitle__title span")?.textContent?.trim()
        || document.querySelector("h1")?.textContent?.trim()
        || "";
    } else if (platform === "x") {
      title = getVisibleVideo()
        ?.closest("article")
        ?.querySelector('[data-testid="tweetText"]')
        ?.textContent
        ?.trim() || "";
    }

    title = title
      || getMetaContent('meta[property="og:title"]')
      || getMetaContent('meta[name="twitter:title"]')
      || document.title
      || "";

    return title.replace(/\s+/g, " ").trim();
  }

  function getExperimentalVideoUrl() {
    return getExperimentalCandidates(settings.deepScanner)[0]?.url || null;
  }

  function getExperimentalCandidates(deepScan) {
    const candidates = [];
    const video = getVisibleVideo();

    if (video) {
      candidates.push(
        createExperimentalCandidate(video.currentSrc, "video.currentSrc"),
        createExperimentalCandidate(video.src, "video.src"),
        ...Array.from(video.querySelectorAll("source[src]"), (source) =>
          createExperimentalCandidate(source.src, "source.src"))
      );
    }

    candidates.push(
      createExperimentalCandidate(getMetaContent('meta[property="og:video:secure_url"]'), "meta.og:video:secure_url"),
      createExperimentalCandidate(getMetaContent('meta[property="og:video:url"]'), "meta.og:video:url"),
      createExperimentalCandidate(getMetaContent('meta[property="og:video"]'), "meta.og:video"),
      createExperimentalCandidate(getMetaContent('meta[name="twitter:player:stream"]'), "meta.twitter:player:stream")
    );

    if (deepScan) {
      for (const entry of performance.getEntriesByType("resource")) {
        if (isLikelyMediaUrl(entry.name)) {
          candidates.push(createExperimentalCandidate(entry.name, "performance", performance.timeOrigin + entry.startTime));
        }
      }
    }

    return rankExperimentalCandidates(candidates.filter(Boolean));
  }

  function createExperimentalCandidate(rawUrl, source, time) {
    if (!rawUrl) {
      return null;
    }

    try {
      const parsed = new URL(rawUrl, location.href);

      if (parsed.protocol !== "https:") {
        return null;
      }

      return {
        url: parsed.href,
        type: getCandidateType(parsed.href),
        source,
        time: time || Date.now()
      };
    } catch {
      return null;
    }
  }

  function getCandidateType(url) {
    const mediaRole = getMediaRole(url);
    const extension = getMediaExtension(url);

    if (mediaRole === "audio") {
      return "direct-audio";
    }

    if (extension === "m3u8") {
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

    return "html5-video";
  }

  function getMediaExtension(url) {
    try {
      const parsed = new URL(url, location.href);
      const path = decodeURIComponent(parsed.pathname).replace(/\/+$/, "");
      const pathMatch = path.match(/\.(m3u8|mpd|mp4|webm|m4v|mov)$/i);

      if (pathMatch) {
        return pathMatch[1].toLowerCase();
      }

      const fileName = getQueryMediaFileName(parsed);
      const queryMatch = fileName.match(/\.(m3u8|mpd|mp4|webm|m4v|mov)$/i);
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

      if (/\.(?:m3u8|mpd|mp4|webm|m4v|mov)$/i.test(fileName)) {
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
      const parsed = new URL(url, location.href);
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

  function experimentalCandidateScore(candidate) {
    let score = 0;
    const ageMs = Date.now() - (candidate.time || 0);
    const url = candidate.url || "";

    if (candidate.type === "direct-audio") {
      score -= 80;
    } else if (candidate.type === "direct-mp4") {
      score += 130;
    } else if (candidate.type === "direct-webm" || candidate.type === "direct-video") {
      score += 125;
    } else if (candidate.type === "hls") {
      score += 120;
    } else if (candidate.type === "dash") {
      score += 85;
    } else {
      score += 60;
    }

    if (candidate.source === "video.currentSrc") {
      score += 140;
    } else if (candidate.source === "video.src" || candidate.source === "source.src") {
      score += 100;
    } else if (candidate.source === "performance") {
      score += 16;
    } else if (String(candidate.source).startsWith("meta.")) {
      score += 4;
    }

    score += Math.min(20, Math.max(0, 20 - (ageMs / 30000)));

    if (candidate.source === "performance" && ageMs > 120000) {
      score -= ageMs > 300000 ? 100 : 50;
    }

    score += mediaUrlShapeScore(url);

    return score;
  }

  function rankExperimentalCandidates(candidates) {
    const seen = new Set();

    return candidates
      .filter((candidate) => {
        if (!candidate?.url || seen.has(candidate.url)) {
          return false;
        }

        seen.add(candidate.url);
        return true;
      })
      .sort((first, second) => experimentalCandidateScore(second) - experimentalCandidateScore(first))
      .slice(0, 20);
  }

  function shouldWaitForExperimentalCandidates(forceExperimental, forceDeepScan) {
    return Boolean(!getPlatform() && (forceExperimental || settings.experimentalAllSites) && (forceDeepScan || settings.deepScanner));
  }

  function hasReadyExperimentalCandidate(candidates) {
    return candidates.some((candidate) =>
      candidate.type !== "direct-audio"
      && (candidate.source === "video.currentSrc"
        || candidate.source === "video.src"
        || candidate.source === "source.src"
        || isLikelyMediaUrl(candidate.url)));
  }

  function waitForExperimentalCandidates(callback, forceExperimental, forceDeepScan) {
    const deepScan = Boolean(forceDeepScan || settings.deepScanner);

    if (!shouldWaitForExperimentalCandidates(forceExperimental, forceDeepScan)) {
      callback(!getPlatform() && (forceExperimental || settings.experimentalAllSites)
        ? getExperimentalCandidates(false)
        : []);
      return;
    }

    const startedAt = Date.now();

    const poll = () => {
      const candidates = getExperimentalCandidates(deepScan);

      if (hasReadyExperimentalCandidate(candidates) || Date.now() - startedAt >= DEEP_SCAN_WAIT_MS) {
        callback(candidates);
        return;
      }

      window.setTimeout(poll, EXPERIMENTAL_POLL_MS);
    };

    poll();
  }

  function getVisibleVideo() {
    if (!extensionActive) {
      return null;
    }

    const videos = Array.from(document.querySelectorAll("video"));

    return videos
      .filter((video) => {
        const rect = video.getBoundingClientRect();
        return rect.width >= 120 && rect.height >= 120 && isRectVisible(rect);
      })
      .sort((first, second) => {
        const firstRect = first.getBoundingClientRect();
        const secondRect = second.getBoundingClientRect();
        return getVisibleArea(secondRect) - getVisibleArea(firstRect);
      })[0] || null;
  }

  function getYouTubePlayerElement() {
    if (isYouTubeShortsPage()) {
      const activeReel = document.querySelector("ytd-reel-video-renderer[is-active]");

      return activeReel?.querySelector("video")
        || getVisibleVideo()
        || activeReel?.querySelector("#movie_player")
        || activeReel?.querySelector(".html5-video-player")
        || activeReel
        || document.querySelector("#shorts-player");
    }

    return document.querySelector("ytd-reel-video-renderer[is-active] #movie_player")
      || document.querySelector("ytd-reel-video-renderer[is-active] .html5-video-player")
      || document.querySelector("ytd-reel-video-renderer[is-active] #player")
      || document.querySelector("#shorts-player")
      || document.querySelector("#movie_player")
      || document.querySelector(".html5-video-player")
      || document.querySelector("ytd-player")
      || document.querySelector("#player");
  }

  function getTikTokPlayerElement() {
    const video = document.querySelector('[data-e2e="browse-video"] video')
      || document.querySelector('[data-e2e="feed-video"] video')
      || document.querySelector('[data-e2e="video-container"] video')
      || getVisibleVideo();

    if (!video) {
      return null;
    }

    return video.closest('[data-e2e="browse-video"]')
      || video.closest('[data-e2e="feed-video"]')
      || video.closest('[data-e2e="video-container"]')
      || video.closest('[class*="VideoContainer"]')
      || video.closest('[class*="PlayerContainer"]')
      || video.parentElement;
  }

  function getTikTokVideoUrl() {
    if (isTikTokVideoUrl(location.href)) {
      return location.href;
    }

    const video = getVisibleVideo();

    if (!video) {
      return null;
    }

    const container = video.closest('[data-e2e="recommend-list-item-container"]')
      || video.closest('[data-e2e="browse-video"]')
      || video.closest('[data-e2e="feed-video"]')
      || video.closest('[data-e2e="video-container"]')
      || video.closest('[data-e2e="feed-item"]')
      || video.closest('[class*="DivItemContainer"]')
      || video.closest('[class*="DivVideoWrapper"]')
      || video.closest('[class*="VideoContainer"]')
      || video.closest('[class*="PlayerContainer"]')
      || video.parentElement;

    for (const ancestor of getElementAncestors(container, 10)) {
      const link = findFirstMatchingLink(ancestor, isTikTokVideoUrl);

      if (link) {
        return normalizeTikTokVideoUrl(link) || link;
      }

      const embeddedUrl = findTikTokVideoUrlInText(ancestor.outerHTML);

      if (embeddedUrl) {
        return embeddedUrl;
      }

      const videoId = findTikTokVideoId(ancestor);
      const username = findTikTokUsername(ancestor);
      const pageDataUrl = findTikTokVideoUrlInPageData(username);

      if (pageDataUrl) {
        return pageDataUrl;
      }

      const builtUrl = buildTikTokVideoUrl(username, videoId);

      if (builtUrl) {
        return builtUrl;
      }
    }

    const containerUsername = findTikTokUsername(container);
    const pageDataUrl = findTikTokVideoUrlInPageData(containerUsername);

    if (pageDataUrl) {
      return pageDataUrl;
    }

    const documentLink = findFirstMatchingLink(document, isTikTokVideoUrl);

    return documentLink ? normalizeTikTokVideoUrl(documentLink) || documentLink : findTikTokVideoUrlInScripts();
  }

  function getInstagramPlayerElement() {
    return getVisibleVideo();
  }

  function getXPlayerElement() {
    return getVisibleVideo();
  }

  function getXStatusUrl() {
    if (isXStatusUrl(location.href)) {
      return normalizeXStatusUrl(location.href);
    }

    const video = getVisibleVideo();

    if (!video) {
      return null;
    }

    const container = video.closest("article")
      || video.closest('[data-testid="tweet"]')
      || video.closest('[role="article"]')
      || video.parentElement;

    const statusUrl = findFirstMatchingLink(container, isXStatusUrl);

    return statusUrl ? normalizeXStatusUrl(statusUrl) : null;
  }

  function getSoundCloudPlayerElement() {
    return document.querySelector(".playControls")
      || document.querySelector(".soundTitle")
      || document.querySelector(".listenDetails")
      || document.querySelector('[class*="playControls"]')
      || document.querySelector('[class*="soundTitle"]')
      || document.querySelector('[role="main"]')
      || document.querySelector("main");
  }

  function getPlayerElement() {
    const platform = getPlatform();

    if (platform === "youtube") {
      return getYouTubePlayerElement();
    }

    if (platform === "tiktok") {
      return getTikTokPlayerElement();
    }

    if (platform === "instagram") {
      return getInstagramPlayerElement();
    }

    if (platform === "x") {
      return getXPlayerElement();
    }

    if (platform === "soundcloud") {
      return getSoundCloudPlayerElement();
    }

    if (settings.experimentalAllSites) {
      return getVisibleVideo();
    }

    return null;
  }

  function ensureStyle() {
    if (document.getElementById(STYLE_ID)) {
      return;
    }

    const style = document.createElement("style");
    style.id = STYLE_ID;
    style.textContent = `
      #${BUTTON_ID},
      #${TOAST_ID} {
        --dlp-bg: #0d1117;
        --dlp-surface: #151b23;
        --dlp-text-primary: #f2f7ff;
        --dlp-text-secondary: #b7c5d8;
        --dlp-border: #314155;
        --dlp-border-strong: #496178;
        --dlp-accent-active: #2f81f7;
        --dlp-accent-interactive: #58a6ff;
        --dlp-success: #44d17d;
        --dlp-error: #ff7b86;
        --dlp-media: #000000;
      }

      #${BUTTON_ID} {
        position: absolute;
        top: 12px;
        right: 12px;
        z-index: 2147483647;
        width: 58px;
        height: 24px;
        padding: 0;
        border: 1px solid color-mix(in srgb, var(--dlp-text-primary) 34%, transparent);
        border-radius: 6px;
        background: color-mix(in srgb, var(--dlp-surface) 30%, transparent);
        color: var(--dlp-text-primary);
        font: 700 11px/22px Arial, sans-serif;
        text-align: center;
        cursor: pointer;
        text-shadow: 0 1px 2px color-mix(in srgb, var(--dlp-media) 75%, transparent);
        opacity: 0.72;
        transition: background 160ms ease, border-color 160ms ease, opacity 160ms ease, transform 160ms ease;
        user-select: none;
      }

      #${BUTTON_ID}:hover {
        background: color-mix(in srgb, var(--dlp-text-primary) 16%, transparent);
        border-color: color-mix(in srgb, var(--dlp-text-primary) 58%, transparent);
        opacity: 0.95;
        transform: translateY(-1px);
      }

      #${BUTTON_ID}:disabled {
        cursor: default;
        opacity: 0.62;
        transform: none;
      }

      #${BUTTON_ID}[data-dlp-status="sending"] {
        background: color-mix(in srgb, var(--dlp-accent-interactive) 24%, transparent);
        border-color: color-mix(in srgb, var(--dlp-accent-interactive) 70%, transparent);
      }

      #${BUTTON_ID}[data-dlp-status="success"] {
        background: color-mix(in srgb, var(--dlp-success) 22%, var(--dlp-surface));
        border-color: color-mix(in srgb, var(--dlp-success) 78%, transparent);
      }

      #${BUTTON_ID}[data-dlp-status="error"] {
        background: color-mix(in srgb, var(--dlp-error) 18%, var(--dlp-surface));
        border-color: color-mix(in srgb, var(--dlp-error) 78%, transparent);
      }

      #${BUTTON_ID}.dlp-overlay-hidden {
        opacity: 0;
        pointer-events: none;
        transform: translateY(-4px);
      }

      #${TOAST_ID} {
        position: fixed;
        right: 18px;
        bottom: 18px;
        z-index: 2147483647;
        max-width: min(320px, calc(100vw - 36px));
        padding: 9px 12px;
        border: 1px solid color-mix(in srgb, var(--dlp-text-primary) 28%, transparent);
        border-radius: 6px;
        background: color-mix(in srgb, var(--dlp-bg) 92%, transparent);
        color: var(--dlp-text-primary);
        font: 600 12px/1.35 Arial, sans-serif;
        opacity: 0;
        transform: translateY(8px);
        pointer-events: none;
        transition: opacity 160ms ease, transform 160ms ease;
      }

      #${TOAST_ID}.dlp-toast-show {
        opacity: 1;
        transform: translateY(0);
      }

      #${TOAST_ID}.dlp-toast-success {
        border-color: color-mix(in srgb, var(--dlp-success) 62%, transparent);
      }

      #${TOAST_ID}.dlp-toast-error {
        border-color: color-mix(in srgb, var(--dlp-error) 62%, transparent);
      }
    `;

    document.documentElement.appendChild(style);
  }

  function setButtonText(button, text) {
    button.textContent = text;
  }

  function setButtonStatus(button, status, text) {
    button.dataset.dlpStatus = status;
    setButtonText(button, text);
  }

  function resetButtonStatus(button) {
    setButtonStatus(button, "idle", "DLP");
    button.disabled = false;
    syncAutoHideAfterPlacement(button);
  }

  function getToast() {
    let toast = document.getElementById(TOAST_ID);

    if (!toast) {
      toast = document.createElement("div");
      toast.id = TOAST_ID;
      document.body.appendChild(toast);
    }

    return toast;
  }

  function showToast(message, type) {
    if (!document.body) {
      return;
    }

    const toast = getToast();
    toast.textContent = message;
    toast.className = `dlp-toast-show dlp-toast-${type}`;

    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => {
      toast.classList.remove("dlp-toast-show");
    }, 2400);
  }

  function shouldAutoHideButton(button) {
    return Boolean(
      settings.autoHideOverlay
        && button
        && !button.disabled
        && !button.matches(":hover")
    );
  }

  function scheduleAutoHide(button) {
    window.clearTimeout(hideTimer);

    if (!shouldAutoHideButton(button)) {
      button?.classList.remove("dlp-overlay-hidden");
      return;
    }

    hideTimer = window.setTimeout(() => {
      if (!shouldAutoHideButton(button)) {
        button?.classList.remove("dlp-overlay-hidden");
        return;
      }

      button.classList.add("dlp-overlay-hidden");
    }, AUTO_HIDE_DELAY_MS);
  }

  function showButtonForInteraction(button) {
    const targetButton = button || document.getElementById(BUTTON_ID);

    if (!targetButton) {
      return;
    }

    targetButton.classList.remove("dlp-overlay-hidden");
    scheduleAutoHide(targetButton);
  }

  function syncAutoHideAfterPlacement(button) {
    if (!settings.autoHideOverlay) {
      window.clearTimeout(hideTimer);
      button.classList.remove("dlp-overlay-hidden");
      return;
    }

    if (!button.classList.contains("dlp-overlay-hidden")) {
      scheduleAutoHide(button);
    }
  }

  function handlePageActivity() {
    const now = Date.now();

    if (now - lastActivityAt < 180) {
      return;
    }

    lastActivityAt = now;
    showButtonForInteraction();
  }

  function sendDownload(button) {
    if (!hasRuntime()) {
      showToast("Reload the DLP extension", "error");
      deactivateExtensionUi();
      return;
    }

    setButtonStatus(button, "sending", "...");
    button.disabled = true;
    button.classList.remove("dlp-overlay-hidden");
    showToast("Sending to DLP", "success");

    waitForExperimentalCandidates((candidates) => {
      try {
        chrome.runtime.sendMessage(
          {
            type: "dlp-download-current-video",
            url: getDownloadUrl(),
            title: getMediaTitle(),
            pageUrl: location.href,
            userAgent: navigator.userAgent,
            deepScanner: Boolean(settings.deepScanner),
            candidates
          },
          (response) => {
            if (!hasRuntime()) {
              deactivateExtensionUi();
              return;
            }

            if (chrome.runtime.lastError) {
              setButtonStatus(button, "error", "ERR");
              showToast("DLP app connection failed", "error");
              console.log("DLP extension error:", chrome.runtime.lastError.message);
            } else if (!response || response.ok === false) {
              setButtonStatus(button, "error", "ERR");
              showToast(response?.message || "DLP request failed", "error");
              console.log("DLP native host response:", response);
            } else {
              setButtonStatus(button, "success", "OK");
              showToast("Sent to DLP", "success");
            }

            window.setTimeout(() => {
              if (!extensionActive) {
                return;
              }

              resetButtonStatus(button);
            }, 1600);
          }
        );
      } catch (error) {
        showToast("Reload the DLP extension", "error");
        console.log("DLP extension context ended:", error && error.message ? error.message : error);
        deactivateExtensionUi();
      }
    }, false, Boolean(settings.deepScanner));
  }

  function createButton() {
    const button = document.createElement("button");
    button.id = BUTTON_ID;
    button.type = "button";
    button.title = "Download with DLP";
    button.setAttribute("aria-label", "Download with DLP");
    setButtonStatus(button, "idle", "DLP");

    button.addEventListener("mouseenter", () => {
      showButtonForInteraction(button);
    });

    button.addEventListener("mouseleave", () => {
      scheduleAutoHide(button);
    });

    button.addEventListener("mousedown", (event) => {
      event.preventDefault();
      event.stopPropagation();
    });

    button.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      sendDownload(button);
    });

    return button;
  }

  function removeButton() {
    const existing = document.getElementById(BUTTON_ID);

    if (existing) {
      existing.remove();
    }
  }

  function isFixedOverlayPlatform(platform) {
    return (platform === "youtube" && isYouTubeShortsPage())
      || platform === "tiktok"
      || platform === "instagram"
      || platform === "x"
      || platform === "soundcloud";
  }

  function getOverlayPosition() {
    const value = settings.overlayPosition || DEFAULT_SETTINGS.overlayPosition;
    const allowedPositions = new Set([
      "auto",
      "top-right",
      "top-center",
      "top-left",
      "bottom-right",
      "bottom-center",
      "bottom-left"
    ]);

    return allowedPositions.has(value) ? value : DEFAULT_SETTINGS.overlayPosition;
  }

  function isRectVisible(rect) {
    return rect.width > 0
      && rect.height > 0
      && rect.bottom > 0
      && rect.right > 0
      && rect.top < window.innerHeight
      && rect.left < window.innerWidth;
  }

  function getVisibleArea(rect) {
    const visibleWidth = Math.max(0, Math.min(rect.right, window.innerWidth) - Math.max(rect.left, 0));
    const visibleHeight = Math.max(0, Math.min(rect.bottom, window.innerHeight) - Math.max(rect.top, 0));

    return visibleWidth * visibleHeight;
  }

  function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
  }

  function placeButtonAtPosition(button, target, position) {
    const rect = target.getBoundingClientRect();

    if (!isRectVisible(rect)) {
      button.style.display = "none";
      return;
    }

    let top = rect.top + BUTTON_OFFSET;
    let left = rect.right - BUTTON_WIDTH - BUTTON_OFFSET;

    if (position.startsWith("bottom")) {
      top = rect.bottom - BUTTON_HEIGHT - BUTTON_OFFSET;
    }

    if (position.endsWith("left")) {
      left = rect.left + BUTTON_OFFSET;
    } else if (position.endsWith("center")) {
      left = rect.left + (rect.width / 2) - (BUTTON_WIDTH / 2);
    }

    button.style.display = "";
    button.style.position = "fixed";
    button.style.top = `${clamp(top, BUTTON_OFFSET, window.innerHeight - BUTTON_HEIGHT - BUTTON_OFFSET)}px`;
    button.style.left = `${clamp(left, BUTTON_OFFSET, window.innerWidth - BUTTON_WIDTH - BUTTON_OFFSET)}px`;
    button.style.right = "auto";
    button.style.bottom = "auto";
    syncAutoHideAfterPlacement(button);
  }

  function placeFixedButton(button, target) {
    const rect = target.getBoundingClientRect();

    if (!isRectVisible(rect)) {
      button.style.display = "none";
      return;
    }

    const top = Math.max(rect.top + BUTTON_OFFSET, BUTTON_OFFSET);
    const left = Math.min(
      Math.max(rect.right - BUTTON_WIDTH - BUTTON_OFFSET, BUTTON_OFFSET),
      window.innerWidth - BUTTON_WIDTH - BUTTON_OFFSET
    );

    button.style.display = "";
    button.style.position = "fixed";
    button.style.top = `${top}px`;
    button.style.left = `${left}px`;
    button.style.right = "auto";
    button.style.bottom = "auto";
    syncAutoHideAfterPlacement(button);
  }

  function placeTopCenterButton(button, target) {
    const rect = target.getBoundingClientRect();

    if (!isRectVisible(rect)) {
      button.style.display = "none";
      return;
    }

    const top = Math.max(rect.top + BUTTON_OFFSET, BUTTON_OFFSET);
    const left = Math.min(
      Math.max(rect.left + (rect.width / 2) - (BUTTON_WIDTH / 2), BUTTON_OFFSET),
      window.innerWidth - BUTTON_WIDTH - BUTTON_OFFSET
    );

    button.style.display = "";
    button.style.position = "fixed";
    button.style.top = `${top}px`;
    button.style.left = `${left}px`;
    button.style.right = "auto";
    button.style.bottom = "auto";
    syncAutoHideAfterPlacement(button);
  }

  function placeSoundCloudButton(button, target) {
    const rect = target.getBoundingClientRect();

    button.style.display = "";
    button.style.position = "fixed";
    button.style.left = "auto";

    if (isRectVisible(rect)) {
      const top = Math.max(rect.top - 36, BUTTON_OFFSET);
      const right = Math.max(window.innerWidth - rect.right + BUTTON_OFFSET, BUTTON_OFFSET);

      button.style.top = `${top}px`;
      button.style.right = `${right}px`;
      button.style.bottom = "auto";
      syncAutoHideAfterPlacement(button);
      return;
    }

    button.style.top = "auto";
    button.style.right = "18px";
    button.style.bottom = "74px";
    syncAutoHideAfterPlacement(button);
  }

  function placeAnchoredButton(button, player) {
    const computedPosition = window.getComputedStyle(player).position;

    if (computedPosition === "static") {
      player.style.position = "relative";
    }

    button.style.display = "";
    button.style.position = "";
    button.style.top = "";
    button.style.left = "";
    button.style.right = "";

    if (button.parentElement !== player) {
      player.appendChild(button);
    }

    syncAutoHideAfterPlacement(button);
  }

  function ensureButton() {
    if (!extensionActive) {
      return;
    }

    if (!hasRuntime()) {
      deactivateExtensionUi();
      return;
    }

    ensureStyle();

    const platform = getPlatform();

    if (!isSupportedVideoPage()) {
      removeButton();
      return;
    }

    const player = getPlayerElement();

    if (!player) {
      removeButton();
      return;
    }

    let button = document.getElementById(BUTTON_ID);

    if (!button) {
      button = createButton();
    }

    const overlayPosition = getOverlayPosition();

    if (overlayPosition !== "auto") {
      if (button.parentElement !== document.body) {
        document.body.appendChild(button);
      }

      placeButtonAtPosition(button, player, overlayPosition);
      return;
    }

    if (isFixedOverlayPlatform(platform)) {
      if (button.parentElement !== document.body) {
        document.body.appendChild(button);
      }

      if (platform === "tiktok" || (platform === "youtube" && isYouTubeShortsPage())) {
        placeTopCenterButton(button, player);
        return;
      }

      if (platform === "soundcloud") {
        placeSoundCloudButton(button, player);
        return;
      }

      placeFixedButton(button, player);
      return;
    }

    placeAnchoredButton(button, player);
  }

  function scheduleRefresh() {
    if (!extensionActive) {
      return;
    }

    if (!hasRuntime()) {
      deactivateExtensionUi();
      return;
    }

    window.clearTimeout(refreshTimer);
    refreshTimer = window.setTimeout(ensureButton, 120);
  }

  function watchUrlChanges() {
    const notify = () => {
      if (location.href !== lastUrl) {
        lastUrl = location.href;
        showButtonForInteraction();
        scheduleRefresh();
      }
    };

    for (const methodName of ["pushState", "replaceState"]) {
      const original = history[methodName];

      history[methodName] = function () {
        const result = original.apply(this, arguments);
        notify();
        return result;
      };
    }

    window.addEventListener("popstate", notify);
    window.addEventListener("resize", scheduleRefresh);
    window.addEventListener("scroll", scheduleRefresh, true);
    document.addEventListener("yt-navigate-finish", scheduleRefresh);
  }

  function watchExtensionMessages() {
    if (!hasRuntime() || !chrome.runtime.onMessage) {
      return;
    }

    chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
      if (!message || message.type !== "dlp-scan-candidates") {
        return false;
      }

      const experimental = Boolean(settings.experimentalAllSites || message.experimentalAllSites);
      const deepScan = Boolean(settings.deepScanner || message.deepScanner);

      if (!getPlatform() && experimental) {
        waitForExperimentalCandidates((candidates) => {
          sendResponse({
            url: candidates[0]?.url || location.href,
            title: getMediaTitle(),
            candidates
          });
        }, true, deepScan);

        return true;
      }

      sendResponse({
        url: getDownloadUrl(),
        title: getMediaTitle(),
        candidates: []
      });

      return false;
    });
  }

  observer = new MutationObserver(scheduleRefresh);
  observer.observe(document.documentElement, {
    childList: true,
    subtree: true
  });

  document.addEventListener("mousemove", handlePageActivity, true);
  document.addEventListener("touchstart", handlePageActivity, true);
  document.addEventListener("keydown", handlePageActivity, true);
  window.addEventListener("scroll", handlePageActivity, true);

  watchSettingsChanges();
  watchExtensionMessages();
  watchUrlChanges();
  loadSettings(scheduleRefresh);
})();
