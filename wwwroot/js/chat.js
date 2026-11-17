/*
 * Xabarlar (admin ↔ o'quvchi). Chat.open(cfg) — bitta suhbat oynasi; Chat.admin(cfg) — admin: suhbatlar ro'yxati + oyna.
 * Yangi xabarlar har 4 soniyada (sahifa ko'rinib turganda) so'raladi; matn faqat textContent orqali chiqariladi.
 */
(function () {
    "use strict";
    function t(k, a) { return window.I18n ? I18n.t(k, a) : k; }
    function token() {
        var m = document.querySelector('meta[name="csrf"]');
        var i = document.querySelector('input[name="__RequestVerificationToken"]');
        return (m && m.content) || (i && i.value) || "";
    }
    function pad(n) { return (n < 10 ? "0" : "") + n; }
    function hm(d) { return pad(d.getHours()) + ":" + pad(d.getMinutes()); }
    function dayKey(d) { return d.getFullYear() + "-" + d.getMonth() + "-" + d.getDate(); }
    function dayLabel(d) {
        var now = new Date(), y = new Date(now); y.setDate(now.getDate() - 1);
        if (dayKey(d) === dayKey(now)) return t("msg.today");
        if (dayKey(d) === dayKey(y)) return t("msg.yesterday");
        return pad(d.getDate()) + "." + pad(d.getMonth() + 1) + "." + d.getFullYear();
    }
    function post(url, data) {
        var body = new URLSearchParams(data);
        return fetch(url, { method: "POST", credentials: "same-origin", headers: { "RequestVerificationToken": token() }, body: body });
    }

    // ── Bitta suhbat ──
    function Thread(root, userId) {
        var log = root.querySelector("[data-log]"), form = root.querySelector("[data-form]"), input = form.querySelector("textarea");
        var last = 0, lastDay = null, readUpTo = 0, timer = null, busy = false, stopped = false;
        var q = userId ? "u=" + userId + "&" : "";

        function bubble(m) {
            var d = new Date(m.at);
            if (dayKey(d) !== lastDay) {
                lastDay = dayKey(d);
                var sep = document.createElement("div");
                sep.className = "ch-day";
                sep.textContent = dayLabel(d);
                log.appendChild(sep);
            }
            var el = document.createElement("div");
            el.className = "ch-msg" + (m.mine ? " mine" : "");
            el.dataset.id = m.id;
            el.dataset.at = m.at;
            el.textContent = m.text;
            var tm = document.createElement("time");
            tm.textContent = hm(d);
            if (m.mine) { var tick = document.createElement("i"); tick.dataset.tick = ""; tm.appendChild(tick); }
            el.appendChild(tm);
            log.appendChild(el);
        }
        function ticks() {
            log.querySelectorAll(".ch-msg.mine").forEach(function (el) {
                var i = el.querySelector("[data-tick]");
                if (i) i.textContent = +el.dataset.id <= readUpTo ? "✓✓" : "✓";
            });
        }
        function empty() {
            var e = log.querySelector(".ch-empty");
            if (log.querySelector(".ch-msg")) { if (e) e.remove(); return; }
            if (!e) { e = document.createElement("div"); e.className = "ch-empty"; e.innerHTML = "<span>💬</span>"; var p = document.createElement("p"); p.textContent = t(userId ? "msg.emptyAdmin" : "msg.empty"); e.appendChild(p); log.appendChild(e); }
        }
        function poll() {
            if (busy || stopped) return;
            busy = true;
            fetch("/Messages/Poll?" + q + "after=" + last, { credentials: "same-origin", cache: "no-store" })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (d) {
                    if (!d) return;
                    var atBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 80;
                    d.items.forEach(function (m) { if (m.id > last) { bubble(m); last = m.id; } });
                    readUpTo = d.readUpTo || readUpTo;
                    if (d.ids) syncDeleted(d.ids);
                    ticks(); empty();
                    if (d.items.length && (atBottom || last === d.items[d.items.length - 1].id)) log.scrollTop = log.scrollHeight;
                    document.dispatchEvent(new CustomEvent("chat:read"));
                })
                .catch(function () { })
                .then(function () { busy = false; });
        }
        form.addEventListener("submit", function (e) {
            e.preventDefault();
            var text = input.value.trim();
            if (!text) return;
            var btn = form.querySelector("button[type=submit]");
            btn.disabled = true;
            post("/Messages/Send", userId ? { u: userId, text: text } : { text: text })
                .then(function (r) { if (r.status === 429) throw new Error("many"); return r.ok ? r.json() : Promise.reject(); })
                .then(function (m) {
                    input.value = ""; input.style.height = "";
                    if (m.id > last) { bubble(m); last = m.id; }
                    ticks(); empty();
                    log.scrollTop = log.scrollHeight;
                    document.dispatchEvent(new CustomEvent("chat:sent"));
                })
                .catch(function (e) { alert(t(e && e.message === "many" ? "msg.tooMany" : "msg.sendFail")); })
                .then(function () { btn.disabled = false; input.focus(); });
        });
        input.addEventListener("keydown", function (e) {
            if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); form.requestSubmit(); }
        });
        input.addEventListener("input", function () { input.style.height = "auto"; input.style.height = Math.min(160, input.scrollHeight) + "px"; });
        // ── O'chirish: boshqa tomon o'chirgan xabarlar ham shu yerdan yo'qoladi ──
        function syncDeleted(ids) {
            var set = {}, min = Infinity;
            ids.forEach(function (i) { set[i] = 1; if (i < min) min = i; });
            var full = ids.length < 500;
            log.querySelectorAll(".ch-msg").forEach(function (el) {
                var id = +el.dataset.id;
                if (!set[id] && (full || id > min)) el.remove();
            });
            tidy();
        }
        function tidy() {
            // Ichida xabar qolmagan sana ajratgichlari olib tashlanadi
            log.querySelectorAll(".ch-day").forEach(function (sep) {
                var n = sep.nextElementSibling;
                if (!n || n.classList.contains("ch-day")) sep.remove();
            });
            var lastSep = log.querySelectorAll(".ch-day");
            lastDay = null;
            if (lastSep.length) {
                var msgs = log.querySelectorAll(".ch-msg");
                if (msgs.length) lastDay = dayKey(new Date(msgs[msgs.length - 1].dataset.at));
            }
            empty();
        }

        // Tanlash rejimi: xabarlarni belgilab o'chirish yoki butun suhbatni o'chirish
        var old = root.querySelector(".ch-tools");
        if (old) old.remove();
        var tools = document.createElement("div");
        tools.className = "ch-tools";
        tools.innerHTML =
            '<button type="button" class="ch-tool" data-act="select"></button>' +
            '<button type="button" class="ch-tool danger" data-act="all"></button>' +
            '<span class="ch-tool-n" hidden></span>' +
            '<button type="button" class="ch-tool danger" data-act="del" hidden></button>' +
            '<button type="button" class="ch-tool" data-act="cancel" hidden></button>';
        log.parentNode.insertBefore(tools, log);
        var selecting = false;
        function label() {
            tools.querySelector('[data-act="select"]').textContent = "☑ " + t("msg.select");
            tools.querySelector('[data-act="all"]').textContent = "🗑 " + t("msg.deleteAll");
            tools.querySelector('[data-act="del"]').textContent = "🗑 " + t("msg.deleteSel");
            tools.querySelector('[data-act="cancel"]').textContent = t("users.clearSel");
            var n = log.querySelectorAll(".ch-msg.sel").length;
            var nEl = tools.querySelector(".ch-tool-n");
            nEl.textContent = t("msg.selectedN", { n: n });
            nEl.hidden = !selecting;
            tools.querySelector('[data-act="select"]').hidden = selecting;
            tools.querySelector('[data-act="all"]').hidden = selecting;
            tools.querySelector('[data-act="del"]').hidden = !selecting;
            tools.querySelector('[data-act="del"]').disabled = n === 0;
            tools.querySelector('[data-act="cancel"]').hidden = !selecting;
        }
        function setSelecting(on) {
            selecting = on;
            log.classList.toggle("selecting", on);
            if (!on) log.querySelectorAll(".ch-msg.sel").forEach(function (el) { el.classList.remove("sel"); });
            label();
        }
        function remove(params, done) {
            post("/Messages/Delete", params).then(function (r) { return r.ok ? r.json() : Promise.reject(); })
                .then(done).catch(function () { alert(t("msg.deleteFail")); });
        }
        log.addEventListener("click", function (e) {
            if (!selecting) return;
            var m = e.target.closest(".ch-msg");
            if (!m) return;
            m.classList.toggle("sel");
            label();
        });
        tools.addEventListener("click", function (e) {
            var b = e.target.closest("[data-act]");
            if (!b) return;
            var act = b.getAttribute("data-act");
            if (act === "select") setSelecting(true);
            else if (act === "cancel") setSelecting(false);
            else if (act === "del") {
                var sel = Array.prototype.slice.call(log.querySelectorAll(".ch-msg.sel"));
                if (!sel.length || !confirm(t("msg.deleteSelConfirm", { n: sel.length }))) return;
                var params = new URLSearchParams();
                if (userId) params.append("u", userId);
                sel.forEach(function (el) { params.append("ids", el.dataset.id); });
                remove(params, function () { sel.forEach(function (el) { el.remove(); }); setSelecting(false); tidy(); document.dispatchEvent(new CustomEvent("chat:sent")); });
            } else if (act === "all") {
                if (!log.querySelector(".ch-msg") || !confirm(t("msg.deleteAllConfirm"))) return;
                remove(userId ? { u: userId, all: "true" } : { all: "true" }, function () {
                    log.querySelectorAll(".ch-msg, .ch-day").forEach(function (el) { el.remove(); });
                    lastDay = null; tidy(); document.dispatchEvent(new CustomEvent("chat:sent"));
                });
            }
        });
        document.addEventListener("i18n:change", label);
        label();

        poll();
        timer = setInterval(function () { if (document.visibilityState === "visible") poll(); }, 4000);
        this.stop = function () { stopped = true; clearInterval(timer); tools.remove(); };
        empty();
    }

    var Chat = {};
    Chat.open = function (root, userId) { return new Thread(root, userId); };

    // ── Admin: suhbatlar ro'yxati ──
    Chat.admin = function (cfg) {
        var wrap = document.querySelector("[data-chat-wrap]"), listEl = document.querySelector("[data-threads]");
        var search = document.querySelector("[data-thread-search]"), pane = document.querySelector("[data-pane]");
        var head = pane.querySelector("[data-pane-name]"), link = pane.querySelector("[data-pane-link]");
        var current = null, openId = cfg.open || null, timer = null;

        function initials(n) { return (n || "?").split(/\s+/).slice(0, 2).map(function (w) { return w.charAt(0).toUpperCase(); }).join(""); }
        function render(rows) {
            listEl.textContent = "";
            if (!rows.length) {
                var p = document.createElement("p"); p.className = "ca-muted ca-small"; p.style.padding = "14px";
                p.textContent = t(search.value.trim() ? "msg.noFound" : "msg.noThreads"); listEl.appendChild(p); return;
            }
            rows.forEach(function (r) {
                var a = document.createElement("a");
                a.className = "ch-thread" + (r.id === openId ? " active" : "");
                a.href = "?u=" + r.id;
                a.innerHTML = '<span class="lg-av"></span><span class="ch-thread-mid"><b></b><small></small></span><span class="ch-thread-side"></span>';
                a.querySelector(".lg-av").textContent = initials(r.name);
                if (r.online) { var dot = document.createElement("i"); dot.className = "pres-dot on"; a.querySelector(".lg-av").appendChild(dot); }
                a.querySelector("b").textContent = r.name;
                a.querySelector("small").textContent = r.last ? (r.last.fromAdmin ? t("msg.you") + ": " : "") + r.last.text : "@" + r.username;
                var side = a.querySelector(".ch-thread-side");
                if (r.last) { var tm = document.createElement("span"); tm.textContent = hm(new Date(r.last.at)); side.appendChild(tm); }
                if (r.unread) { var u = document.createElement("span"); u.className = "ch-unread"; u.textContent = r.unread; side.appendChild(u); }
                a.addEventListener("click", function (e) { e.preventDefault(); open(r.id, r.name, r.username); });
                listEl.appendChild(a);
            });
        }
        function load() {
            fetch("/Messages/Threads?q=" + encodeURIComponent(search.value.trim()), { credentials: "same-origin", cache: "no-store" })
                .then(function (r) { return r.ok ? r.json() : []; }).then(render).catch(function () { });
        }
        function open(id, name, username) {
            openId = id;
            if (current) current.stop();
            var log = pane.querySelector("[data-log]"); log.textContent = "";
            head.textContent = name;
            link.href = "/Analytics/Student/" + id;
            link.hidden = false;
            pane.querySelector("[data-form]").hidden = false;
            wrap.classList.add("has-open");
            history.replaceState(null, "", "?u=" + id);
            current = Chat.open(pane, id);
            listEl.querySelectorAll(".ch-thread").forEach(function (a) { a.classList.toggle("active", a.getAttribute("href") === "?u=" + id); });
            pane.querySelector("textarea").focus();
        }
        var deb = null;
        search.addEventListener("input", function () { clearTimeout(deb); deb = setTimeout(load, 250); });
        var back = pane.querySelector("[data-back]");
        if (back) back.addEventListener("click", function () { wrap.classList.remove("has-open"); });
        document.addEventListener("chat:read", function () { setTimeout(load, 300); });
        document.addEventListener("chat:sent", load);
        load();
        timer = setInterval(function () { if (document.visibilityState === "visible") load(); }, 8000);
        if (cfg.open && cfg.openName) open(cfg.open, cfg.openName, cfg.openUser);

        // Hammaga xabar
        var bform = document.querySelector("[data-broadcast]");
        if (bform) bform.addEventListener("submit", function (e) {
            e.preventDefault();
            var ta = bform.querySelector("textarea"), text = ta.value.trim();
            if (!text || !confirm(t("msg.broadcastConfirm"))) return;
            var btn = bform.querySelector("button[type=submit]"); btn.disabled = true;
            post("/Messages/Broadcast", { text: text }).then(function (r) { return r.ok ? r.json() : Promise.reject(); })
                .then(function (d) { ta.value = ""; bform.closest("details").open = false; alert(t("msg.broadcastDone", { n: d.sent })); load(); })
                .catch(function () { alert(t("msg.sendFail")); })
                .then(function () { btn.disabled = false; });
        });
    };

    // ── Menyudagi o'qilmaganlar belgisi ──
    Chat.badge = function (sel) {
        var els = document.querySelectorAll(sel);
        if (!els.length) return;
        function upd() {
            if (document.visibilityState !== "visible") return;
            fetch("/Messages/Unread", { credentials: "same-origin", cache: "no-store" })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (d) { if (!d) return; els.forEach(function (el) { el.textContent = d.count > 99 ? "99+" : d.count; el.hidden = !d.count; }); })
                .catch(function () { });
        }
        upd();
        setInterval(upd, 30000);
        document.addEventListener("chat:read", upd);
    };

    window.Chat = Chat;
})();
