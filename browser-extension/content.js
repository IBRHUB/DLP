(function () {
  const BUTTON_ID = "dlp-video-download-button";
  const STYLE_ID = "dlp-video-download-style";
  const BUTTON_WIDTH = 58;
  const BUTTON_OFFSET = 12;

  let lastUrl = location.href;
  let refreshTimer = null;
  let extensionActive = true;
  let observer = null;
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
    removeButton();

    if (observer) {
      observer.disconnect();
    }
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

    return false;
  }

  function getDownloadUrl() {
    const platform = getPlatform();

    if (platform === "tiktok") {
      return getTikTokVideoUrl() || location.href;
    }

    if (platform === "x") {
      return getXStatusUrl() || location.href;
    }

    return location.href;
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

    return null;
  }

  function ensureStyle() {
    if (document.getElementById(STYLE_ID)) {
      return;
    }

    const style = document.createElement("style");
    style.id = STYLE_ID;
    style.textContent = `
      #${BUTTON_ID} {
        position: absolute;
        top: 12px;
        right: 12px;
        z-index: 2147483647;
        width: 58px;
        height: 24px;
        padding: 0;
        border: 1px solid rgba(255, 255, 255, 0.34);
        border-radius: 6px;
        background: rgba(10, 10, 10, 0.18);
        backdrop-filter: blur(2px) saturate(1.45);
        -webkit-backdrop-filter: blur(2px) saturate(1.45);
        color: #fff;
        font: 700 11px/22px Arial, sans-serif;
        text-align: center;
        cursor: pointer;
        text-shadow: 0 1px 2px rgba(0, 0, 0, 0.75);
        box-shadow: 0 2px 5px rgba(0, 0, 0, 0.22);
        opacity: 0.72;
        transition: background 160ms ease, border-color 160ms ease, opacity 160ms ease, transform 160ms ease;
        user-select: none;
      }

      #${BUTTON_ID}:hover {
        background: rgba(255, 255, 255, 0.16);
        border-color: rgba(255, 255, 255, 0.58);
        opacity: 0.95;
        transform: translateY(-1px);
      }

      #${BUTTON_ID}:disabled {
        cursor: default;
        opacity: 0.62;
        transform: none;
      }
    `;

    document.documentElement.appendChild(style);
  }

  function setButtonText(button, text) {
    button.textContent = text;
  }

  function sendDownload(button) {
    if (!hasRuntime()) {
      deactivateExtensionUi();
      return;
    }

    setButtonText(button, "...");
    button.disabled = true;

    try {
      chrome.runtime.sendMessage(
        {
          type: "dlp-download-current-video",
          url: getDownloadUrl()
        },
        (response) => {
          if (!hasRuntime()) {
            deactivateExtensionUi();
            return;
          }

          if (chrome.runtime.lastError) {
            setButtonText(button, "ERR");
            console.log("DLP extension error:", chrome.runtime.lastError.message);
          } else if (!response || response.ok === false) {
            setButtonText(button, "ERR");
            console.log("DLP native host response:", response);
          } else {
            setButtonText(button, "OK");
          }

          window.setTimeout(() => {
            if (!extensionActive) {
              return;
            }

            setButtonText(button, "DLP");
            button.disabled = false;
          }, 1600);
        }
      );
    } catch (error) {
      console.log("DLP extension context ended:", error && error.message ? error.message : error);
      deactivateExtensionUi();
    }
  }

  function createButton() {
    const button = document.createElement("button");
    button.id = BUTTON_ID;
    button.type = "button";
    button.title = "Download with DLP";
    button.setAttribute("aria-label", "Download with DLP");
    setButtonText(button, "DLP");

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
      return;
    }

    button.style.top = "auto";
    button.style.right = "18px";
    button.style.bottom = "74px";
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

  observer = new MutationObserver(scheduleRefresh);
  observer.observe(document.documentElement, {
    childList: true,
    subtree: true
  });

  watchUrlChanges();
  scheduleRefresh();
})();
