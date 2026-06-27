(function () {
  const BUTTON_ID = "dlp-video-download-button";
  const STYLE_ID = "dlp-video-download-style";
  const BUTTON_WIDTH = 58;
  const BUTTON_OFFSET = 12;

  let lastUrl = location.href;
  let refreshTimer = null;
  let extensionActive = true;
  let observer = null;

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
        return rect.width >= 120 && rect.height >= 120;
      })
      .sort((first, second) => {
        const firstRect = first.getBoundingClientRect();
        const secondRect = second.getBoundingClientRect();
        return (secondRect.width * secondRect.height) - (firstRect.width * firstRect.height);
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

    const container = video.closest('[data-e2e="browse-video"]')
      || video.closest('[data-e2e="feed-video"]')
      || video.closest('[data-e2e="video-container"]')
      || video.closest('[class*="DivItemContainer"]')
      || video.closest('[class*="VideoContainer"]')
      || video.closest('[class*="PlayerContainer"]')
      || video.parentElement;

    return findFirstMatchingLink(container, isTikTokVideoUrl)
      || findFirstMatchingLink(document, isTikTokVideoUrl);
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
