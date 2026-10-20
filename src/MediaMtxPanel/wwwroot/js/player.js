// Stream preview player. Plays a MediaMTX path via WebRTC (WHEP, using MediaMTX's own reader.js)
// with HLS (hls.js) as alternative. Used by the customer portal (through JS interop) and by the
// public watch page (auto-initialised from data-player attributes) and by the embedded player (embed/embed.js).
(function () {
    const HLS_JS = "https://cdn.jsdelivr.net/npm/hls.js@1.5.20/dist/hls.min.js";
    const players = new WeakMap();
    const scripts = {};

    function loadScript(src) {
        if (!scripts[src]) {
            scripts[src] = new Promise((resolve, reject) => {
                const s = document.createElement("script");
                s.src = src;
                s.onload = resolve;
                s.onerror = () => { delete scripts[src]; reject(new Error("could not load " + src)); };
                document.head.appendChild(s);
            });
        }
        return scripts[src];
    }

    // Status texts arrive translated in data-texts on the <video> (see StreamPlayer.razor); English is the fallback.
    function tr(video, text) {
        try {
            const texts = JSON.parse(video.dataset.texts || "{}");
            return texts[text] ?? text;
        } catch {
            return text;
        }
    }

    function setStatus(video, text, kind) {
        text = tr(video, text);
        const el = video.closest("[data-player-root]")?.querySelector("[data-player-status]");
        if (el) {
            el.textContent = text;
            el.dataset.kind = kind || "";
        }
    }

    function stop(video) {
        const p = players.get(video);
        if (p) {
            try { p.close(); } catch { /* already closed */ }
            players.delete(video);
        }
        video.srcObject = null;
        video.removeAttribute("src");
        video.load();
    }

    // WebRTC media needs UDP/TCP 8189 to MediaMTX, which a reverse proxy does not carry. When signalling works
    // but no picture arrives, or reader.js cannot be loaded at all, switch to HLS, which runs over https only.
    const WEBRTC_FALLBACK_MS = 12000;
    // After such a fallback, later visits start with HLS for a while instead of waiting again (e.g. behind
    // Cloudflare, which never passes WebRTC media). Choosing WebRTC again on the same player still tries it.
    const WEBRTC_FAILED_FOR_MS = 30 * 60 * 1000;
    const startedBefore = new WeakSet();

    function failureKey(o) {
        try { return "panelPlayer.webrtcFailed:" + new URL(o.whepUrl, location.href).origin; } catch { return null; }
    }

    function webRtcFailedRecently(o) {
        try {
            const at = Number(localStorage.getItem(failureKey(o)) || 0);
            return Date.now() - at < WEBRTC_FAILED_FOR_MS;
        } catch {
            return false;
        }
    }

    function rememberWebRtcFailure(o, failed) {
        try {
            const key = failureKey(o);
            if (!key) return;
            if (failed) localStorage.setItem(key, String(Date.now()));
            else localStorage.removeItem(key);
        } catch { /* storage blocked: just try WebRTC next time */ }
    }

    function fallBackToHls(video, o) {
        rememberWebRtcFailure(o, true);
        stop(video);
        setStatus(video, "WebRTC could not connect; switching to HLS…", "wait");
        startHls(video, o).catch((e) => setStatus(video, tr(video, "Player could not start:") + " " + e.message, "error"));
    }

    async function startWebRtc(video, o) {
        try {
            await loadScript(o.readerJsUrl);
        } catch (e) {
            if (o.hlsUrl) {
                fallBackToHls(video, o);
                return;
            }
            throw e;
        }
        setStatus(video, "Connecting via WebRTC…", "wait");
        let gotTrack = false;
        const reader = new window.MediaMTXWebRTCReader({
            url: o.whepUrl,
            user: o.user || "",
            pass: o.pass || "",
            onError: (err) => {
                if (String(err).includes("codecs not supported")) {
                    // This browser cannot decode the stream's codecs over WebRTC; HLS usually can.
                    stop(video);
                    startHls(video, o).catch((e) => setStatus(video, tr(video, "Player could not start:") + " " + e.message, "error"));
                    return;
                }
                setStatus(video, String(err).includes("not found")
                    ? "No picture yet: the stream is not live yet"
                    : tr(video, "No picture yet:") + " " + err, "error");
            },
            onTrack: (evt) => {
                if (!gotTrack) rememberWebRtcFailure(o, false);
                gotTrack = true;
                if (video.srcObject !== evt.streams[0]) {
                    video.srcObject = evt.streams[0];
                    video.play().catch(() => { /* the user can press play */ });
                }
                setStatus(video, "Live (WebRTC)", "live");
            },
        });
        players.set(video, reader);
        // reader.js also reports "stream not found" when its session ended before it could send its ICE
        // candidates (a 404 on the PATCH), which is what a failed connection looks like from outside the
        // network. So ask HLS whether the stream is live: if it is, WebRTC is the problem and HLS takes over;
        // if not, keep waiting on WebRTC and ask again later.
        const check = async () => {
            if (players.get(video) !== reader || gotTrack) return;
            if (await isLive(o)) {
                if (players.get(video) === reader && !gotTrack) fallBackToHls(video, o);
            } else {
                setTimeout(check, WEBRTC_FALLBACK_MS);
            }
        };
        setTimeout(check, WEBRTC_FALLBACK_MS);
    }

    async function isLive(o) {
        if (!o.hlsUrl) return false;
        try {
            const headers = o.user ? { Authorization: "Basic " + btoa(o.user + ":" + o.pass) } : {};
            const res = await fetch(o.hlsUrl, { headers, cache: "no-store" });
            return res.ok;
        } catch {
            return false;
        }
    }

    async function startHls(video, o) {
        setStatus(video, "Connecting via HLS…", "wait");
        const withAuth = (xhr) => {
            if (o.user) xhr.setRequestHeader("Authorization", "Basic " + btoa(o.user + ":" + o.pass));
        };
        if (!o.user && video.canPlayType("application/vnd.apple.mpegurl")) {
            video.src = o.hlsUrl; // Safari plays HLS natively
            players.set(video, { close() { } });
        } else {
            await loadScript(HLS_JS);
            if (!window.Hls.isSupported()) {
                setStatus(video, "This browser does not support HLS.", "error");
                return;
            }
            const hls = new window.Hls({ xhrSetup: withAuth, liveSyncDurationCount: 2 });
            hls.on(window.Hls.Events.MANIFEST_PARSED, () => setStatus(video, "Live (HLS)", "live"));
            hls.on(window.Hls.Events.ERROR, (_, data) => {
                if (data.fatal) {
                    setStatus(video, tr(video, "No picture yet (HLS):") + " " + (data.response?.code || data.details), "error");
                    setTimeout(() => { if (players.get(video) === hls) { hls.loadSource(o.hlsUrl); } }, 4000);
                }
            });
            hls.loadSource(o.hlsUrl);
            hls.attachMedia(video);
            players.set(video, { close() { hls.destroy(); } });
        }
        video.play().catch(() => { });
    }

    async function start(video, options) {
        stop(video);
        // Browsers only autoplay muted video. Set the property: Blazor renders "muted" as an attribute only.
        // The embedded player passes muted: false after a click, when sound is allowed.
        video.muted = options.muted !== false;
        video.autoplay = true;
        const firstStart = !startedBefore.has(video);
        startedBefore.add(video);
        try {
            if (options.mode === "hls" || (firstStart && options.hlsUrl && webRtcFailedRecently(options))) {
                await startHls(video, options);
            } else {
                await startWebRtc(video, options);
            }
        } catch (e) {
            setStatus(video, tr(video, "Player could not start:") + " " + e.message, "error");
        }
    }

    function initAll() {
        document.querySelectorAll("video[data-player]").forEach((video) => {
            if (video.dataset.playerStarted) return;
            video.dataset.playerStarted = "1";
            const options = JSON.parse(video.dataset.player);
            start(video, options);
            const root = video.closest("[data-player-root]");
            root?.querySelectorAll("[data-player-mode]").forEach((btn) => {
                btn.addEventListener("click", () => {
                    options.mode = btn.dataset.playerMode;
                    root.querySelectorAll("[data-player-mode]").forEach((b) => b.classList.toggle("active", b === btn));
                    start(video, options);
                });
            });
        });
    }

    window.panelPlayer = { start, stop, initAll };
    document.addEventListener("DOMContentLoaded", initAll);
    // Blazor enhanced navigation swaps page content without a full load.
    window.addEventListener("load", () => window.Blazor?.addEventListener?.("enhancedload", initAll));
})();
