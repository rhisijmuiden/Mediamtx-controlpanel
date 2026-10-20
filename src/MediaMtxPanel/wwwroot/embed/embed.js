// Embedded player (/embed/{slug}, see Endpoints/EmbedEndpoints.cs). Builds its own controls around
// wwwroot/js/player.js, applies the stream's embed settings and, for paywalled streams, shows a ticket
// screen: buy a ticket or sign in (both in a popup that hands the ticket back), or type a ticket code.
(function () {
    const root = document.getElementById("mx-embed");
    if (!root) return;
    const cfg = JSON.parse(root.dataset.config);
    let look = cfg.look;

    const t = (key, ...args) => (cfg.texts[key] ?? key).replace(/\{(\d)\}/g, (_, i) => args[i] ?? "");
    const ICONS = {
        play: "M8 5v14l11-7z",
        pause: "M6 5h4v14H6zM14 5h4v14h-4z",
        volume: "M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 8v8a4.5 4.5 0 0 0 2.5-4zM14 3.2v2.1a7 7 0 0 1 0 13.4v2.1a9 9 0 0 0 0-17.6z",
        muted: "M3 9v6h4l5 5V4L7 9H3zm13.4-.4L15 10l2 2-2 2 1.4 1.4 2-2 2 2 1.4-1.4-2-2 2-2-1.4-1.4-2 2z",
        expand: "M5 5h5v2H7v3H5zm9 0h5v5h-2V7h-3zM5 14h2v3h3v2H5zm12 0h2v5h-5v-2h3z",
        shrink: "M8 5h2v5H5V8h3zm6 0h2v3h3v2h-5zM5 14h5v5H8v-3H5zm9 0h5v2h-3v3h-2z",
        pip: "M3 5h18v14H3zm2 2v10h14V7zm7 5h6v4h-6z",
        ticket: "M3 7a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v3a2 2 0 0 0 0 4v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-3a2 2 0 0 0 0-4zm11 0v2h2V7zm0 4v2h2v-2zm0 4v2h2v-2z",
        lock: "M7 10V7a5 5 0 0 1 10 0v3h1a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1zm2 0h6V7a3 3 0 0 0-6 0z",
        clock: "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm1 5v5.4l3.6 2.1-1 1.7L11 13V7z",
        person: "M12 12a5 5 0 1 0 0-10 5 5 0 0 0 0 10zm0 2c-4.4 0-8 2.2-8 5v1h16v-1c0-2.8-3.6-5-8-5z",
    };
    const icon = (name) => `<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="${ICONS[name]}"/></svg>`;
    const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

    // ---------------------------------------------------------------- tickets

    const storeKey = "mediamtx-ticket:" + cfg.slug;
    const store = {
        get() { try { return localStorage.getItem(storeKey); } catch { return null; } },
        set(v) { try { localStorage.setItem(storeKey, v); } catch { /* storage blocked */ } },
        remove() { try { localStorage.removeItem(storeKey); } catch { /* storage blocked */ } },
    };
    const urlToken = new URL(location.href).searchParams.get("t");

    function useToken(token) {
        store.set(token);
        const url = new URL(location.href);
        url.searchParams.set("t", token);
        location.replace(url.toString());
    }

    if (cfg.access === "ticket") {
        if (cfg.ticketInvalid) {
            store.remove();
        } else if (!urlToken && store.get()) {
            useToken(store.get());
            return;
        }
    } else if (cfg.access === "ok" && urlToken) {
        store.set(urlToken);
    }

    // The ticket popup (watch page or My tickets) and the designer talk to us from the panel's own origin.
    window.addEventListener("message", (e) => {
        if (e.origin !== location.origin || !e.data) return;
        if (e.data.type === "mediamtx-ticket" && e.data.slug === cfg.slug && e.data.token) {
            useToken(e.data.token);
        } else if (e.data.type === "mediamtx-embed-look" && cfg.preview) {
            look = { ...look, ...e.data.look };
            applyLook();
        }
    });

    function popup(url) {
        const w = window.open(url, "mx-ticket", "popup=yes,width=480,height=760");
        if (!w) window.open(url, "_blank");
    }

    // ---------------------------------------------------------------- markup

    root.innerHTML = `
        <div class="mx" data-player-root>
            <video playsinline></video>
            <img class="mx-poster" alt="" hidden>
            <span class="mx-status" data-player-status></span>
            <div class="mx-top">
                <span class="mx-badge ${cfg.preview ? "preview" : ""}">${esc(cfg.preview ? t("Preview") : t("Live"))}</span>
                <span class="mx-title">${esc(cfg.title)}</span>
            </div>
            <a class="mx-logo" target="_blank" rel="noopener" hidden><img alt=""></a>
            <div class="mx-center">
                <button type="button" class="mx-bigplay" aria-label="${esc(t("Play"))}" hidden>${icon("play")}</button>
                <div class="mx-spinner" hidden></div>
                <div class="mx-offline" hidden>${icon("clock")}<p></p></div>
            </div>
            <button type="button" class="mx-unmute" hidden>${icon("muted")}<span>${esc(t("Tap to unmute"))}</span></button>
            <div class="mx-bar">
                <button type="button" class="mx-btn" data-act="play" aria-label="${esc(t("Play"))}">${icon("play")}</button>
                <div class="mx-vol">
                    <button type="button" class="mx-btn" data-act="mute" aria-label="${esc(t("Mute"))}">${icon("volume")}</button>
                    <input type="range" class="mx-volume" min="0" max="1" step="0.05" value="1" aria-label="${esc(t("Volume"))}">
                </div>
                <span class="mx-livebtn">${esc(t("Live"))}</span>
                <span class="mx-spacer"></span>
                <button type="button" class="mx-btn" data-act="pip" aria-label="${esc(t("Picture-in-picture"))}" title="${esc(t("Picture-in-picture"))}">${icon("pip")}</button>
                <button type="button" class="mx-btn" data-act="fullscreen" aria-label="${esc(t("Full screen"))}" title="${esc(t("Full screen"))}">${icon("expand")}</button>
            </div>
        </div>`;

    const box = root.firstElementChild;
    const $ = (sel) => box.querySelector(sel);
    const video = $("video");
    const poster = $(".mx-poster");
    const bigPlay = $(".mx-bigplay");
    const spinner = $(".mx-spinner");
    const offline = $(".mx-offline");
    const unmute = $(".mx-unmute");
    const bar = $(".mx-bar");
    const playBtn = $('[data-act="play"]');
    const muteBtn = $('[data-act="mute"]');
    const volume = $(".mx-volume");
    const pipBtn = $('[data-act="pip"]');
    const fsBtn = $('[data-act="fullscreen"]');
    const logo = $(".mx-logo");

    video.dataset.texts = JSON.stringify(cfg.texts);

    function applyLook() {
        box.style.setProperty("--accent", look.accent);
        box.style.setProperty("--bg", look.background);
        box.style.setProperty("--radius", look.radius + "px");
        box.classList.toggle("no-controls", !look.controls);
        box.classList.toggle("has-title", !!look.showTitle);
        bar.hidden = !look.controls || cfg.access !== "ok";
        $(".mx-title").hidden = !look.showTitle;
        $(".mx-badge").hidden = !(look.showLive || cfg.preview);
        $(".mx-livebtn").hidden = !look.showLive;
        $(".mx-top").hidden = !look.showTitle && !look.showLive && !cfg.preview;
        pipBtn.hidden = !look.showPip || !document.pictureInPictureEnabled;
        fsBtn.hidden = !look.showFullscreen;
        poster.hidden = !cfg.posterUrl;
        if (cfg.posterUrl) poster.src = cfg.posterUrl;
        logo.hidden = !cfg.logoUrl;
        if (cfg.logoUrl) {
            logo.className = "mx-logo " + look.logoPosition;
            logo.style.width = look.logoSize + "%";
            logo.style.opacity = look.logoOpacity / 100;
            logo.querySelector("img").src = cfg.logoUrl;
            if (look.logoLink) logo.href = look.logoLink; else logo.removeAttribute("href");
        }
        offline.querySelector("p").textContent = look.offlineText || t("The broadcast will start soon.");
    }
    applyLook();

    // ---------------------------------------------------------------- no access: ticket screen

    if (cfg.access !== "ok") {
        const gate = document.createElement("div");
        gate.className = "mx-gate";
        if (cfg.access === "ticket") {
            gate.innerHTML = `
                <div class="mx-gate-inner">
                    <div class="mx-gate-icon">${icon("ticket")}</div>
                    <h2>${esc(t("This stream requires a ticket"))}</h2>
                    ${cfg.priceFrom ? `<p class="mx-sub">${esc(t("Tickets from {0}", cfg.priceFrom))}</p>` : ""}
                    ${cfg.ticketInvalid ? `<p class="mx-error">${esc(t("Your ticket has expired or is invalid."))}</p>` : ""}
                    <div class="mx-actions">
                        ${cfg.priceFrom ? `<button type="button" class="mx-primary" data-act="buy">${icon("ticket")}${esc(t("Buy a ticket"))}</button>` : ""}
                        <button type="button" class="mx-secondary" data-act="signin">${icon("person")}${esc(t("Sign in"))}</button>
                    </div>
                    <div class="mx-divider">${esc(t("I have a code"))}</div>
                    <form class="mx-code">
                        <input name="code" autocomplete="off" spellcheck="false" maxlength="20" placeholder="${esc(t("Enter your ticket code"))}" aria-label="${esc(t("Enter your ticket code"))}">
                        <button type="submit" class="mx-primary">${esc(t("Watch"))}</button>
                    </form>
                    <p class="mx-error" data-code-error hidden></p>
                </div>`;
            gate.querySelector('[data-act="buy"]')?.addEventListener("click", () => popup(cfg.watchUrl));
            gate.querySelector('[data-act="signin"]').addEventListener("click", () => popup(cfg.ticketsUrl));
            const form = gate.querySelector("form");
            const input = form.querySelector("input");
            const error = gate.querySelector("[data-code-error]");
            input.addEventListener("input", () => {
                const raw = input.value.toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, 12);
                input.value = raw.match(/.{1,4}/g)?.join("-") ?? "";
            });
            form.addEventListener("submit", async (e) => {
                e.preventDefault();
                error.hidden = true;
                try {
                    const res = await fetch(cfg.redeemUrl, {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({ code: input.value }),
                    });
                    const data = await res.json();
                    if (data.ok) {
                        useToken(data.token);
                        return;
                    }
                    error.textContent = data.error === "expired" ? t("This code has expired.") : t("This code is not valid for this stream.");
                } catch {
                    error.textContent = t("Something went wrong. Please try again.");
                }
                error.hidden = false;
            });
        } else {
            gate.innerHTML = `
                <div class="mx-gate-inner">
                    <div class="mx-gate-icon">${icon("lock")}</div>
                    <h2>${esc(cfg.access === "private" ? t("This stream is private.") : t("This stream is not available."))}</h2>
                </div>`;
        }
        box.appendChild(gate);
        return;
    }

    // ---------------------------------------------------------------- playback

    let started = false;
    let soundTouched = false;
    box.classList.toggle("is-live", cfg.live);

    function start(withSound) {
        started = true;
        bigPlay.hidden = true;
        spinner.hidden = false;
        window.panelPlayer.start(video, { ...cfg.player, muted: !withSound });
    }

    // player.js reports its state in the status element; mirror it in the controls.
    new MutationObserver(() => {
        const kind = $(".mx-status").dataset.kind;
        spinner.hidden = kind !== "wait";
        if (kind === "live") {
            box.classList.add("is-live");
            offline.hidden = true;
        } else if (kind === "error") {
            box.classList.remove("is-live", "is-playing");
            offline.hidden = false;
        }
    }).observe($(".mx-status"), { attributes: true, childList: true, characterData: true, subtree: true });

    function syncButtons() {
        const paused = video.paused || !started;
        playBtn.innerHTML = icon(paused ? "play" : "pause");
        playBtn.setAttribute("aria-label", paused ? t("Play") : t("Pause"));
        muteBtn.innerHTML = icon(video.muted || video.volume === 0 ? "muted" : "volume");
        muteBtn.setAttribute("aria-label", video.muted ? t("Unmute") : t("Mute"));
        volume.value = video.muted ? 0 : video.volume;
        // Offer sound when the owner wants sound (autoplay starts muted) or there are no controls to unmute with.
        unmute.hidden = !started || !video.muted || video.paused || soundTouched || (look.controls && look.muted);
        const fs = !!document.fullscreenElement;
        fsBtn.innerHTML = icon(fs ? "shrink" : "expand");
        fsBtn.setAttribute("aria-label", fs ? t("Exit full screen") : t("Full screen"));
    }

    video.addEventListener("playing", () => {
        box.classList.add("is-playing");
        offline.hidden = true;
        spinner.hidden = true;
        bigPlay.hidden = true;
        syncButtons();
    });
    video.addEventListener("pause", () => {
        bigPlay.hidden = !look.controls && started;
        syncButtons();
    });
    video.addEventListener("volumechange", syncButtons);
    document.addEventListener("fullscreenchange", syncButtons);

    function togglePlay() {
        if (!started) start(!look.muted);
        else if (video.paused) video.play().catch(() => { });
        else video.pause();
    }

    function toggleMute() {
        soundTouched = true;
        video.muted = !video.muted;
        if (!video.muted && video.volume === 0) video.volume = 1;
        if (video.paused && started) video.play().catch(() => { });
    }

    bigPlay.addEventListener("click", togglePlay);
    playBtn.addEventListener("click", togglePlay);
    muteBtn.addEventListener("click", toggleMute);
    unmute.addEventListener("click", (e) => { e.stopPropagation(); video.muted = false; video.volume = video.volume || 1; });
    volume.addEventListener("input", () => {
        soundTouched = true;
        video.volume = Number(volume.value);
        video.muted = video.volume === 0;
    });
    pipBtn.addEventListener("click", () => {
        if (document.pictureInPictureElement) document.exitPictureInPicture();
        else video.requestPictureInPicture?.().catch(() => { });
    });
    fsBtn.addEventListener("click", () => {
        if (document.fullscreenElement) document.exitFullscreen();
        else if (box.requestFullscreen) box.requestFullscreen().catch(() => { });
        else video.webkitEnterFullscreen?.(); // iPhone
    });
    video.addEventListener("click", () => { if (look.controls) togglePlay(); });
    video.addEventListener("dblclick", () => { if (look.showFullscreen) fsBtn.click(); });

    // Hide the controls while the video plays and the pointer rests.
    let idleTimer;
    function wake() {
        box.classList.remove("idle");
        clearTimeout(idleTimer);
        idleTimer = setTimeout(() => { if (!video.paused) box.classList.add("idle"); }, 2800);
    }
    ["pointermove", "pointerdown", "keydown"].forEach((ev) => box.addEventListener(ev, wake));
    box.addEventListener("pointerleave", () => { if (!video.paused) box.classList.add("idle"); });

    if (look.autoplay) {
        start(false); // browsers only autoplay without sound; "Tap to unmute" offers sound
    } else {
        bigPlay.hidden = false;
        spinner.hidden = true;
    }
    syncButtons();
})();
