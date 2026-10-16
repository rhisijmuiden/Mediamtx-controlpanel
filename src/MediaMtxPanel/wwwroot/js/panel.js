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
    }
};
