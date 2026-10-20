// Script embed for the MediaMTX Panel player. Paste on any website:
//   <div data-mediamtx-embed="https://panel.example.com/embed/abc123" data-aspect="16:9"></div>
//   <script src="https://panel.example.com/js/embed-loader.js" async></script>
// Every such element becomes a responsive iframe; data-width="640" gives a fixed width instead.
(function () {
    function mount(el) {
        if (el.dataset.mediamtxMounted) return;
        el.dataset.mediamtxMounted = "1";
        const src = new URL(el.dataset.mediamtxEmbed, document.baseURI);
        const token = new URLSearchParams(location.search).get("ticket");
        if (token) src.searchParams.set("t", token);
        const [w, h] = (el.dataset.aspect || "16:9").split(":").map(Number);
        const frame = document.createElement("iframe");
        frame.src = src.toString();
        frame.title = el.dataset.title || "Live stream";
        frame.allow = "autoplay; fullscreen; picture-in-picture; encrypted-media";
        frame.allowFullscreen = true;
        frame.loading = "lazy";
        frame.referrerPolicy = "strict-origin-when-cross-origin";
        frame.style.cssText = "display:block;border:0;width:100%;max-width:100%;aspect-ratio:" + w + " / " + h + ";"
            + "border-radius:" + (el.dataset.radius || "12") + "px;background:#000";
        if (el.dataset.width) frame.style.width = el.dataset.width + "px";
        el.replaceChildren(frame);
    }
    function scan() { document.querySelectorAll("[data-mediamtx-embed]").forEach(mount); }
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", scan); else scan();
    if (window.MutationObserver) new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
})();
