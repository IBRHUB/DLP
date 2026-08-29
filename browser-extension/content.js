(function () {
  const BUTTON_ID = "dlp-video-download-button";
  const ROTATE_BUTTON_ID = "dlp-video-rotate-button";
  const STREAM_PANEL_ID = "dlp-video-stream-panel";
  const STYLE_ID = "dlp-video-download-style";
  const STYLE_VERSION = "4";
  const TOAST_ID = "dlp-video-download-toast";
  const BUTTON_WIDTH = 30;
  const BUTTON_HEIGHT = 30;
  const ACTION_GAP = 2;
  const BUTTON_INSET = 4;
  const BUTTON_GAP = 3;
  const VIEWPORT_MARGIN = 8;
  const MIN_VISIBLE_MEDIA_EDGE = 24;
  const AUTO_HIDE_DELAY_MS = 2600;
  const STREAM_PANEL_HIDE_DELAY_MS = 4200;
  const DEEP_SCAN_WAIT_MS = 1600;
  const INSTAGRAM_SCAN_WAIT_MS = 3500;
  const EXPERIMENTAL_POLL_MS = 120;
  const MAX_PAGE_STREAM_CANDIDATES = 50;
  const MAX_INSTAGRAM_SCRIPT_SCAN_CHARS = 3000000;
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
  const OVERLAY_POSITIONS = new Set([
    "auto",
    "top-right",
    "top-center",
    "top-left",
    "bottom-right",
    "bottom-center",
    "bottom-left"
  ]);
  const AUTO_PLACEMENTS = Object.freeze({
    "youtube-watch": ["inside-top-right", "inside-top-left"],
    "youtube-shorts": ["outside-right-top", "outside-left-top", "inside-top-right"],
    tiktok: ["outside-right-top", "outside-left-top", "inside-top-right"],
    "instagram-reels": ["outside-right-top", "outside-left-top", "inside-top-right"],
    "instagram-modal": ["inside-top-right", "inside-top-left"],
    "instagram-feed": ["inside-top-right", "inside-top-left"],
    x: ["inside-top-right", "inside-top-left"],
    soundcloud: ["above-right", "below-right"],
    generic: ["above-right", "inside-top-right", "below-right", "inside-top-left"]
  });
  const MANUAL_PLACEMENTS = Object.freeze({
    "top-right": ["above-right", "inside-top-right", "below-right"],
    "top-center": ["above-center", "inside-top-center", "below-center"],
    "top-left": ["above-left", "inside-top-left", "below-left"],
    "bottom-right": ["below-right", "inside-bottom-right", "above-right"],
    "bottom-center": ["below-center", "inside-bottom-center", "above-center"],
    "bottom-left": ["below-left", "inside-bottom-left", "above-left"]
  });

  let lastUrl = location.href;
  let refreshTimer = null;
  let placementFrame = null;
  let hideTimer = null;
  let streamPanelHideTimer = null;
  let toastTimer = null;
  let lastActivityAt = 0;
  let lastPointerX = -1;
  let lastPointerY = -1;
  let extensionActive = true;
  let observer = null;
  let targetResizeObserver = null;
  let observedTarget = null;
  let settings = { ...DEFAULT_SETTINGS };
  let pageStreamCandidates = [];
  let tikTokScriptUrlCache = null;
  let tikTokScriptUrlCacheAt = 0;
  let tikTokItemsCache = null;
  let tikTokItemsCacheAt = 0;
  let lastInstagramMediaContext = "";
  let instagramCandidatesStartedAt = Date.now();
  const videoRotationStates = new WeakMap();

  function normalizeSettings(storedSettings) {
    const values = {
      ...DEFAULT_SETTINGS,
      ...(storedSettings && typeof storedSettings === "object" ? storedSettings : {})
    };

    return {
      silentDownload: Boolean(values.silentDownload),
      autoHideOverlay: Boolean(values.autoHideOverlay),
      overlayPosition: OVERLAY_POSITIONS.has(values.overlayPosition)
        ? values.overlayPosition
        : DEFAULT_SETTINGS.overlayPosition,
      experimentalAllSites: Boolean(values.experimentalAllSites),
      deepScanner: Boolean(values.deepScanner),
      streamOverlay: Boolean(values.streamOverlay),
      browserCookies: Boolean(values.browserCookies),
      cookieBrowser: String(values.cookieBrowser || DEFAULT_SETTINGS.cookieBrowser).toLowerCase()
    };
  }

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
    window.clearTimeout(streamPanelHideTimer);
    window.clearTimeout(toastTimer);

    if (placementFrame !== null) {
      window.cancelAnimationFrame(placementFrame);
      placementFrame = null;
    }

    removeButton();

    if (observer) {
      observer.disconnect();
    }

    if (targetResizeObserver) {
      targetResizeObserver.disconnect();
      targetResizeObserver = null;
      observedTarget = null;
    }
  }

  function installPageStreamHook() {
    const target = document.documentElement || document.head || document.body;

    if (!target) {
      document.addEventListener("DOMContentLoaded", installPageStreamHook, { once: true });
      return;
    }

    const script = document.createElement("script");
    script.textContent = `(${function () {
      if (window.__DLP_STREAM_HOOK__) {
        return;
      }

      window.__DLP_STREAM_HOOK__ = true;

      const mediaUrlRe = /https?:\/\/[^\s"'<>\\]+?\.(?:m3u8|m3u|mpd|mp4|webm|m4v|mov)(?:[^\s"'<>\\]*)?/gi;
      const relativeMediaUrlRe = /(?:^|[\s"'(])((?:\/|\.\.?\/)[^\s"'<>\\]+?\.(?:m3u8|m3u|mpd|mp4|webm|m4v|mov)(?:[^\s"'<>\\]*)?)/gi;

      function clean(value) {
        return String(value || "")
          .replace(/\\u0026/gi, "&")
          .replace(/\\\//g, "/")
          .replace(/&amp;/gi, "&");
      }

      function emit(rawUrl, source) {
        try {
          const url = new URL(clean(rawUrl), location.href);

          if (url.protocol === "https:") {
            window.postMessage({
              type: "dlp-stream-candidate",
              url: url.href,
              candidateSource: source,
              pageUrl: location.href,
              origin: location.origin,
              userAgent: navigator.userAgent
            }, "*");
          }
        } catch {
          // Ignore values that are not URLs.
        }
      }

      function scanText(text, source, allowDecode) {
        if (!text || typeof text !== "string" || text.length > 2000000) {
          return;
        }

        const body = clean(text);

        mediaUrlRe.lastIndex = 0;

        for (const match of body.matchAll(mediaUrlRe)) {
          emit(match[0], source);
        }

        relativeMediaUrlRe.lastIndex = 0;

        let relativeMatch = relativeMediaUrlRe.exec(body);

        while (relativeMatch) {
          emit(relativeMatch[1], source);
          relativeMatch = relativeMediaUrlRe.exec(body);
        }

        if (allowDecode && /%3a%2f%2f|%2f|%2e(?:m3u8|m3u|mpd|mp4|webm|m4v|mov)/i.test(body)) {
          try {
            const decoded = decodeURIComponent(body);

            if (decoded !== body) {
              scanText(decoded, source, false);
            }
          } catch {
            // Ignore malformed encoded response text.
          }
        }
      }

      function shouldReadResponse(response, url) {
        const contentType = response?.headers?.get?.("content-type") || "";
        return /\.(?:m3u8|m3u|mpd)(?:[?#]|$)/i.test(url || "")
          || /json|text|xml|mpegurl|dash|javascript/i.test(contentType);
      }

      if (typeof window.fetch === "function" && !window.fetch.__dlpHooked) {
        const originalFetch = window.fetch;

        window.fetch = function () {
          const requestUrl = typeof arguments[0] === "string" ? arguments[0] : arguments[0]?.url;
          emit(requestUrl, "fetch.request");

          return originalFetch.apply(this, arguments).then((response) => {
            try {
              const responseUrl = response.url || requestUrl || "";
              emit(responseUrl, "fetch.response");

              if (shouldReadResponse(response, responseUrl)) {
                response.clone().text().then((text) => {
                  scanText(text, "fetch.body", true);
                }).catch(() => {});
              }
            } catch {
              // Keep page fetch behavior unchanged.
            }

            return response;
          });
        };

        window.fetch.__dlpHooked = true;
      }

      if (window.XMLHttpRequest?.prototype && !window.XMLHttpRequest.prototype.__dlpHooked) {
        const originalOpen = window.XMLHttpRequest.prototype.open;
        const originalSend = window.XMLHttpRequest.prototype.send;

        window.XMLHttpRequest.prototype.open = function (method, url) {
          this.__dlpStreamUrl = url ? String(url) : "";
          emit(this.__dlpStreamUrl, "xhr.request");
          return originalOpen.apply(this, arguments);
        };

        window.XMLHttpRequest.prototype.send = function () {
          this.addEventListener("loadend", () => {
            try {
              emit(this.responseURL || this.__dlpStreamUrl, "xhr.response");

              if (typeof this.responseText === "string") {
                scanText(this.responseText, "xhr.body", true);
              }
            } catch {
              // Keep page XHR behavior unchanged.
            }
          }, { once: true });

          return originalSend.apply(this, arguments);
        };

        window.XMLHttpRequest.prototype.__dlpHooked = true;
      }

      function patchHls() {
        const hlsPrototype = window.Hls?.prototype;

        if (!hlsPrototype || hlsPrototype.__dlpHooked || typeof hlsPrototype.loadSource !== "function") {
          return Boolean(hlsPrototype?.__dlpHooked);
        }

        const originalLoadSource = hlsPrototype.loadSource;

        hlsPrototype.loadSource = function (url) {
          emit(url, "hls.loadSource");
          return originalLoadSource.apply(this, arguments);
        };

        hlsPrototype.__dlpHooked = true;
        return true;
      }

      function patchClappr() {
        const clappr = window.Clappr;

        if (!clappr?.Player || clappr.Player.__dlpHooked) {
          return Boolean(clappr?.Player?.__dlpHooked);
        }

        const OriginalPlayer = clappr.Player;

        function DlpPlayerWrapper() {
          const options = arguments[0] || {};

          if (options.source) {
            emit(options.source, "clappr.source");
          }

          return Reflect.construct(OriginalPlayer, Array.from(arguments), new.target || DlpPlayerWrapper);
        }

        Object.setPrototypeOf(DlpPlayerWrapper, OriginalPlayer);
        DlpPlayerWrapper.prototype = OriginalPlayer.prototype;
        DlpPlayerWrapper.__dlpHooked = true;
        clappr.Player = DlpPlayerWrapper;
        return true;
      }

      let playerPatchAttempts = 0;
      const playerPatchTimer = window.setInterval(() => {
        playerPatchAttempts += 1;

        const hlsReady = patchHls();
        const clapprReady = patchClappr();

        if ((hlsReady && clapprReady) || playerPatchAttempts >= 40) {
          window.clearInterval(playerPatchTimer);
        }
      }, 250);

      patchHls();
      patchClappr();
    }.toString()})();`;

    target.appendChild(script);
    script.remove();
  }

  function watchPageStreamMessages() {
    window.addEventListener("message", (event) => {
      if (event.source !== window || !event.data || event.data.type !== "dlp-stream-candidate") {
        return;
      }

      rememberPageStreamCandidate(event.data.url, event.data.candidateSource || "page.stream", {
        pageUrl: event.data.pageUrl || "",
        origin: event.data.origin || "",
        userAgent: event.data.userAgent || ""
      });
    }, true);
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
        settings = normalizeSettings(storedSettings);
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

      const updatedSettings = { ...settings };
      let changed = false;

      for (const key of Object.keys(DEFAULT_SETTINGS)) {
        if (Object.prototype.hasOwnProperty.call(changes, key)) {
          updatedSettings[key] = changes[key].newValue ?? DEFAULT_SETTINGS[key];
          changed = true;
        }
      }

      if (changed) {
        const previousStreamOverlay = settings.streamOverlay;
        settings = normalizeSettings(updatedSettings);

        if (previousStreamOverlay && !settings.streamOverlay) {
          removeStreamPanel();
        }

        showButtonForInteraction();
        scheduleRefresh();
      }
    });
  }

  function getPlatform() {
    const host = location.hostname.toLowerCase();

    if ([
      "youtube.com",
      "www.youtube.com",
      "m.youtube.com",
      "music.youtube.com",
      "youtu.be",
      "youtube-nocookie.com",
      "www.youtube-nocookie.com"
    ].includes(host)) {
      return "youtube";
    }

    if (["tiktok.com", "www.tiktok.com", "m.tiktok.com", "vm.tiktok.com", "vt.tiktok.com"].includes(host)) {
      return "tiktok";
    }

    if (isInstagramHost(host) || isInstagramCdnHost(host)) {
      return "instagram";
    }

    if (["x.com", "www.x.com", "mobile.x.com", "twitter.com", "www.twitter.com", "mobile.twitter.com", "video.twimg.com"].includes(host)) {
      return "x";
    }

    if (["soundcloud.com", "www.soundcloud.com", "m.soundcloud.com", "on.soundcloud.com"].includes(host)) {
      return "soundcloud";
    }

    return null;
  }

  function isTopFrame() {
    try {
      return window.top === window;
    } catch {
      return false;
    }
  }

  function isYouTubeShortsPage() {
    return getPlatform() === "youtube" && location.pathname.startsWith("/shorts/");
  }

  function isYouTubeEmbedPage() {
    return getPlatform() === "youtube"
      && /^\/(?:embed|v)\//i.test(location.pathname);
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
      const path = parsed.pathname.toLowerCase();
      if (parsed.hostname.toLowerCase() === "video.twimg.com") {
        return /\.(?:mp4|m3u8|m3u|mov|m4v)(?:$|[?#])/i.test(parsed.pathname)
          || path.includes("/amplify_video/")
          || path.includes("/ext_tw_video/")
          || path.includes("/tweet_video/");
      }

      return /^\/(?:i\/web\/status\/\d+|[^/]+\/status\/\d+|statuses\/\d+)(?:\/(?:video|photo)\/\d+)?(?:$|\/)?/i.test(path)
        || /^\/i\/(?:cards\/tfw\/v1|videos(?:\/tweet)?)\/\d+/i.test(path)
        || path.startsWith("/i/broadcasts/")
        || path.startsWith("/i/spaces/");
    } catch {
      return false;
    }
  }

  function normalizeXStatusUrl(url) {
    try {
      const parsed = new URL(url);
      const host = parsed.hostname.toLowerCase();
      const path = parsed.pathname;

      if (host === "video.twimg.com") {
        return `${parsed.origin}${path}${parsed.search || ""}`;
      }

      const statusMatch = path.match(/^\/([^/]+)\/status\/(\d+)(\/(?:video|photo)\/\d+)?/i);

      if (statusMatch) {
        return `${parsed.origin}/${statusMatch[1]}/status/${statusMatch[2]}${statusMatch[3] || ""}`;
      }

      const statusesMatch = path.match(/^\/statuses\/(\d+)(\/(?:video|photo)\/\d+)?/i);

      if (statusesMatch) {
        return `${parsed.origin}/statuses/${statusesMatch[1]}${statusesMatch[2] || ""}`;
      }

      const systemPathMatch = path.match(/^\/i\/(?:web\/status|(?:cards\/tfw\/v1|videos(?:\/tweet)?))\/([^/?#]+)/i);

      if (systemPathMatch) {
        return `${parsed.origin}${path}`;
      }

      return path.toLowerCase().startsWith("/i/broadcasts/")
        || path.toLowerCase().startsWith("/i/spaces/")
        ? `${parsed.origin}${path}`
        : null;
    } catch {
      return null;
    }
  }

  function isInstagramHost(host) {
    return ["instagram.com", "www.instagram.com", "m.instagram.com"].includes(host);
  }

  function isInstagramCdnHost(host) {
    return host === "cdninstagram.com"
      || host.endsWith(".cdninstagram.com")
      || (host.endsWith(".fbcdn.net")
        && (host.startsWith("video.")
          || host.startsWith("scontent.")
          || host.includes("instagram")));
  }

  function normalizeInstagramMediaUrl(url) {
    try {
      const parsed = new URL(url, location.origin);
      const host = parsed.hostname.toLowerCase();
      const path = parsed.pathname;

      if (parsed.protocol !== "https:") {
        return null;
      }

      if (isInstagramCdnHost(host)) {
        return isLikelyMediaUrl(parsed.href) ? parsed.href : null;
      }

      if (!isInstagramHost(host)) {
        return null;
      }

      const postMatch = path.match(/^\/(?:(?!share\/)[^/]+\/)?(p|tv|reels?)\/([^/?#&]+)/i);

      if (postMatch) {
        const mediaType = postMatch[1].toLowerCase() === "reel"
          ? "reels"
          : postMatch[1].toLowerCase();
        return `https://www.instagram.com/${mediaType}/${postMatch[2]}/`;
      }

      const storyMatch = path.match(/^\/stories\/(highlights\/\d+|[^/?#]+(?:\/\d+)?)/i);

      if (storyMatch) {
        return `https://www.instagram.com/stories/${storyMatch[1]}/`;
      }
    } catch {
      return null;
    }

    return null;
  }

  function isInstagramMediaUrl(url) {
    return Boolean(normalizeInstagramMediaUrl(url));
  }

  function isInstagramVideoPageUrl(url) {
    try {
      const parsed = new URL(url, location.origin);
      return isInstagramMediaUrl(url)
        && !/^\/reels\/audio(?:\/|$)/i.test(parsed.pathname);
    } catch {
      return false;
    }
  }

  function decodeEscapedMediaText(text) {
    return String(text || "")
      .replace(/\\u([0-9a-f]{4})/gi, (_, code) => String.fromCharCode(parseInt(code, 16)))
      .replace(/\\x([0-9a-f]{2})/gi, (_, code) => String.fromCharCode(parseInt(code, 16)))
      .replace(/\\\//g, "/")
      .replace(/&amp;/gi, "&");
  }

  function cleanExtractedInstagramMediaUrl(rawUrl) {
    try {
      const cleaned = decodeEscapedMediaText(rawUrl)
        .replace(/[),;\]}]+$/g, "");
      const parsed = new URL(cleaned, location.href);
      const host = parsed.hostname.toLowerCase();

      if (parsed.protocol !== "https:" || (!isInstagramCdnHost(host) && !isInstagramHost(host))) {
        return null;
      }

      return isLikelyMediaUrl(parsed.href) ? parsed.href : null;
    } catch {
      return null;
    }
  }

  function extractInstagramMediaUrlsFromText(text) {
    if (!text || typeof text !== "string") {
      return [];
    }

    const body = decodeEscapedMediaText(text);
    const urlRe = /https:\/\/[^\s"'<>\\]+?\.(?:m3u8|m3u|mpd|mp4|webm|m4v|mov)(?:[^\s"'<>\\]*)?/gi;
    const urls = [];
    const seen = new Set();

    for (const match of body.matchAll(urlRe)) {
      const url = cleanExtractedInstagramMediaUrl(match[0]);

      if (url && !seen.has(url)) {
        seen.add(url);
        urls.push(url);
      }
    }

    return urls;
  }

  function getInstagramScriptMediaCandidates(context) {
    const candidates = [];
    const seen = new Set();
    let remainingBudget = MAX_INSTAGRAM_SCRIPT_SCAN_CHARS;

    for (const script of Array.from(document.scripts)) {
      if (remainingBudget <= 0) {
        break;
      }

      const text = script.textContent || "";

      if (!text) {
        continue;
      }

      const scanText = text.length > remainingBudget
        ? text.slice(0, remainingBudget)
        : text;
      remainingBudget -= scanText.length;

      for (const url of extractInstagramMediaUrlsFromText(scanText)) {
        if (seen.has(url)) {
          continue;
        }

        seen.add(url);
        candidates.push(createExperimentalCandidate(url, "instagram.script", undefined, context));
      }
    }

    return candidates;
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
      if (["youtu.be"].includes(location.hostname.toLowerCase())) {
        return location.pathname.length > 1;
      }

      if (location.pathname === "/watch") {
        return new URLSearchParams(location.search).has("v");
      }

      return isYouTubeShortsPage()
        || isYouTubeEmbedPage()
        || location.pathname.startsWith("/live/")
        || location.pathname.startsWith("/clip/");
    }

    if (platform === "tiktok") {
      const path = location.pathname.toLowerCase();

      return path.includes("/video/")
        || location.hostname.toLowerCase() === "vm.tiktok.com"
        || location.hostname.toLowerCase() === "vt.tiktok.com"
        || Boolean(getTikTokVideoUrl());
    }

    if (platform === "instagram") {
      return Boolean(getInstagramMediaUrl());
    }

    if (platform === "x") {
      return isXStatusUrl(location.href)
        || Boolean(getXStatusUrl());
    }

    if (platform === "soundcloud") {
      const path = location.pathname.toLowerCase();
      const ignoredPaths = ["/", "/discover", "/stream", "/you", "/upload", "/search"];

      return !ignoredPaths.some((ignoredPath) => path === ignoredPath || path.startsWith(`${ignoredPath}/`));
    }

    return Boolean(
      (settings.experimentalAllSites || settings.streamOverlay)
        && location.protocol === "https:"
        && (getVisibleVideo() || (isTopFrame() && getVisibleMediaFrame()) || pageStreamCandidates.length)
    );
  }

  function getDownloadUrl(preferredVideo = null) {
    const platform = getPlatform();

    if (platform === "tiktok") {
      return getTikTokVideoUrl() || location.href;
    }

    if (platform === "x") {
      return getXStatusUrl() || location.href;
    }

    if (platform === "instagram") {
      return getInstagramMediaUrl(preferredVideo) || location.href;
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

  function getExperimentalCandidates(deepScan, preferredVideo = null) {
    const candidates = [];
    const platform = getPlatform();
    const isInstagramPage = platform === "instagram";
    const visibleVideo = isInstagramPage
      ? preferredVideo || getInstagramActiveVideo()
      : getVisibleVideo();
    const instagramMediaUrl = isInstagramPage ? getInstagramMediaUrl(visibleVideo) : null;
    const candidateContext = isInstagramPage
      ? { mediaPageUrl: instagramMediaUrl, video: visibleVideo }
      : undefined;

    if (isInstagramPage) {
      syncInstagramMediaContext(visibleVideo, instagramMediaUrl);
    }

    const videos = [visibleVideo].filter(Boolean);

    for (const video of videos) {
      const sourcePrefix = isInstagramPage ? "instagram.video.visible" : "video";
      candidates.push(
        createExperimentalCandidate(video.currentSrc, `${sourcePrefix}.currentSrc`, undefined, candidateContext),
        createExperimentalCandidate(video.src, `${sourcePrefix}.src`, undefined, candidateContext),
        ...Array.from(video.querySelectorAll("source[src]"), (source) =>
          createExperimentalCandidate(source.src, `${sourcePrefix}.source`, undefined, candidateContext))
      );
    }

    candidates.push(
      createExperimentalCandidate(getMetaContent('meta[property="og:video:secure_url"]'), "meta.og:video:secure_url", undefined, candidateContext),
      createExperimentalCandidate(getMetaContent('meta[property="og:video:url"]'), "meta.og:video:url", undefined, candidateContext),
      createExperimentalCandidate(getMetaContent('meta[property="og:video"]'), "meta.og:video", undefined, candidateContext),
      createExperimentalCandidate(getMetaContent('meta[name="twitter:player:stream"]'), "meta.twitter:player:stream", undefined, candidateContext)
    );

    if (deepScan || isInstagramPage) {
      for (const entry of performance.getEntriesByType("resource")) {
        const entryTime = performance.timeOrigin + entry.startTime;

        if (isLikelyMediaUrl(entry.name)
            && (!isInstagramPage || entryTime >= instagramCandidatesStartedAt)) {
          candidates.push(createExperimentalCandidate(
            entry.name,
            isInstagramPage ? "instagram.performance" : "performance",
            entryTime,
            candidateContext));
        }
      }
    }

    candidates.push(...pageStreamCandidates);

    if (isInstagramPage) {
      const hasLiveMediaCandidate = candidates.some((candidate) =>
        isInstagramLiveMediaCandidate(candidate));

      if (!visibleVideo || !hasLiveMediaCandidate) {
        candidates.push(...getInstagramScriptMediaCandidates(candidateContext));
      }
    }

    return rankExperimentalCandidates(candidates.filter(Boolean));
  }

  function isInstagramLiveMediaCandidate(candidate) {
    return Boolean(candidate
      && candidate.source !== "instagram.script"
      && [
        "hls",
        "dash",
        "direct-mp4",
        "direct-webm",
        "direct-video"
      ].includes(candidate.type)
      && isLikelyMediaUrl(candidate.url));
  }

  function createExperimentalCandidate(rawUrl, source, time, context) {
    if (!rawUrl) {
      return null;
    }

    try {
      const parsed = new URL(rawUrl, location.href);

      if (parsed.protocol !== "https:") {
        return null;
      }

      const mediaPageUrl = getPlatform() === "instagram"
        ? context?.mediaPageUrl || getInstagramMediaUrl(context?.video)
        : null;

      return {
        url: parsed.href,
        type: getCandidateType(parsed.href),
        source,
        time: time || Date.now(),
        pageUrl: mediaPageUrl || context?.pageUrl || location.href,
        origin: context?.origin || location.origin,
        userAgent: context?.userAgent || navigator.userAgent || ""
      };
    } catch {
      return null;
    }
  }

  function rememberPageStreamCandidate(rawUrl, source, context) {
    const candidate = createExperimentalCandidate(rawUrl, source || "page.stream", undefined, context);

    if (!candidate) {
      return;
    }

    pageStreamCandidates = rankExperimentalCandidates([...pageStreamCandidates, candidate])
      .slice(0, MAX_PAGE_STREAM_CANDIDATES);

    if (settings.streamOverlay) {
      scheduleRefresh();
    }

    sendStreamCandidateToBackground(candidate);
  }

  function syncInstagramMediaContext(visibleVideo, mediaPageUrl) {
    const videoSource = visibleVideo?.currentSrc || visibleVideo?.src || "";
    const contextKey = `${mediaPageUrl || location.href}|${videoSource}`;

    if (!lastInstagramMediaContext) {
      lastInstagramMediaContext = contextKey;
      return;
    }

    if (contextKey === lastInstagramMediaContext) {
      return;
    }

    lastInstagramMediaContext = contextKey;
    resetInstagramMediaCandidates();
    lastInstagramMediaContext = contextKey;
  }

  function resetInstagramMediaCandidates() {
    instagramCandidatesStartedAt = Date.now();
    pageStreamCandidates = [];
    clearBackgroundTabCandidates();
  }

  function clearBackgroundTabCandidates() {
    if (!hasRuntime()) {
      return;
    }

    try {
      chrome.runtime.sendMessage({ type: "dlp-clear-tab-candidates" }, () => {
        void chrome.runtime.lastError;
      });
    } catch {
      // The page may outlive an extension reload.
    }
  }

  function sendStreamCandidateToBackground(candidate) {
    if (!hasRuntime()) {
      return;
    }

    try {
      chrome.runtime.sendMessage({
        type: "dlp-remember-stream-candidate",
        candidate
      }, () => {
        void chrome.runtime.lastError;
      });
    } catch {
      // The page may outlive an extension reload.
    }
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

    return "html5-video";
  }

  function getMediaExtension(url) {
    try {
      const parsed = new URL(url, location.href);
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

  function decodeInstagramBase64Value(value) {
    if (!value) {
      return "";
    }

    try {
      const normalized = String(value)
        .replace(/-/g, "+")
        .replace(/_/g, "/");
      const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, "=");
      return atob(padded);
    } catch {
      return "";
    }
  }

  function getInstagramCdnSignalText(parsedUrl) {
    const efg = decodeInstagramBase64Value(parsedUrl.searchParams.get("efg"));
    const ncVs = decodeInstagramBase64Value(parsedUrl.searchParams.get("_nc_vs"));

    return `${parsedUrl.pathname} ${parsedUrl.search} ${efg} ${ncVs}`.toLowerCase();
  }

  function isInstagramByteRangeUrl(url) {
    try {
      const parsed = new URL(url, location.href);
      return parsed.searchParams.has("bytestart") || parsed.searchParams.has("byteend");
    } catch {
      return false;
    }
  }

  function getInstagramCdnMediaRole(url) {
    try {
      const parsed = new URL(url, location.href);

      if (!isInstagramCdnHost(parsed.hostname.toLowerCase())) {
        return "unknown";
      }

      const efgText = decodeInstagramBase64Value(parsed.searchParams.get("efg")).toLowerCase();
      const signalText = getInstagramCdnSignalText(parsed);
      const audioRe = /(?:^|[._/-])(?:audio|heaac|mp4a|aac|opus|dash_audio|audio_dashinit)(?:[._/-]|$)/;
      const videoRe = /(?:^|[._/-])(?:video|progressive|dash_baseline|avc|h264|h265|vp9|av01|vencode)(?:[._/-]|$)|(?:^|[._/-])(?:144|240|360|480|576|720|1080|1440|2160)(?:[._/-]|$)/;

      if (audioRe.test(efgText)) {
        return "audio";
      }

      if (videoRe.test(efgText)) {
        return "video";
      }

      if (audioRe.test(signalText) && !videoRe.test(signalText)) {
        return "audio";
      }

      if (videoRe.test(signalText)) {
        return "video";
      }
    } catch {
      return "unknown";
    }

    return "unknown";
  }

  function getMediaRole(url) {
    try {
      const parsed = new URL(url);
      const instagramRole = getInstagramCdnMediaRole(url);

      if (instagramRole !== "unknown") {
        return instagramRole;
      }

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

      if (isInstagramByteRangeUrl(url)) {
        score -= 500;
      }

      if (isInstagramCdnHost(parsed.hostname.toLowerCase())) {
        const role = getInstagramCdnMediaRole(url);

        if (role === "video") {
          score += 260;
        } else if (role === "audio") {
          score -= 650;
        }

        if (!isInstagramByteRangeUrl(url)) {
          score += 120;
        }
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

    if (String(candidate.source).startsWith("instagram.video.visible.")) {
      score += 240;
    } else if (String(candidate.source).startsWith("instagram.video.")) {
      score += 60;
    } else if (candidate.source === "video.currentSrc") {
      score += 140;
    } else if (candidate.source === "video.src" || candidate.source === "source.src") {
      score += 100;
    } else if (candidate.source === "instagram.performance") {
      score += 55;
    } else if (candidate.source === "instagram.script") {
      score += 5;
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
      .sort((first, second) => experimentalCandidateScore(second) - experimentalCandidateScore(first)
        || (second.time || 0) - (first.time || 0))
      .slice(0, 20);
  }

  function shouldWaitForExperimentalCandidates(forceExperimental, forceDeepScan) {
    return Boolean(getPlatform() === "instagram"
      || (!getPlatform()
        && (forceExperimental || settings.experimentalAllSites)
        && (forceDeepScan || settings.deepScanner)));
  }

  function hasReadyExperimentalCandidate(candidates) {
    return candidates.some((candidate) =>
      candidate.type !== "direct-audio"
      && (candidate.source === "video.currentSrc"
        || candidate.source === "video.src"
        || candidate.source === "source.src"
        || String(candidate.source).startsWith("instagram.video.")
        || candidate.source === "instagram.performance"
        || candidate.source === "instagram.script"
        || isLikelyMediaUrl(candidate.url)));
  }

  function waitForExperimentalCandidates(callback, forceExperimental, forceDeepScan, preferredVideo = null) {
    const platform = getPlatform();
    const deepScan = Boolean(forceDeepScan || settings.deepScanner);

    if (!shouldWaitForExperimentalCandidates(forceExperimental, forceDeepScan)) {
      callback(platform === "instagram"
        ? getExperimentalCandidates(false, preferredVideo)
        : !platform && (forceExperimental || settings.experimentalAllSites)
        ? getExperimentalCandidates(false, preferredVideo)
        : []);
      return;
    }

    const startedAt = Date.now();
    const waitMs = platform === "instagram" ? INSTAGRAM_SCAN_WAIT_MS : DEEP_SCAN_WAIT_MS;

    const poll = () => {
      const candidates = getExperimentalCandidates(deepScan, preferredVideo);

      if (hasReadyExperimentalCandidate(candidates) || Date.now() - startedAt >= waitMs) {
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
      .map((video) => {
        const rect = video.getBoundingClientRect();

        if (rect.width < 96
            || rect.height < 96
            || !isElementRenderable(video, rect)) {
          return null;
        }

        let score = getVisibleArea(rect);

        if (!video.paused && !video.ended) {
          score += 1_500_000;
        }

        if (video.currentTime > 0) {
          score += 80_000;
        }

        if (video.controls) {
          score += 20_000;
        }

        if (video.closest("main, article, [role=\"main\"], [role=\"article\"]")) {
          score += 10_000;
        }

        if (video.muted && video.loop && video.autoplay && !video.controls) {
          score -= 300_000;
        }

        return { video, score };
      })
      .filter(Boolean)
      .sort((first, second) => second.score - first.score)[0]?.video || null;
  }

  function getInstagramActiveVideo() {
    if (!extensionActive) {
      return null;
    }

    return Array.from(document.querySelectorAll("video"))
      .map((video) => {
        const rect = video.getBoundingClientRect();

        if (rect.width < 96
            || rect.height < 96
            || !isElementRenderable(video, rect)) {
          return null;
        }

        let score = getVisibleArea(rect);

        if (!video.paused && !video.ended) {
          score += 2_000_000;
        }

        if (video.currentTime > 0) {
          score += 100_000;
        }

        if (video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA) {
          score += 10_000;
        }

        return { video, score };
      })
      .filter(Boolean)
      .sort((first, second) => second.score - first.score)[0]?.video || null;
  }

  function getVisibleMediaFrame() {
    if (!extensionActive) {
      return null;
    }

    const frames = Array.from(document.querySelectorAll("iframe[src]"));

    return frames
      .filter((frame) => {
        const rect = frame.getBoundingClientRect();

        return frame.src.startsWith("https://")
          && rect.width >= 140
          && rect.height >= 90
          && isElementRenderable(frame, rect);
      })
      .sort((first, second) => {
        const firstRect = first.getBoundingClientRect();
        const secondRect = second.getBoundingClientRect();
        return getVisibleArea(secondRect) - getVisibleArea(firstRect);
      })[0] || null;
  }

  function getBestVisibleElement(elements) {
    const uniqueElements = Array.from(new Set((elements || []).filter(Boolean)));
    const visibleElements = uniqueElements
      .map((element) => ({ element, rect: element.getBoundingClientRect() }))
      .filter(({ element, rect }) => isElementRenderable(element, rect))
      .sort((first, second) => getVisibleArea(second.rect) - getVisibleArea(first.rect));

    return visibleElements[0]?.element || uniqueElements[0] || null;
  }

  function getYouTubePlayerElement() {
    if (isYouTubeShortsPage()) {
      const activeReel = document.querySelector("ytd-reel-video-renderer[is-active]");

      return getBestVisibleElement([
        activeReel?.querySelector("#movie_player"),
        activeReel?.querySelector(".html5-video-player"),
        activeReel?.querySelector("video"),
        activeReel,
        getVisibleVideo(),
        document.querySelector("#shorts-player")
      ]);
    }

    return getBestVisibleElement([
      document.querySelector("ytd-reel-video-renderer[is-active] #movie_player"),
      document.querySelector("ytd-reel-video-renderer[is-active] .html5-video-player"),
      document.querySelector("ytd-reel-video-renderer[is-active] #player"),
      document.querySelector("#shorts-player"),
      document.querySelector("#movie_player"),
      document.querySelector(".html5-video-player"),
      document.querySelector("ytd-player"),
      document.querySelector("#player"),
      document.querySelector(".html5-video-container"),
      document.querySelector("video"),
      getVisibleVideo()
    ]);
  }

  function getTikTokPlayerElement() {
    const video = getBestVisibleElement([
      document.querySelector('[data-e2e="browse-video"] video'),
      document.querySelector('[data-e2e="feed-video"] video'),
      document.querySelector('[data-e2e="video-container"] video'),
      getVisibleVideo()
    ]);

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
    return getInstagramActiveVideo();
  }

  function getInstagramMediaUrl(preferredVideo = null) {
    const currentUrl = normalizeInstagramMediaUrl(location.href);
    const video = preferredVideo || getInstagramActiveVideo();
    const container = video?.closest("article")
      || video?.closest('[role="dialog"]')
      || video?.closest("main")
      || document;
    const linkedUrl = findFirstMatchingLink(container, isInstagramVideoPageUrl);
    const linkedMediaUrl = linkedUrl ? normalizeInstagramMediaUrl(linkedUrl) : null;

    if (preferredVideo && linkedMediaUrl) {
      return linkedMediaUrl;
    }

    if (currentUrl) {
      return currentUrl;
    }

    const canonicalUrl = normalizeInstagramMediaUrl(
      document.querySelector('link[rel="canonical"]')?.href
        || getMetaContent('meta[property="og:url"]')
        || getMetaContent('meta[name="twitter:url"]'));

    if (canonicalUrl) {
      return canonicalUrl;
    }

    return linkedMediaUrl;
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

    if (settings.experimentalAllSites || settings.streamOverlay) {
      return getVisibleVideo() || (isTopFrame() ? getVisibleMediaFrame() : null);
    }

    return null;
  }

  function ensureStyle() {
    const existingStyle = document.getElementById(STYLE_ID);

    if (existingStyle?.dataset.dlpStyleVersion === STYLE_VERSION) {
      return;
    }

    existingStyle?.remove();

    const style = document.createElement("style");
    style.id = STYLE_ID;
    style.dataset.dlpStyleVersion = STYLE_VERSION;
    style.textContent = `
      #${BUTTON_ID},
      #${ROTATE_BUTTON_ID},
      #${STREAM_PANEL_ID},
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

      #${BUTTON_ID},
      #${ROTATE_BUTTON_ID} {
        /* Keep the hit target, but let the page remain visible through it. */
        all: initial;
        position: fixed !important;
        z-index: 2147483647 !important;
        display: inline-flex !important;
        align-items: center !important;
        justify-content: center !important;
        gap: 0 !important;
        width: 30px !important;
        min-width: 30px !important;
        max-width: 30px !important;
        height: 30px !important;
        padding: 7px !important;
        border: 0 !important;
        border-radius: 0 !important;
        background: transparent !important;
        color: #fff !important;
        direction: ltr !important;
        font: 650 12px/1 system-ui, -apple-system, "Segoe UI", sans-serif !important;
        letter-spacing: 0 !important;
        text-align: center !important;
        text-decoration: none !important;
        text-indent: 0 !important;
        text-shadow: none !important;
        white-space: nowrap !important;
        cursor: pointer !important;
        opacity: 0.72;
        box-shadow: none !important;
        transition: opacity 160ms ease, transform 160ms ease;
        user-select: none;
        -webkit-user-select: none;
        isolation: auto;
        overflow: visible;
      }

      #${BUTTON_ID},
      #${BUTTON_ID} *,
      #${ROTATE_BUTTON_ID},
      #${ROTATE_BUTTON_ID} * {
        box-sizing: border-box !important;
      }

      #${BUTTON_ID}[hidden],
      #${ROTATE_BUTTON_ID}[hidden] {
        display: none !important;
      }

      #${BUTTON_ID} .dlp-button-icon,
      #${ROTATE_BUTTON_ID} .dlp-button-icon {
        display: inline-flex !important;
        flex: 0 0 auto !important;
        align-items: center !important;
        justify-content: center !important;
        width: 16px !important;
        height: 16px !important;
        color: #fff !important;
        /* The icon adapts to the pixels behind it instead of adding a fixed accent. */
        mix-blend-mode: difference;
      }

      #${BUTTON_ID} .dlp-button-icon svg,
      #${ROTATE_BUTTON_ID} .dlp-button-icon svg {
        display: block !important;
        width: 16px !important;
        height: 16px !important;
        fill: none !important;
        stroke: currentColor !important;
        stroke-linecap: round !important;
        stroke-linejoin: round !important;
        stroke-width: 1.8 !important;
      }

      #${BUTTON_ID} .dlp-button-label {
        position: absolute !important;
        width: 1px !important;
        height: 1px !important;
        padding: 0 !important;
        margin: -1px !important;
        overflow: hidden !important;
        clip: rect(0, 0, 0, 0) !important;
        clip-path: inset(50%) !important;
        white-space: nowrap !important;
      }

      #${BUTTON_ID}:hover,
      #${ROTATE_BUTTON_ID}:hover {
        opacity: 1;
        transform: scale(1.08);
      }

      #${BUTTON_ID}:focus-visible,
      #${ROTATE_BUTTON_ID}:focus-visible {
        outline: 2px solid var(--dlp-accent-interactive) !important;
        outline-offset: 3px !important;
      }

      #${BUTTON_ID}:disabled,
      #${ROTATE_BUTTON_ID}:disabled {
        cursor: default !important;
        opacity: 0.48;
        transform: none;
      }

      #${BUTTON_ID}[data-dlp-status="sending"] {
        opacity: 0.86;
      }

      #${BUTTON_ID}[data-dlp-status="sending"] .dlp-button-icon svg {
        animation: dlp-download-pulse 900ms ease-in-out infinite;
      }

      #${BUTTON_ID}[data-dlp-status="success"] {
        opacity: 1;
      }

      #${BUTTON_ID}[data-dlp-status="error"] {
        opacity: 0.86;
      }

      #${ROTATE_BUTTON_ID}[data-dlp-rotation]:not([data-dlp-rotation="0"]) {
        opacity: 1;
      }

      #${BUTTON_ID}.dlp-overlay-hidden,
      #${ROTATE_BUTTON_ID}.dlp-overlay-hidden {
        opacity: 0;
        pointer-events: none;
        transform: translateY(-4px);
      }

      @keyframes dlp-download-pulse {
        0%, 100% { opacity: 0.55; transform: translateY(0); }
        50% { opacity: 1; transform: translateY(2px); }
      }

      #${STREAM_PANEL_ID} {
        position: fixed;
        z-index: 2147483647;
        width: min(560px, calc(100vw - 20px));
        max-height: min(330px, calc(100vh - 40px));
        border: 1px solid color-mix(in srgb, var(--dlp-text-primary) 24%, transparent);
        border-radius: 8px;
        background: color-mix(in srgb, var(--dlp-surface) 46%, transparent);
        color: var(--dlp-text-primary);
        box-shadow: 0 18px 52px color-mix(in srgb, #000 46%, transparent);
        overflow: hidden;
        backdrop-filter: blur(12px) saturate(1.1);
        opacity: 1;
        transform: translateY(0) scale(1);
        transition: opacity 150ms ease, transform 150ms ease;
      }

      #${STREAM_PANEL_ID},
      #${STREAM_PANEL_ID} * {
        box-sizing: border-box;
      }

      #${STREAM_PANEL_ID}.dlp-stream-panel-hidden {
        opacity: 0;
        pointer-events: none;
        transform: translateY(-4px) scale(0.985);
      }

      #${STREAM_PANEL_ID} .dlp-stream-head,
      #${STREAM_PANEL_ID} .dlp-stream-row {
        display: grid;
        align-items: center;
        gap: 8px;
      }

      #${STREAM_PANEL_ID} .dlp-stream-head {
        grid-template-columns: minmax(0, 1fr) auto auto;
        gap: 8px;
        min-height: 56px;
        padding: 10px 14px;
        border-bottom: 1px solid color-mix(in srgb, var(--dlp-text-primary) 13%, transparent);
      }

      #${STREAM_PANEL_ID} .dlp-stream-title-wrap {
        min-width: 0;
        display: flex;
        align-items: baseline;
        gap: 8px;
      }

      #${STREAM_PANEL_ID} .dlp-stream-title {
        font: 800 13px/1.2 Arial, sans-serif;
      }

      #${STREAM_PANEL_ID} .dlp-stream-count {
        color: color-mix(in srgb, var(--dlp-text-secondary) 82%, transparent);
        font: 700 11px/1.2 Arial, sans-serif;
      }

      #${STREAM_PANEL_ID} .dlp-stream-list {
        display: grid;
        gap: 6px;
        max-height: 264px;
        padding: 10px 12px 12px;
        overflow: auto;
        scrollbar-width: thin;
        scrollbar-color: transparent transparent;
      }

      #${STREAM_PANEL_ID}:hover .dlp-stream-list {
        scrollbar-color: color-mix(in srgb, var(--dlp-text-primary) 30%, transparent) transparent;
      }

      #${STREAM_PANEL_ID} .dlp-stream-list::-webkit-scrollbar {
        width: 8px;
      }

      #${STREAM_PANEL_ID} .dlp-stream-list::-webkit-scrollbar-thumb {
        border: 2px solid transparent;
        border-radius: 999px;
        background: transparent;
        background-clip: padding-box;
      }

      #${STREAM_PANEL_ID}:hover .dlp-stream-list::-webkit-scrollbar-thumb {
        background: color-mix(in srgb, var(--dlp-text-primary) 28%, transparent);
        background-clip: padding-box;
      }

      #${STREAM_PANEL_ID} .dlp-stream-row {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: 7px;
        min-height: 78px;
        padding: 8px;
        border: 1px solid color-mix(in srgb, var(--dlp-text-primary) 16%, transparent);
        border-radius: 6px;
        background: color-mix(in srgb, var(--dlp-bg) 48%, transparent);
        transition: background 140ms ease, border-color 140ms ease;
      }

      #${STREAM_PANEL_ID} .dlp-stream-row:hover {
        border-color: color-mix(in srgb, var(--dlp-accent-interactive) 44%, transparent);
        background: color-mix(in srgb, var(--dlp-bg) 62%, transparent);
      }

      #${STREAM_PANEL_ID} .dlp-stream-row-top {
        min-width: 0;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
      }

      #${STREAM_PANEL_ID} .dlp-stream-meta,
      #${STREAM_PANEL_ID} .dlp-stream-actions {
        display: flex;
        align-items: center;
      }

      #${STREAM_PANEL_ID} .dlp-stream-meta {
        min-width: 0;
        gap: 8px;
      }

      #${STREAM_PANEL_ID} .dlp-stream-actions {
        flex: 0 0 auto;
        gap: 6px;
      }

      #${STREAM_PANEL_ID} .dlp-stream-type {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-width: 0;
        flex: 0 0 auto;
        width: 52px;
        height: 30px;
        border-radius: 5px;
        background: color-mix(in srgb, var(--dlp-accent-interactive) 18%, transparent);
        color: var(--dlp-accent-interactive);
        font: 800 11px/1 Arial, sans-serif;
        text-transform: uppercase;
      }

      #${STREAM_PANEL_ID} .dlp-stream-host,
      #${STREAM_PANEL_ID} .dlp-stream-empty {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      #${STREAM_PANEL_ID} .dlp-stream-host {
        color: var(--dlp-text-primary);
        font: 700 12px/1.25 Consolas, "Cascadia Mono", monospace;
      }

      #${STREAM_PANEL_ID} .dlp-stream-url-scroll {
        min-width: 0;
        width: 100%;
        height: 28px;
        padding: 6px 8px;
        border: 1px solid color-mix(in srgb, var(--dlp-text-primary) 12%, transparent);
        border-radius: 5px;
        background: color-mix(in srgb, #000 18%, transparent);
        color: color-mix(in srgb, var(--dlp-text-secondary) 86%, transparent);
        font: 11px/14px Consolas, "Cascadia Mono", monospace;
        overflow-x: auto;
        overflow-y: hidden;
        overscroll-behavior-x: contain;
        scrollbar-width: thin;
        scrollbar-color: color-mix(in srgb, var(--dlp-text-primary) 24%, transparent) transparent;
        white-space: nowrap;
        word-break: normal;
        overflow-wrap: normal;
        user-select: text;
      }

      #${STREAM_PANEL_ID} .dlp-stream-url-scroll::-webkit-scrollbar {
        height: 6px;
      }

      #${STREAM_PANEL_ID} .dlp-stream-url-scroll::-webkit-scrollbar-track {
        background: transparent;
      }

      #${STREAM_PANEL_ID} .dlp-stream-url-scroll::-webkit-scrollbar-thumb {
        border-radius: 999px;
        background: color-mix(in srgb, var(--dlp-text-primary) 24%, transparent);
      }

      #${STREAM_PANEL_ID} .dlp-stream-empty {
        color: var(--dlp-text-secondary);
        font: 12px/1.35 Arial, sans-serif;
      }

      #${STREAM_PANEL_ID} .dlp-stream-empty {
        padding: 8px;
        white-space: normal;
      }

      #${STREAM_PANEL_ID} .dlp-stream-copy,
      #${STREAM_PANEL_ID} .dlp-stream-vlc,
      #${STREAM_PANEL_ID} .dlp-stream-live,
      #${STREAM_PANEL_ID} .dlp-stream-close,
      #${STREAM_PANEL_ID} .dlp-stream-refresh {
        height: 30px;
        border: 1px solid color-mix(in srgb, var(--dlp-border) 88%, transparent);
        border-radius: 6px;
        background: color-mix(in srgb, var(--dlp-surface) 46%, transparent);
        color: var(--dlp-text-primary);
        font: 800 11px Arial, sans-serif;
        line-height: 28px;
        text-align: center;
        cursor: pointer;
        transition: background 140ms ease, border-color 140ms ease, color 140ms ease;
      }

      #${STREAM_PANEL_ID} .dlp-stream-copy {
        width: 52px;
      }

      #${STREAM_PANEL_ID} .dlp-stream-vlc {
        width: 46px;
      }

      #${STREAM_PANEL_ID} .dlp-stream-live {
        width: 46px;
      }

      #${STREAM_PANEL_ID} .dlp-stream-close,
      #${STREAM_PANEL_ID} .dlp-stream-refresh {
        width: 34px;
        padding: 0;
      }

      #${STREAM_PANEL_ID} button:hover {
        border-color: var(--dlp-accent-interactive);
        background: color-mix(in srgb, var(--dlp-accent-interactive) 12%, var(--dlp-surface));
        color: var(--dlp-accent-interactive);
      }

      #${STREAM_PANEL_ID} button:disabled {
        opacity: 0.48;
        cursor: default;
      }

      #${STREAM_PANEL_ID} button:focus-visible {
        outline: 2px solid color-mix(in srgb, var(--dlp-accent-interactive) 72%, transparent);
        outline-offset: 2px;
      }

      @media (prefers-reduced-motion: reduce) {
        #${BUTTON_ID},
        #${BUTTON_ID} .dlp-button-icon svg,
        #${ROTATE_BUTTON_ID},
        #${ROTATE_BUTTON_ID} .dlp-button-icon svg,
        #${STREAM_PANEL_ID},
        #${STREAM_PANEL_ID} button {
          transition: none;
          animation: none;
        }
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
    const label = button?.querySelector(".dlp-button-label");

    if (label) {
      label.textContent = text;
      return;
    }

    button.textContent = text;
  }

  function setButtonStatus(button, status, text) {
    button.dataset.dlpStatus = status;
    button.setAttribute("aria-busy", status === "sending" ? "true" : "false");
    setButtonText(button, text);
  }

  function resetButtonStatus(button) {
    setButtonStatus(button, "idle", settings.streamOverlay ? "Streams" : "Download");
    button.disabled = false;
    syncAutoHideAfterPlacement(button);
  }

  function getToast() {
    let toast = document.getElementById(TOAST_ID);

    if (!toast) {
      toast = document.createElement("div");
      toast.id = TOAST_ID;
      toast.setAttribute("role", "status");
      toast.setAttribute("aria-live", "polite");
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
    const rotateButton = document.getElementById(ROTATE_BUTTON_ID);

    return Boolean(
        settings.autoHideOverlay
        && button
        && !button.disabled
        && !button.matches(":hover")
        && !button.matches(":focus")
        && !rotateButton?.matches(":hover")
        && !rotateButton?.matches(":focus")
        && !document.getElementById(STREAM_PANEL_ID)
    );
  }

  function scheduleAutoHide(button) {
    window.clearTimeout(hideTimer);

    if (!shouldAutoHideButton(button)) {
      button?.classList.remove("dlp-overlay-hidden");
      document.getElementById(ROTATE_BUTTON_ID)?.classList.remove("dlp-overlay-hidden");
      return;
    }

    hideTimer = window.setTimeout(() => {
      if (!shouldAutoHideButton(button)) {
        button?.classList.remove("dlp-overlay-hidden");
        document.getElementById(ROTATE_BUTTON_ID)?.classList.remove("dlp-overlay-hidden");
        return;
      }

      button.classList.add("dlp-overlay-hidden");
      document.getElementById(ROTATE_BUTTON_ID)?.classList.add("dlp-overlay-hidden");
    }, AUTO_HIDE_DELAY_MS);
  }

  function showButtonForInteraction(button) {
    const targetButton = button || document.getElementById(BUTTON_ID);

    if (!targetButton) {
      return;
    }

    targetButton.classList.remove("dlp-overlay-hidden");
    document.getElementById(ROTATE_BUTTON_ID)?.classList.remove("dlp-overlay-hidden");
    scheduleAutoHide(targetButton);
  }

  function syncAutoHideAfterPlacement(button) {
    const panel = document.getElementById(STREAM_PANEL_ID);

    if (panel) {
      placeStreamPanel(panel, button);
    }

    if (!settings.autoHideOverlay) {
      window.clearTimeout(hideTimer);
      button.classList.remove("dlp-overlay-hidden");
      document.getElementById(ROTATE_BUTTON_ID)?.classList.remove("dlp-overlay-hidden");
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

  function handlePointerActivity(event) {
    const point = event.touches?.[0] || event;

    if (Number.isFinite(point.clientX) && Number.isFinite(point.clientY)) {
      lastPointerX = point.clientX;
      lastPointerY = point.clientY;
    }

    handlePageActivity();
  }

  function getButtonTargetVideo(button) {
    const targetVideo = button?.__dlpTargetVideo;

    return targetVideo instanceof HTMLVideoElement && document.contains(targetVideo)
      ? targetVideo
      : null;
  }

  function captureStyleProperty(element, name) {
    return {
      value: element.style.getPropertyValue(name),
      priority: element.style.getPropertyPriority(name)
    };
  }

  function restoreStyleProperty(element, name, property) {
    if (property.value) {
      element.style.setProperty(name, property.value, property.priority);
    } else {
      element.style.removeProperty(name);
    }
  }

  function getVideoRotationSource(video) {
    return video.currentSrc
      || video.src
      || video.querySelector("source[src]")?.src
      || location.href;
  }

  function restoreVideoRotationStyles(video, state) {
    restoreStyleProperty(video, "rotate", state.original.rotate);
    restoreStyleProperty(video, "scale", state.original.scale);
    restoreStyleProperty(video, "transform-origin", state.original.transformOrigin);
    video.removeAttribute("data-dlp-rotation");
  }

  function getVideoRotationState(video) {
    const source = getVideoRotationSource(video);
    let state = videoRotationStates.get(video);

    if (state && state.source !== source) {
      restoreVideoRotationStyles(video, state);
      state = null;
    }

    if (!state) {
      state = {
        degrees: 0,
        frame: null,
        source,
        original: {
          rotate: captureStyleProperty(video, "rotate"),
          scale: captureStyleProperty(video, "scale"),
          transformOrigin: captureStyleProperty(video, "transform-origin")
        }
      };
      videoRotationStates.set(video, state);
    }

    return state;
  }

  function getVideoRotationScale(video, degrees) {
    if (degrees % 180 === 0) {
      return 1;
    }

    const width = video.clientWidth || video.offsetWidth || video.videoWidth || 1;
    const height = video.clientHeight || video.offsetHeight || video.videoHeight || 1;
    return Math.min(1, width / height, height / width);
  }

  function syncRotateButton(button, video) {
    if (!button) {
      return;
    }

    button.__dlpTargetVideo = video instanceof HTMLVideoElement ? video : null;

    if (!(video instanceof HTMLVideoElement)) {
      button.hidden = true;
      button.dataset.dlpRotation = "0";
      return;
    }

    const degrees = getVideoRotationState(video).degrees;
    button.hidden = false;
    button.dataset.dlpRotation = String(degrees);
    button.title = degrees
      ? `Rotation: ${degrees}° · Click: 90° right · Shift+click: 90° left · Double-click: reset`
      : "Rotate 90° right · Shift+click: 90° left · Double-click: reset · [ ] \\";
    button.setAttribute("aria-label", degrees
      ? `Video rotation ${degrees} degrees. Click to rotate right, Shift click to rotate left, double click to reset`
      : "Rotate video right. Shift click rotates left. Double click resets");
  }

  function setVideoRotation(video, degrees) {
    if (!(video instanceof HTMLVideoElement)) {
      return;
    }

    const state = getVideoRotationState(video);
    const normalized = ((degrees % 360) + 360) % 360;
    state.degrees = normalized;

    if (normalized === 0) {
      restoreVideoRotationStyles(video, state);
    } else {
      const scale = getVideoRotationScale(video, normalized);
      video.style.setProperty("transform-origin", "center center", "important");
      video.style.setProperty("rotate", `${normalized}deg`, "important");
      video.style.setProperty("scale", String(Number(scale.toFixed(4))), "important");
      video.dataset.dlpRotation = String(normalized);
    }

    syncRotateButton(document.getElementById(ROTATE_BUTTON_ID), video);
  }

  function rotateTargetVideo(button, delta) {
    const video = getButtonTargetVideo(button);

    if (!video) {
      return;
    }

    const state = getVideoRotationState(video);
    setVideoRotation(video, state.degrees + delta);
  }

  function isEditableTarget(target) {
    return target instanceof Element && Boolean(target.closest(
      'input, textarea, select, [contenteditable="true"], [role="textbox"]'
    ));
  }

  function isPointerOverVideo(video) {
    const rect = video.getBoundingClientRect();
    return lastPointerX >= rect.left
      && lastPointerX <= rect.right
      && lastPointerY >= rect.top
      && lastPointerY <= rect.bottom;
  }

  function handleRotationShortcut(event) {
    if (event.defaultPrevented
        || event.repeat
        || event.isComposing
        || event.ctrlKey
        || event.altKey
        || event.metaKey
        || isEditableTarget(event.target)) {
      return;
    }

    const action = {
      BracketLeft: -90,
      BracketRight: 90,
      Backslash: 0
    }[event.code];

    if (action === undefined) {
      return;
    }

    const button = document.getElementById(ROTATE_BUTTON_ID);
    const video = getButtonTargetVideo(button);
    const buttonFocused = document.activeElement === button;

    if (!video || (!buttonFocused && !isPointerOverVideo(video))) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();

    if (action === 0) {
      setVideoRotation(video, 0);
    } else {
      rotateTargetVideo(button, action);
    }
  }

  function sendDownload(button) {
    if (!hasRuntime()) {
      showToast("Reload the DLP extension", "error");
      deactivateExtensionUi();
      return;
    }

    const targetVideo = getButtonTargetVideo(button);
    const requestPageUrl = location.href;
    const requestUrl = getDownloadUrl(targetVideo);
    const requestTitle = getMediaTitle();
    const instagramRequest = getPlatform() === "instagram";
    const mediaVideo = targetVideo || (instagramRequest ? getInstagramActiveVideo() : null);
    const requestMediaPageUrl = instagramRequest
      ? getInstagramMediaUrl(targetVideo)
      : null;

    setButtonStatus(button, "sending", "Sending");
    button.disabled = true;
    button.classList.remove("dlp-overlay-hidden");
    showToast("Sending to DLP", "success");

    waitForExperimentalCandidates((candidates) => {
      try {
        chrome.runtime.sendMessage(
          {
            type: "dlp-download-current-video",
            url: requestUrl,
            title: requestTitle,
            pageUrl: requestPageUrl,
            mediaPageUrl: requestMediaPageUrl || requestPageUrl,
            preferPageUrl: instagramRequest && Boolean(requestMediaPageUrl),
            mediaDuration: mediaVideo && Number.isFinite(mediaVideo.duration)
              ? mediaVideo.duration
              : null,
            candidateStartedAt: instagramRequest ? instagramCandidatesStartedAt : 0,
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
              setButtonStatus(button, "error", "Retry");
              showToast("DLP app connection failed", "error");
              console.log("DLP extension error:", chrome.runtime.lastError.message);
            } else if (!response || response.ok === false) {
              setButtonStatus(button, "error", "Retry");
              showToast(response?.message || "DLP request failed", "error");
              console.log("DLP native host response:", response);
            } else {
              setButtonStatus(button, "success", "Sent");
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
    }, false, Boolean(settings.deepScanner), targetVideo);
  }

  function isStreamCandidate(candidate) {
    return Boolean(candidate?.url && [
      "hls",
      "dash",
      "direct-mp4",
      "direct-webm",
      "direct-video",
      "direct-audio"
    ].includes(candidate.type));
  }

  function getStreamLabel(candidate) {
    if (candidate.type === "hls") {
      return "M3U8";
    }

    if (candidate.type === "dash") {
      return "MPD";
    }

    if (candidate.type === "direct-audio") {
      return "Audio";
    }

    return (getMediaExtension(candidate.url) || "File").toUpperCase();
  }

  function getStreamDisplayParts(candidate) {
    try {
      const parsed = new URL(candidate.url);
      const pathParts = parsed.pathname.split("/").filter(Boolean);
      const compactPath = pathParts.length
        ? `/${pathParts.slice(Math.max(0, pathParts.length - 3)).join("/")}`
        : parsed.pathname || "/";

      return {
        host: parsed.hostname.replace(/^www\./i, ""),
        path: `${compactPath}${parsed.search ? " ?" : ""}`
      };
    } catch {
      return {
        host: getStreamLabel(candidate),
        path: candidate.url
      };
    }
  }

  function getStreamRowScore(candidate) {
    let score = 0;

    if (candidate.type === "hls") {
      score += 40;
    } else if (candidate.type === "dash") {
      score += 32;
    } else if (candidate.type?.startsWith("direct")) {
      score += 20;
    }

    if (candidate.source === "clappr.source" || candidate.source === "hls.loadSource") {
      score += 30;
    } else if (candidate.source === "network.redirect") {
      score += 20;
    } else if (candidate.source === "network") {
      score += 10;
    }

    return score;
  }

  function getVisibleStreamCandidates(candidates) {
    return candidates
      .slice()
      .sort((first, second) => getStreamRowScore(second) - getStreamRowScore(first))
      .slice(0, 10);
  }

  function copyText(text, callback) {
    const fallback = () => {
      const input = document.createElement("textarea");
      input.value = text;
      input.style.position = "fixed";
      input.style.left = "-9999px";
      document.body.appendChild(input);
      input.select();

      let copied = false;

      try {
        copied = document.execCommand("copy");
      } catch {
        copied = false;
      }

      input.remove();
      callback(copied);
    };

    if (navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(text).then(
        () => callback(true),
        fallback
      );
      return;
    }

    fallback();
  }

  function buildVlcPlaylist(candidate) {
    const referrer = candidate.pageUrl || location.href;
    const userAgent = candidate.userAgent || navigator.userAgent || "";

    return [
      "#EXTM3U",
      `#EXTINF:-1,${getMediaTitle() || getStreamLabel(candidate)}`,
      referrer ? `#EXTVLCOPT:http-referrer=${referrer}` : "",
      userAgent ? `#EXTVLCOPT:http-user-agent=${userAgent}` : "",
      "#EXTVLCOPT:http-reconnect=true",
      candidate.url
    ].filter(Boolean).join("\n");
  }

  function getStreamPlaylistFileName(candidate) {
    const label = getMediaTitle() || getStreamLabel(candidate) || "stream";
    const safeLabel = label
      .replace(/[<>:"/\\|?*\x00-\x1F]/g, " ")
      .replace(/\s+/g, " ")
      .trim()
      .slice(0, 80);

    return `${safeLabel || "dlp-stream"}.m3u8`;
  }

  function downloadTextFile(text, fileName) {
    const blob = new Blob([text], { type: "application/vnd.apple.mpegurl;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");

    link.href = url;
    link.download = fileName;
    link.style.display = "none";
    document.body.appendChild(link);
    link.click();

    window.setTimeout(() => {
      URL.revokeObjectURL(url);
      link.remove();
    }, 1000);
  }

  function buildStreamPlaylist(candidate, callback) {
    if (!hasRuntime()) {
      callback({
        ok: true,
        playlist: buildVlcPlaylist(candidate)
      });
      return;
    }

    try {
      chrome.runtime.sendMessage(
        {
          type: "dlp-build-stream-playlist",
          candidate
        },
        (response) => {
          if (chrome.runtime.lastError || !response?.ok || !response.playlist) {
            callback({
              ok: false,
              message: response?.message || chrome.runtime.lastError?.message || "Could not build playlist"
            });
            return;
          }

          callback(response);
        }
      );
    } catch (error) {
      callback({
        ok: false,
        message: error?.message || "Could not build playlist"
      });
    }
  }

  function openLiveStream(candidate, callback) {
    if (!hasRuntime()) {
      callback({
        ok: false,
        message: "Reload the DLP extension"
      });
      return;
    }

    try {
      chrome.runtime.sendMessage(
        {
          type: "dlp-open-stream",
          candidate,
          title: getMediaTitle()
        },
        (response) => {
          if (chrome.runtime.lastError || !response?.ok) {
            callback({
              ok: false,
              message: response?.message || chrome.runtime.lastError?.message || "Could not open live stream"
            });
            return;
          }

          callback(response);
        }
      );
    } catch (error) {
      callback({
        ok: false,
        message: error?.message || "Could not open live stream"
      });
    }
  }

  function markCopyButton(button, copied, fallbackText) {
    button.textContent = copied ? "OK" : "Fail";

    window.setTimeout(() => {
      button.textContent = fallbackText;
    }, 1200);
  }

  function getStreamCandidates(callback) {
    const localCandidates = getExperimentalCandidates(true).filter(isStreamCandidate);

    if (!hasRuntime()) {
      callback(localCandidates);
      return;
    }

    try {
      chrome.runtime.sendMessage(
        {
          type: "dlp-get-stream-candidates",
          candidates: localCandidates
        },
        (response) => {
          if (chrome.runtime.lastError || !response?.ok) {
            callback(localCandidates);
            return;
          }

          callback(Array.isArray(response.candidates)
            ? response.candidates.filter(isStreamCandidate)
            : localCandidates);
        }
      );
    } catch {
      callback(localCandidates);
    }
  }

  function clearStreamPanelAutoHide() {
    window.clearTimeout(streamPanelHideTimer);
    streamPanelHideTimer = null;
  }

  function hideStreamPanel(panel, button) {
    if (!panel || !document.documentElement.contains(panel)) {
      return;
    }

    panel.classList.add("dlp-stream-panel-hidden");

    window.setTimeout(() => {
      if (panel.classList.contains("dlp-stream-panel-hidden")) {
        panel.remove();
        button?.setAttribute("aria-expanded", "false");
        scheduleAutoHide(button);
      }
    }, 170);
  }

  function scheduleStreamPanelAutoHide(panel, button) {
    clearStreamPanelAutoHide();

    if (!settings.autoHideOverlay || !panel || !document.documentElement.contains(panel)) {
      return;
    }

    streamPanelHideTimer = window.setTimeout(() => {
      if (!document.documentElement.contains(panel)) {
        return;
      }

      if (panel.matches(":hover") || panel.contains(document.activeElement)) {
        scheduleStreamPanelAutoHide(panel, button);
        return;
      }

      hideStreamPanel(panel, button);
    }, STREAM_PANEL_HIDE_DELAY_MS);
  }

  function removeStreamPanel() {
    clearStreamPanelAutoHide();
    document.getElementById(STREAM_PANEL_ID)?.remove();
  }

  function placeStreamPanel(panel, button) {
    const rect = button.getBoundingClientRect();
    const viewport = getViewportSize();
    const width = Math.min(560, Math.max(1, viewport.width - (VIEWPORT_MARGIN * 2)));
    const maxHeight = Math.min(340, Math.max(1, viewport.height - (VIEWPORT_MARGIN * 2)));

    panel.style.width = `${width}px`;
    panel.style.maxHeight = `${maxHeight}px`;

    const panelRect = panel.getBoundingClientRect();
    const panelHeight = Math.min(panelRect.height || maxHeight, maxHeight);
    const minLeft = viewport.left + VIEWPORT_MARGIN;
    const maxLeft = Math.max(minLeft, viewport.right - width - VIEWPORT_MARGIN);
    const left = clamp(rect.left, minLeft, maxLeft);
    const belowTop = rect.bottom + BUTTON_GAP;
    const aboveTop = rect.top - panelHeight - BUTTON_GAP;
    const top = belowTop + panelHeight <= viewport.bottom - VIEWPORT_MARGIN
      ? belowTop
      : clamp(
        aboveTop,
        viewport.top + VIEWPORT_MARGIN,
        Math.max(viewport.top + VIEWPORT_MARGIN, viewport.bottom - panelHeight - VIEWPORT_MARGIN)
      );

    panel.style.left = `${left}px`;
    panel.style.top = `${top}px`;
  }

  function renderStreamRows(panel, candidates) {
    const list = panel.querySelector(".dlp-stream-list");
    const count = panel.querySelector(".dlp-stream-count");
    const visibleCandidates = getVisibleStreamCandidates(candidates);

    list.replaceChildren();

    if (count) {
      count.textContent = candidates.length > visibleCandidates.length
        ? `${visibleCandidates.length} of ${candidates.length}`
        : `${candidates.length}`;
    }

    if (!candidates.length) {
      const empty = document.createElement("div");
      empty.className = "dlp-stream-empty";
      empty.textContent = "No streams found. Play the video, then refresh.";
      list.appendChild(empty);
      return;
    }

    for (const candidate of visibleCandidates) {
      const display = getStreamDisplayParts(candidate);
      const row = document.createElement("div");
      row.className = "dlp-stream-row";

      const top = document.createElement("div");
      top.className = "dlp-stream-row-top";

      const meta = document.createElement("div");
      meta.className = "dlp-stream-meta";

      const type = document.createElement("span");
      type.className = "dlp-stream-type";
      type.textContent = getStreamLabel(candidate);

      const host = document.createElement("span");
      host.className = "dlp-stream-host";
      host.textContent = display.host;

      meta.append(type, host);

      const copy = document.createElement("button");
      copy.className = "dlp-stream-copy";
      copy.type = "button";
      copy.textContent = "Copy";
      copy.addEventListener("click", (event) => {
        event.preventDefault();
        event.stopPropagation();
        copyText(candidate.url, (copied) => {
          markCopyButton(copy, copied, "Copy");
        });
      });

      const vlc = document.createElement("button");
      vlc.className = "dlp-stream-vlc";
      vlc.type = "button";
      vlc.title = "Download VLC playlist";
      vlc.textContent = "M3U";
      vlc.addEventListener("click", (event) => {
        event.preventDefault();
        event.stopPropagation();

        vlc.disabled = true;
        vlc.textContent = "...";

        buildStreamPlaylist(candidate, (response) => {
          vlc.disabled = false;

          if (!response.ok) {
            showToast(response.message || "Playlist failed", "error");
            markCopyButton(vlc, false, "M3U");
            return;
          }

          downloadTextFile(response.playlist, getStreamPlaylistFileName(candidate));
          showToast(response.transformed ? "VLC playlist fixed" : "VLC playlist ready", "success");
          markCopyButton(vlc, true, "M3U");
        });
      });

      const live = document.createElement("button");
      live.className = "dlp-stream-live";
      live.type = "button";
      live.title = "Open live stream in VLC";
      live.textContent = "Live";
      live.disabled = candidate.type !== "hls";
      live.addEventListener("click", (event) => {
        event.preventDefault();
        event.stopPropagation();

        live.disabled = true;
        live.textContent = "...";

        openLiveStream(candidate, (response) => {
          live.disabled = candidate.type !== "hls";

          if (!response.ok) {
            showToast(response.message || "Live stream failed", "error");
            markCopyButton(live, false, "Live");
            return;
          }

          showToast("Opening VLC live stream", "success");
          markCopyButton(live, true, "Live");
        });
      });

      const actions = document.createElement("div");
      actions.className = "dlp-stream-actions";
      actions.append(copy, vlc, live);

      const url = document.createElement("div");
      url.className = "dlp-stream-url-scroll";
      url.title = candidate.url;
      url.textContent = candidate.url;

      top.append(meta, actions);
      row.append(top, url);
      list.appendChild(row);
    }
  }

  function refreshStreamPanel(panel, button) {
    const list = panel.querySelector(".dlp-stream-list");
    list.replaceChildren();

    const loading = document.createElement("div");
    loading.className = "dlp-stream-empty";
    loading.textContent = "Scanning streams";
    list.appendChild(loading);

    getStreamCandidates((candidates) => {
      if (!document.documentElement.contains(panel)) {
        return;
      }

      renderStreamRows(panel, candidates);
      placeStreamPanel(panel, button);
      scheduleStreamPanelAutoHide(panel, button);
    });
  }

  function toggleStreamPanel(button) {
    const existing = document.getElementById(STREAM_PANEL_ID);

    if (existing) {
      existing.remove();
      button.setAttribute("aria-expanded", "false");
      return;
    }

    const panel = document.createElement("div");
    panel.id = STREAM_PANEL_ID;
    panel.setAttribute("role", "dialog");
    panel.setAttribute("aria-label", "Detected media streams");
    button.setAttribute("aria-expanded", "true");
    panel.addEventListener("mousedown", (event) => event.stopPropagation());
    panel.addEventListener("click", (event) => event.stopPropagation());
    panel.addEventListener("mouseenter", clearStreamPanelAutoHide);
    panel.addEventListener("focusin", clearStreamPanelAutoHide);
    panel.addEventListener("mouseleave", () => {
      scheduleStreamPanelAutoHide(panel, button);
    });
    panel.addEventListener("focusout", () => {
      window.setTimeout(() => scheduleStreamPanelAutoHide(panel, button), 0);
    });

    const head = document.createElement("div");
    head.className = "dlp-stream-head";

    const titleWrap = document.createElement("div");
    titleWrap.className = "dlp-stream-title-wrap";

    const title = document.createElement("span");
    title.className = "dlp-stream-title";
    title.textContent = "Streams";

    const count = document.createElement("span");
    count.className = "dlp-stream-count";
    count.textContent = "0";

    titleWrap.append(title, count);

    const refresh = document.createElement("button");
    refresh.className = "dlp-stream-refresh";
    refresh.type = "button";
    refresh.title = "Refresh streams";
    refresh.textContent = "R";

    const close = document.createElement("button");
    close.className = "dlp-stream-close";
    close.type = "button";
    close.title = "Close";
    close.textContent = "X";

    const list = document.createElement("div");
    list.className = "dlp-stream-list";

    refresh.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      refreshStreamPanel(panel, button);
    });

    close.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      removeStreamPanel();
      button.setAttribute("aria-expanded", "false");
      scheduleAutoHide(button);
    });

    head.append(titleWrap, refresh, close);
    panel.append(head, list);
    (button.parentElement || document.body || document.documentElement).appendChild(panel);
    placeStreamPanel(panel, button);
    scheduleStreamPanelAutoHide(panel, button);
    refreshStreamPanel(panel, button);
  }

  function setButtonMarkup(button) {
    button.innerHTML = `
      <span class="dlp-button-icon" aria-hidden="true">
        <svg viewBox="0 0 24 24" focusable="false">
          <path d="M12 3v12"></path>
          <path d="m7.5 10.5 4.5 4.5 4.5-4.5"></path>
          <path d="M5 20h14"></path>
        </svg>
      </span>
      <span class="dlp-button-label"></span>
    `;
  }

  function setRotateButtonMarkup(button) {
    button.innerHTML = `
      <span class="dlp-button-icon" aria-hidden="true">
        <svg viewBox="0 0 24 24" focusable="false">
          <path d="M20 11a8 8 0 1 1-2.34-5.66"></path>
          <path d="M20 4v7h-7"></path>
        </svg>
      </span>
    `;
  }

  function createButton() {
    const button = document.createElement("button");
    button.id = BUTTON_ID;
    button.type = "button";
    setButtonMarkup(button);
    button.title = settings.streamOverlay ? "Show stream links" : "Download this video with DLP";
    button.setAttribute("aria-label", settings.streamOverlay ? "Show stream links" : "Download this video with DLP");
    if (settings.streamOverlay) {
      button.setAttribute("aria-haspopup", "dialog");
      button.setAttribute("aria-expanded", "false");
    }
    setButtonStatus(button, "idle", settings.streamOverlay ? "Streams" : "Download");

    button.addEventListener("mouseenter", () => {
      showButtonForInteraction(button);
    });

    button.addEventListener("mouseleave", () => {
      scheduleAutoHide(button);
    });

    button.addEventListener("focus", () => {
      showButtonForInteraction(button);
    });

    button.addEventListener("blur", () => {
      scheduleAutoHide(button);
    });

    button.addEventListener("mousedown", (event) => {
      event.preventDefault();
      event.stopPropagation();
    });

    button.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();

      if (settings.streamOverlay) {
        toggleStreamPanel(button);
        return;
      }

      sendDownload(button);
    });

    return button;
  }

  function createRotateButton() {
    const button = document.createElement("button");
    button.id = ROTATE_BUTTON_ID;
    button.type = "button";
    button.hidden = true;
    setRotateButtonMarkup(button);

    button.addEventListener("mouseenter", () => {
      showButtonForInteraction();
    });

    button.addEventListener("focus", () => {
      showButtonForInteraction();
    });

    button.addEventListener("mousedown", (event) => {
      event.stopPropagation();
    });

    button.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      rotateTargetVideo(button, event.shiftKey ? -90 : 90);
    });

    button.addEventListener("dblclick", (event) => {
      event.preventDefault();
      event.stopPropagation();

      const video = getButtonTargetVideo(button);

      if (video) {
        setVideoRotation(video, 0);
      }
    });

    return button;
  }

  function observeTargetSize(target) {
    if (observedTarget === target || !window.ResizeObserver) {
      return;
    }

    targetResizeObserver?.disconnect();
    targetResizeObserver = new ResizeObserver(() => {
      const rotateButton = document.getElementById(ROTATE_BUTTON_ID);
      const video = getButtonTargetVideo(rotateButton);
      const state = video && videoRotationStates.get(video);

      if (video && state?.degrees) {
        setVideoRotation(video, state.degrees);
      }

      scheduleRefresh();
    });
    observedTarget = target;
    targetResizeObserver.observe(target);
  }

  function clearTargetSizeObservation() {
    targetResizeObserver?.disconnect();
    targetResizeObserver = null;
    observedTarget = null;
  }

  function removeButton() {
    if (placementFrame !== null) {
      window.cancelAnimationFrame(placementFrame);
      placementFrame = null;
    }

    const existing = document.getElementById(BUTTON_ID);
    const rotateButton = document.getElementById(ROTATE_BUTTON_ID);

    if (existing) {
      existing.remove();
    }

    rotateButton?.remove();

    removeStreamPanel();
    clearTargetSizeObservation();
  }

  function getOverlayPosition() {
    const value = settings.overlayPosition || DEFAULT_SETTINGS.overlayPosition;
    return OVERLAY_POSITIONS.has(value) ? value : DEFAULT_SETTINGS.overlayPosition;
  }

  function isRectVisible(rect) {
    const viewport = getViewportSize();

    return rect.width > 0
      && rect.height > 0
      && rect.bottom > viewport.top
      && rect.right > viewport.left
      && rect.top < viewport.bottom
      && rect.left < viewport.right;
  }

  function isElementRenderable(element, rect = element.getBoundingClientRect()) {
    if (!isRectVisible(rect)) {
      return false;
    }

    const style = window.getComputedStyle(element);
    return style.display !== "none"
      && style.visibility !== "hidden"
      && style.opacity !== "0"
      && !element.closest("[hidden], [aria-hidden=\"true\"]");
  }

  function getVisibleArea(rect) {
    const viewport = getViewportSize();
    const visibleWidth = Math.max(0, Math.min(rect.right, viewport.right) - Math.max(rect.left, viewport.left));
    const visibleHeight = Math.max(0, Math.min(rect.bottom, viewport.bottom) - Math.max(rect.top, viewport.top));

    return visibleWidth * visibleHeight;
  }

  function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
  }

  function getViewportSize() {
    const visualViewport = window.visualViewport;
    const width = Math.max(
      1,
      visualViewport?.width || window.innerWidth || document.documentElement.clientWidth || 1
    );
    const height = Math.max(
      1,
      visualViewport?.height || window.innerHeight || document.documentElement.clientHeight || 1
    );
    const left = Math.max(0, visualViewport?.offsetLeft || 0);
    const top = Math.max(0, visualViewport?.offsetTop || 0);

    return {
      left,
      top,
      right: left + width,
      bottom: top + height,
      width,
      height
    };
  }

  function getActionSize(button, rotateButton, viewport) {
    const availableWidth = Math.max(1, viewport.width - (VIEWPORT_MARGIN * 2));
    const availableHeight = Math.max(1, viewport.height - (VIEWPORT_MARGIN * 2));
    const buttonWidth = button.offsetWidth || BUTTON_WIDTH;
    const rotateVisible = Boolean(rotateButton && !rotateButton.hidden);
    const rotateWidth = rotateVisible ? rotateButton.offsetWidth || BUTTON_WIDTH : 0;
    const width = Math.max(1, Math.min(
      buttonWidth + (rotateVisible ? ACTION_GAP + rotateWidth : 0),
      availableWidth
    ));
    const height = Math.max(1, Math.min(
      Math.max(button.offsetHeight || BUTTON_HEIGHT, rotateButton?.offsetHeight || 0),
      availableHeight
    ));

    return { width, height, buttonWidth, rotateVisible };
  }

  function getVisibleRect(rect, viewport) {
    const left = Math.max(rect.left, viewport.left);
    const top = Math.max(rect.top, viewport.top);
    const right = Math.min(rect.right, viewport.right);
    const bottom = Math.min(rect.bottom, viewport.bottom);

    return {
      left,
      top,
      right,
      bottom,
      width: Math.max(0, right - left),
      height: Math.max(0, bottom - top)
    };
  }

  function getStableVideoFrame(video) {
    const state = getVideoRotationState(video);

    if (state.frame && document.documentElement.contains(state.frame)) {
      return state.frame;
    }

    const videoRect = video.getBoundingClientRect();
    let frame = video;
    let parent = video.parentElement;

    for (let depth = 0; parent && depth < 4; depth += 1, parent = parent.parentElement) {
      const rect = parent.getBoundingClientRect();
      const closelyWrapsVideo = rect.width >= videoRect.width - 2
        && rect.height >= videoRect.height - 2
        && rect.width <= videoRect.width + 32
        && rect.height <= videoRect.height + 32;

      if (!closelyWrapsVideo) {
        break;
      }

      frame = parent;
    }

    state.frame = frame;
    return frame;
  }

  function getRotationVideo(player) {
    if (player instanceof HTMLVideoElement) {
      return player;
    }

    if (typeof player?.querySelectorAll === "function") {
      const video = getBestVisibleElement(Array.from(player.querySelectorAll("video")));

      if (video instanceof HTMLVideoElement && isElementRenderable(video)) {
        return video;
      }
    }

    return getVisibleVideo();
  }

  function getPlacementTarget(player, platform, rotationVideo) {
    const shouldUseVideoFrame = platform === "tiktok"
      || platform === "instagram"
      || platform === "x"
      || (platform === "youtube" && isYouTubeShortsPage())
      || !platform;

    if (!shouldUseVideoFrame || !(rotationVideo instanceof HTMLVideoElement)) {
      return player;
    }

    return getStableVideoFrame(rotationVideo);
  }

  function getOverlayRoot(target) {
    const fullscreenElement = document.fullscreenElement;

    if (fullscreenElement
        && (fullscreenElement === target
          || fullscreenElement.contains(target)
          || target.contains(fullscreenElement))) {
      return fullscreenElement;
    }

    return document.body || document.documentElement;
  }

  function getAutoPlacementKey(platform, target) {
    if (platform === "youtube") {
      return isYouTubeShortsPage() ? "youtube-shorts" : "youtube-watch";
    }

    if (platform === "instagram") {
      if (target.closest('[role="dialog"]')) {
        return "instagram-modal";
      }

      return /^\/(?:reels?|stories)\//i.test(location.pathname)
        ? "instagram-reels"
        : "instagram-feed";
    }

    return Object.prototype.hasOwnProperty.call(AUTO_PLACEMENTS, platform)
      ? platform
      : "generic";
  }

  function getPlacementOrder(platform, target, position) {
    if (position !== "auto") {
      return MANUAL_PLACEMENTS[position] || AUTO_PLACEMENTS.generic;
    }

    const key = getAutoPlacementKey(platform, target);
    return AUTO_PLACEMENTS[key] || AUTO_PLACEMENTS.generic;
  }

  function createPlacementCandidate(name, rect, size, viewport) {
    const inside = name.startsWith("inside-");
    const outside = name.startsWith("outside-");
    const align = name.endsWith("-left")
      ? "left"
      : name.endsWith("-center")
        ? "center"
        : "right";
    const minLeft = viewport.left + VIEWPORT_MARGIN;
    const maxLeft = viewport.right - size.width - VIEWPORT_MARGIN;
    const minTop = viewport.top + VIEWPORT_MARGIN;
    const maxTop = viewport.bottom - size.height - VIEWPORT_MARGIN;
    let left = rect.right - size.width;
    let top = rect.top - size.height - BUTTON_GAP;

    if (align === "left") {
      left = rect.left;
    } else if (align === "center") {
      left = rect.left + ((rect.width - size.width) / 2);
    }

    if (outside) {
      left = name.startsWith("outside-left")
        ? rect.left - size.width - BUTTON_GAP
        : rect.right + BUTTON_GAP;
      top = rect.top + BUTTON_INSET;
    } else if (name.startsWith("below-")) {
      top = rect.bottom + BUTTON_GAP;
    } else if (name.startsWith("inside-top-")) {
      top = rect.top + BUTTON_INSET;
      left += align === "right" ? -BUTTON_INSET : align === "left" ? BUTTON_INSET : 0;
    } else if (name.startsWith("inside-bottom-")) {
      top = rect.bottom - size.height - BUTTON_INSET;
      left += align === "right" ? -BUTTON_INSET : align === "left" ? BUTTON_INSET : 0;
    }

    const fitsViewport = left >= minLeft && left <= maxLeft && top >= minTop && top <= maxTop;
    const fitsMedia = !inside || (
      rect.width >= size.width + (BUTTON_INSET * 2)
        && rect.height >= size.height + (BUTTON_INSET * 2)
    );

    return fitsViewport && fitsMedia ? { name, left, top, inside } : null;
  }

  function isPlacementBlocked(candidate, size, target, controls) {
    const points = [
      [candidate.left + (size.width / 2), candidate.top + (size.height / 2)],
      [candidate.left + 3, candidate.top + 3],
      [candidate.left + size.width - 3, candidate.top + size.height - 3]
    ];

    return points.some(([x, y]) => {
      const element = document.elementFromPoint(x, y);

      if (!element || controls.some((control) =>
        control && (element === control || control.contains(element)))) {
        return false;
      }

      if (candidate.inside && (element === target || target.contains(element))) {
        const control = element.closest(
          'button, a[href], input, select, textarea, [role="button"], [role="link"]'
        );
        return Boolean(control && control !== target && !control.contains(target));
      }

      const control = element.closest(
        'button, a[href], input, select, textarea, [role="button"], [role="link"]'
      );
      const style = window.getComputedStyle(element);
      return Boolean(control || element instanceof HTMLIFrameElement || ["fixed", "sticky"].includes(style.position));
    });
  }

  function placeButtonRelativeToMedia(button, rotateButton, target, position, platform) {
    const targetRect = target.getBoundingClientRect();
    const viewport = getViewportSize();
    const rect = getVisibleRect(targetRect, viewport);

    if (!isRectVisible(targetRect)
        || rect.width < MIN_VISIBLE_MEDIA_EDGE
        || rect.height < MIN_VISIBLE_MEDIA_EDGE) {
      button.hidden = true;
      if (rotateButton) {
        rotateButton.hidden = true;
      }
      return;
    }

    button.hidden = false;
    button.style.top = `${viewport.top}px`;
    button.style.left = `${viewport.left}px`;
    const size = getActionSize(button, rotateButton, viewport);
    const order = document.fullscreenElement
      ? ["inside-top-right", "inside-top-left"]
      : getPlacementOrder(platform, target, position);
    const previousPointerEvents = button.style.getPropertyValue("pointer-events");
    const previousPointerPriority = button.style.getPropertyPriority("pointer-events");
    const previousRotatePointerEvents = rotateButton?.style.getPropertyValue("pointer-events") || "";
    const previousRotatePointerPriority = rotateButton?.style.getPropertyPriority("pointer-events") || "";

    button.style.setProperty("pointer-events", "none", "important");
    rotateButton?.style.setProperty("pointer-events", "none", "important");
    const selected = order
      .map((name) => createPlacementCandidate(name, rect, size, viewport))
      .find((candidate) => candidate
        && !isPlacementBlocked(candidate, size, target, [button, rotateButton]));

    if (previousPointerEvents) {
      button.style.setProperty("pointer-events", previousPointerEvents, previousPointerPriority);
    } else {
      button.style.removeProperty("pointer-events");
    }

    if (rotateButton) {
      if (previousRotatePointerEvents) {
        rotateButton.style.setProperty(
          "pointer-events",
          previousRotatePointerEvents,
          previousRotatePointerPriority
        );
      } else {
        rotateButton.style.removeProperty("pointer-events");
      }
    }

    if (!selected) {
      button.hidden = true;
      if (rotateButton) {
        rotateButton.hidden = true;
      }
      return;
    }

    button.dataset.dlpPlacement = selected.name;
    button.style.top = `${selected.top}px`;
    button.style.left = `${selected.left}px`;

    if (rotateButton && size.rotateVisible) {
      rotateButton.dataset.dlpPlacement = selected.name;
      rotateButton.style.top = `${selected.top}px`;
      rotateButton.style.left = `${selected.left + size.buttonWidth + ACTION_GAP}px`;
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

    const rotationVideo = getRotationVideo(player);
    const placementTarget = getPlacementTarget(player, platform, rotationVideo);

    if (!placementTarget) {
      removeButton();
      return;
    }

    observeTargetSize(placementTarget);

    let button = document.getElementById(BUTTON_ID);
    let rotateButton = document.getElementById(ROTATE_BUTTON_ID);

    if (!button) {
      button = createButton();
    } else if (!button.querySelector(".dlp-button-icon")) {
      button.remove();
      button = createButton();
    }

    if (!rotateButton) {
      rotateButton = createRotateButton();
    } else if (!rotateButton.querySelector(".dlp-button-icon")) {
      rotateButton.remove();
      rotateButton = createRotateButton();
    }

    button.__dlpPlacementTarget = placementTarget;
    button.__dlpTargetVideo = rotationVideo;
    syncRotateButton(rotateButton, rotationVideo);

    button.title = settings.streamOverlay ? "Show stream links" : "Download this video with DLP";
    button.setAttribute("aria-label", settings.streamOverlay ? "Show stream links" : "Download this video with DLP");
    if (settings.streamOverlay) {
      button.setAttribute("aria-haspopup", "dialog");
      button.setAttribute("aria-expanded", String(Boolean(document.getElementById(STREAM_PANEL_ID))));
    } else {
      button.removeAttribute("aria-haspopup");
      button.removeAttribute("aria-expanded");
    }

    if (button.dataset.dlpStatus === "idle") {
      setButtonText(button, settings.streamOverlay ? "Streams" : "Download");
    }

    const overlayPosition = getOverlayPosition();

    const overlayRoot = getOverlayRoot(placementTarget);

    if (button.parentElement !== overlayRoot) {
      overlayRoot.appendChild(button);
    }

    if (rotateButton.parentElement !== overlayRoot) {
      overlayRoot.appendChild(rotateButton);
    }

    placeButtonRelativeToMedia(button, rotateButton, placementTarget, overlayPosition, platform);
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

  function schedulePlacementUpdate() {
    if (!extensionActive || placementFrame !== null) {
      return;
    }

    placementFrame = window.requestAnimationFrame(() => {
      placementFrame = null;

      const button = document.getElementById(BUTTON_ID);
      const rotateButton = document.getElementById(ROTATE_BUTTON_ID);
      const target = button?.__dlpPlacementTarget;

      if (!button || !target || !document.documentElement.contains(target)) {
        scheduleRefresh();
        return;
      }

      const overlayRoot = getOverlayRoot(target);

      if (button.parentElement !== overlayRoot) {
        overlayRoot.appendChild(button);
      }

      if (rotateButton && rotateButton.parentElement !== overlayRoot) {
        overlayRoot.appendChild(rotateButton);
      }

      placeButtonRelativeToMedia(button, rotateButton, target, getOverlayPosition(), getPlatform());
      scheduleRefresh();
    });
  }

  function watchUrlChanges() {
    const notify = () => {
      if (location.href !== lastUrl) {
        lastUrl = location.href;

        if (getPlatform() === "instagram") {
          resetInstagramMediaCandidates();
          lastInstagramMediaContext = "";
        }

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

    const handleViewportResize = () => {
      schedulePlacementUpdate();
      scheduleRefresh();
    };

    window.addEventListener("popstate", notify);
    window.addEventListener("resize", handleViewportResize);
    window.addEventListener("scroll", schedulePlacementUpdate, true);
    document.addEventListener("fullscreenchange", scheduleRefresh, true);
    document.addEventListener("yt-navigate-finish", scheduleRefresh);

    if (window.visualViewport) {
      window.visualViewport.addEventListener("resize", handleViewportResize);
      window.visualViewport.addEventListener("scroll", schedulePlacementUpdate);
    }
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

      if (getPlatform() === "instagram" || (!getPlatform() && experimental)) {
        waitForExperimentalCandidates((candidates) => {
          sendResponse({
            url: getPlatform() === "instagram" ? getDownloadUrl() : candidates[0]?.url || location.href,
            title: getMediaTitle(),
            mediaPageUrl: getPlatform() === "instagram" ? getInstagramMediaUrl() : "",
            mediaDuration: getPlatform() === "instagram"
              ? getInstagramActiveVideo()?.duration
              : null,
            candidates
          });
        }, true, deepScan);

        return true;
      }

      sendResponse({
        url: getDownloadUrl(),
        title: getMediaTitle(),
        mediaPageUrl: getPlatform() === "instagram" ? getInstagramMediaUrl() : "",
        mediaDuration: getPlatform() === "instagram"
          ? getInstagramActiveVideo()?.duration
          : null,
        candidates: []
      });

      return false;
    });
  }

  installPageStreamHook();
  watchPageStreamMessages();

  observer = new MutationObserver(scheduleRefresh);
  observer.observe(document.documentElement || document, {
    childList: true,
    subtree: true
  });

  document.addEventListener("mousemove", handlePointerActivity, true);
  document.addEventListener("touchstart", handlePointerActivity, true);
  document.addEventListener("keydown", handleRotationShortcut, true);
  document.addEventListener("keydown", handlePageActivity, true);
  window.addEventListener("scroll", handlePageActivity, true);

  watchSettingsChanges();
  watchExtensionMessages();
  watchUrlChanges();
  loadSettings(scheduleRefresh);
})();
