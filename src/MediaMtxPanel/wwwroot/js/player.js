// Stream preview player. Plays a MediaMTX path via WebRTC (WHEP, using MediaMTX's own reader.js)
// with HLS (hls.js) as alternative. Used by the customer portal (through JS interop) and by the
// public watch page (auto-initialised from data-player attributes).
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

    function setStatus(video, text, kind) {
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

    async function startWebRtc(video, o) {
        await loadScript(o.readerJsUrl);
        setStatus(video, "Connecting via WebRTC…", "wait");
        const reader = new window.MediaMTXWebRTCReader({
            url: o.whepUrl,
            user: o.user || "",
            pass: o.pass || "",
            onError: (err) => {
                if (String(err).includes("codecs not supported")) {
                    // This browser cannot decode the stream's codecs over WebRTC; HLS usually can.
                    stop(video);
                    startHls(video, o).catch((e) => setStatus(video, "Player could not start: " + e.message, "error"));
                    return;
                }
                setStatus(video, String(err).includes("not found")
                    ? "No picture yet: the stream is not live yet"
                    : "No picture yet: " + err, "error");
            },
            onTrack: (evt) => {
                if (video.srcObject !== evt.streams[0]) {
                    video.srcObject = evt.streams[0];
                    video.play().catch(() => { /* the user can press play */ });
                }
                setStatus(video, "Live (WebRTC)", "live");
            },
        });
        players.set(video, reader);
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
                    setStatus(video, "No picture yet (HLS): " + (data.response?.code || data.details), "error");
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
        video.muted = true;
        video.autoplay = true;
        try {
            if (options.mode === "hls") {
                await startHls(video, options);
            } else {
                await startWebRtc(video, options);
            }
        } catch (e) {
            setStatus(video, "Player could not start: " + e.message, "error");
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
