/*
 * Bildirishnomalar qo'ng'iroqchasi (o'quvchi). /Notifications/Feed har daqiqada so'raladi (tab ko'rinib turganda).
 * - Matn: I18n.t("notif." + kind, args) — tanlangan tilda;
 * - jonli dars efirda bo'lsa — panel tepasida va sahifa pastida "Hozir jonli efir" chaqiruvi;
 * - brauzer ruxsat bergan bo'lsa — yangi bildirishnoma tizim xabari sifatida ham chiqadi.
 */
(function () {
    var root = document.querySelector("[data-notify]");
    if (!root) return;
    var btn = root.querySelector("[data-notify-toggle]");
    var panel = root.querySelector("[data-notify-panel]");
    var countEl = root.querySelector("[data-notify-count]");
    var list = root.querySelector("[data-notify-list]");
    var liveEl = root.querySelector("[data-notify-live]");
    var liveTitle = root.querySelector("[data-notify-live-title]");
    var pushBtn = root.querySelector("[data-notify-push]");
    var tokenEl = root.querySelector('input[name="__RequestVerificationToken"]');
    var feed = null;
    var LAST_KEY = "nt.lastId";

    function lastShownId() { try { return +localStorage.getItem(LAST_KEY) || 0; } catch (e) { return 0; } }
    function setLastShown(id) { try { localStorage.setItem(LAST_KEY, String(id)); } catch (e) { } }

    function ago(iso) {
        var min = Math.floor((Date.now() - new Date(iso).getTime()) / 60000);
        if (min < 1) return I18n.t("pres.justNow");
        if (min < 60) return I18n.t("pres.minAgo", { n: min });
        var h = Math.floor(min / 60);
        if (h < 24) return I18n.t("pres.hourAgo", { n: h });
        return I18n.t("pres.dayAgo", { n: Math.floor(h / 24) });
    }

    function text(n) { return I18n.t("notif." + n.kind, n.args || {}); }

    function post(url) {
        return fetch(url, {
            method: "POST", credentials: "same-origin",
            headers: { "RequestVerificationToken": tokenEl ? tokenEl.value : "" }
        }).catch(function () { });
    }

    function render() {
        if (!feed) return;
        countEl.hidden = feed.unread === 0;
        countEl.textContent = feed.unread > 99 ? "99+" : String(feed.unread);
        btn.classList.toggle("has-unread", feed.unread > 0);

        var onLivePage = /^\/Live\/Watch\//i.test(location.pathname);
        if (feed.live) {
            liveEl.hidden = false;
            liveEl.href = "/Live/Watch/" + feed.live.id;
            liveTitle.textContent = feed.live.title;
        } else {
            liveEl.hidden = true;
        }
        renderLiveCall(feed.live && !onLivePage ? feed.live : null);

        list.textContent = "";
        if (!feed.items.length) {
            var e = document.createElement("div");
            e.className = "nt-empty";
            e.textContent = I18n.t("notif.empty");
            list.appendChild(e);
        }
        feed.items.forEach(function (n) {
            var a = document.createElement("a");
            a.className = "nt-item" + (n.read ? "" : " unread");
            a.href = n.url || "#";
            a.dataset.id = n.id;
            var ic = document.createElement("span");
            ic.className = "nt-ic nt-ic-" + n.kind;
            ic.setAttribute("aria-hidden", "true");
            var body = document.createElement("span");
            body.className = "nt-body";
            var t = document.createElement("span");
            t.className = "nt-text";
            t.textContent = text(n);
            var time = document.createElement("span");
            time.className = "nt-time";
            time.textContent = ago(n.at);
            body.appendChild(t);
            body.appendChild(time);
            a.appendChild(ic);
            a.appendChild(body);
            a.addEventListener("click", function (ev) {
                if (!n.read) post("/Notifications/Read?id=" + n.id);
                if (!n.url) ev.preventDefault();
            });
            list.appendChild(a);
        });

        if (pushBtn) pushBtn.hidden = !("Notification" in window) || Notification.permission !== "default";
    }

    // Sahifa pastida — "Hozir jonli efir" chaqiruvi (bir sessiyada yopilgan bo'lsa qayta chiqmaydi)
    var call = null;
    function renderLiveCall(live) {
        var key = live ? "nt.liveClosed." + live.id : null;
        var closed = false;
        try { closed = key && sessionStorage.getItem(key) === "1"; } catch (e) { }
        if (!live || closed) { if (call) { call.remove(); call = null; } return; }
        if (!call) {
            call = document.createElement("div");
            call.className = "nt-call";
            call.setAttribute("role", "status");
            call.innerHTML = '<i class="nt-live-dot" aria-hidden="true"></i><span class="nt-call-text"></span>' +
                '<a class="nt-call-go"></a><button type="button" class="nt-call-x" aria-label="×">×</button>';
            document.body.appendChild(call);
            call.querySelector(".nt-call-x").addEventListener("click", function () {
                try { sessionStorage.setItem(key, "1"); } catch (e) { }
                call.remove(); call = null;
            });
        }
        call.querySelector(".nt-call-text").textContent = I18n.t("notif.liveCall", { title: live.title });
        var go = call.querySelector(".nt-call-go");
        go.href = "/Live/Watch/" + live.id;
        go.textContent = I18n.t("notif.join");
    }

    function systemNotify(items) {
        if (!("Notification" in window) || Notification.permission !== "granted") return;
        var last = lastShownId();
        var fresh = items.filter(function (n) { return !n.read && n.id > last; });
        if (!fresh.length) return;
        setLastShown(Math.max.apply(null, items.map(function (n) { return n.id; })));
        fresh.slice(0, 3).forEach(function (n) {
            try {
                var sys = new Notification("AVTOPRAVA_SAM", { body: text(n), icon: "/images/favicon.png", tag: "nt" + n.id });
                sys.onclick = function () { window.focus(); if (n.url) location.href = n.url; };
            } catch (e) { }
        });
    }

    function load(first) {
        if (!first && document.visibilityState !== "visible") return;
        fetch("/Notifications/Feed", { credentials: "same-origin", cache: "no-store", headers: { "Accept": "application/json" } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                feed = data;
                render();
                if (first) {
                    // Birinchi yuklashda eski bildirishnomalar tizim xabari bo'lib chiqmasin
                    if (!lastShownId() && data.items.length) setLastShown(Math.max.apply(null, data.items.map(function (n) { return n.id; })));
                } else {
                    systemNotify(data.items);
                }
            })
            .catch(function () { });
    }

    function open(on) {
        panel.hidden = !on;
        btn.setAttribute("aria-expanded", on ? "true" : "false");
    }
    btn.addEventListener("click", function (e) { e.stopPropagation(); open(panel.hidden); });
    document.addEventListener("click", function (e) { if (!root.contains(e.target)) open(false); });
    document.addEventListener("keydown", function (e) { if (e.key === "Escape") open(false); });

    root.querySelector("[data-notify-readall]").addEventListener("click", function () {
        post("/Notifications/Read").then(function () { load(true); });
    });
    if (pushBtn) pushBtn.addEventListener("click", function () {
        Notification.requestPermission().then(render);
    });

    document.addEventListener("i18n:change", render);
    document.addEventListener("visibilitychange", function () { if (document.visibilityState === "visible") load(false); });
    load(true);
    setInterval(function () { load(false); }, 60000);
})();
