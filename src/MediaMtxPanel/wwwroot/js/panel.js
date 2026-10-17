window.panel = {
    copy: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return;
        } catch {
            // navigator.clipboard needs a secure context (https or localhost); fall back for plain http.
        }
        const ta = document.createElement("textarea");
        ta.value = text;
        ta.style.position = "fixed";
        ta.style.opacity = "0";
        document.body.appendChild(ta);
        ta.select();
        document.execCommand("copy");
        document.body.removeChild(ta);
    },

    // After a port change: wait until the panel answers at the new address, then go there.
    goWhenUp: function (url) {
        const started = Date.now();
        const attempt = () => {
            fetch(url, { mode: "no-cors", cache: "no-store" })
                .then(() => { window.location.href = url; })
                .catch(() => { if (Date.now() - started < 120000) setTimeout(attempt, 2000); });
        };
        setTimeout(attempt, 4000);
    }
};
