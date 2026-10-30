/*
 * Onlayn holati (admin sahifalari: Tahlil ro'yxati, o'quvchi sahifasi).
 * Belgilash:  <span data-pres-uid="12" data-online="1|0" data-seen="ISO">
 *               <i data-pres-dot></i> <span data-pres-text></span>
 *             </span>
 *             <span data-pres-count></span>  — hozir onlayn o'quvchilar soni
 * Har 30 soniyada /Users/Presence dan yangilanadi (sahifa qayta yuklanmaydi).
 */
(function () {
    function pad(n) { return (n < 10 ? "0" : "") + n; }
    function stamp(d) { return pad(d.getDate()) + "." + pad(d.getMonth() + 1) + "." + d.getFullYear() + " " + pad(d.getHours()) + ":" + pad(d.getMinutes()); }
    function ago(d) {
        var min = Math.floor((Date.now() - d.getTime()) / 60000);
        if (min < 1) return I18n.t("pres.justNow");
        if (min < 60) return I18n.t("pres.minAgo", { n: min });
        var h = Math.floor(min / 60);
        if (h < 24) return I18n.t("pres.hourAgo", { n: h });
        return I18n.t("pres.dayAgo", { n: Math.floor(h / 24) });
    }

    function items() { return Array.prototype.slice.call(document.querySelectorAll("[data-pres-uid]")); }

    function paint() {
        var online = {};
        items().forEach(function (el) {
            var on = el.dataset.online === "1";
            if (on) online[el.dataset.presUid] = true;
            el.classList.toggle("is-online", on);
            var dot = el.querySelector("[data-pres-dot]");
            if (dot) dot.classList.toggle("on", on);
            var text = el.querySelector("[data-pres-text]");
            if (!text) return;
            if (on) {
                text.textContent = I18n.t("pres.online");
                text.removeAttribute("title");
            } else if (el.dataset.seen) {
                var d = new Date(el.dataset.seen);
                text.textContent = ago(d);
                text.title = stamp(d);
            } else {
                text.textContent = I18n.t("pres.never");
                text.removeAttribute("title");
            }
        });
        var n = Object.keys(online).length;
        document.querySelectorAll("[data-pres-count]").forEach(function (c) {
            c.setAttribute("data-i18n-args", JSON.stringify({ n: n }));
            c.textContent = I18n.t("pres.onlineNow", { n: n });
        });
    }

    function refresh() {
        fetch("/Users/Presence", { credentials: "same-origin", cache: "no-store", headers: { "Accept": "application/json" } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (list) {
                if (!list) return;
                var byId = {};
                list.forEach(function (x) { byId[x.id] = x; });
                items().forEach(function (el) {
                    var x = byId[el.dataset.presUid];
                    if (!x) return;
                    el.dataset.online = x.online ? "1" : "0";
                    el.dataset.seen = x.seen || "";
                });
                paint();
            })
            .catch(function () { });
    }

    function start() {
        paint();
        setInterval(refresh, 30000);
        document.addEventListener("i18n:change", paint);
    }
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
