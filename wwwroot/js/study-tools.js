/*
 * Test sahifalaridagi savol ostidagi tugmalar:
 *   ☆ Saqlash — savol kabinetdagi "Saqlangan savollar"ga qo'shiladi (keyin alohida ishlanadi);
 *   🎬 Video — savolga bog'langan video tushuntirish (faqat mavzu va bilet testlarida), sahifadan chiqmasdan oynada.
 * Test sahifalarining o'zi o'zgartirilmagan: #questionText o'zgarganda joriy savol (questions[currentIndex]) o'qiladi.
 */
(function () {
    "use strict";
    var cfg = window.STUDY_TOOLS || {};
    function t(k, a) { return window.I18n ? I18n.t(k, a) : k; }
    function token() { var m = document.querySelector('meta[name="csrf"]'); return m ? m.content : ""; }
    function current() {
        try { if (typeof questions !== "undefined" && typeof currentIndex !== "undefined" && questions) return questions[currentIndex] || null; }
        catch (e) { }
        return null;
    }
    function fmt(s) { s = Math.max(0, s | 0); return Math.floor(s / 60) + ":" + ("0" + s % 60).slice(-2); }

    var css = document.createElement("style");
    css.textContent =
        ".st-bar{display:flex;flex-wrap:wrap;gap:8px;margin:8px 8px 0}" +
        ".st-btn{display:inline-flex;align-items:center;gap:6px;padding:6px 14px;border-radius:999px;border:1px solid rgba(255,255,255,.22);background:rgba(255,255,255,.06);color:inherit;font:600 13px/1.2 system-ui,sans-serif;cursor:pointer;transition:background .15s,border-color .15s,transform .1s}" +
        ".st-btn:hover{background:rgba(255,255,255,.12)}.st-btn:active{transform:scale(.96)}" +
        ".st-btn .st-i{font-size:15px;line-height:1;transition:transform .2s}" +
        ".st-btn.on{border-color:rgba(245,179,1,.7);background:rgba(245,179,1,.16)}.st-btn.on .st-i{transform:scale(1.15)}" +
        ".st-btn.vid{border-color:rgba(255,45,74,.55);background:rgba(255,45,74,.14)}" +
        ".st-modal{position:fixed;inset:0;z-index:7000;background:rgba(0,0,0,.78);display:flex;align-items:center;justify-content:center;padding:16px;backdrop-filter:blur(4px)}" +
        ".st-box{width:min(1100px,100%);height:min(86vh,760px);background:#0b0d12;border-radius:16px;overflow:hidden;display:flex;flex-direction:column;box-shadow:0 30px 80px rgba(0,0,0,.6);border:1px solid rgba(255,255,255,.1)}" +
        ".st-head{display:flex;align-items:center;gap:8px;padding:10px 12px;border-bottom:1px solid rgba(255,255,255,.1);color:#eceef4;font:600 14px system-ui,sans-serif}" +
        ".st-head b{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}" +
        ".st-head select{max-width:45%;background:#181b25;color:#eceef4;border:1px solid rgba(255,255,255,.18);border-radius:8px;padding:4px 8px}" +
        ".st-x{border:0;background:rgba(255,255,255,.1);color:#fff;width:34px;height:34px;border-radius:50%;font-size:18px;cursor:pointer}" +
        ".st-box iframe{flex:1;border:0;width:100%;background:#000}" +
        ".st-toast{position:fixed;left:50%;bottom:20px;transform:translateX(-50%);z-index:7100;padding:9px 16px;border-radius:10px;background:#16a34a;color:#fff;font:600 14px system-ui,sans-serif;box-shadow:0 8px 24px rgba(0,0,0,.35)}";
    document.head.appendChild(css);

    var cache = {}, bar = null, saveBtn = null, vidBtn = null, shownId = null;

    function toast(text, bad) {
        var el = document.createElement("div");
        el.className = "st-toast"; el.textContent = text;
        if (bad) el.style.background = "#b91c1c";
        document.body.appendChild(el);
        setTimeout(function () { el.remove(); }, 2200);
    }

    function ensureBar() {
        var qt = document.getElementById("questionText");
        if (!qt) return null;
        if (bar && bar.isConnected) return bar;
        bar = document.createElement("div");
        bar.className = "st-bar";
        saveBtn = document.createElement("button");
        saveBtn.type = "button"; saveBtn.className = "st-btn";
        saveBtn.innerHTML = '<span class="st-i">☆</span><span class="st-l"></span>';
        saveBtn.addEventListener("click", toggleSave);
        vidBtn = document.createElement("button");
        vidBtn.type = "button"; vidBtn.className = "st-btn vid"; vidBtn.hidden = true;
        vidBtn.innerHTML = '<span class="st-i">🎬</span><span class="st-l"></span>';
        vidBtn.addEventListener("click", openVideo);
        bar.appendChild(saveBtn); bar.appendChild(vidBtn);
        qt.insertAdjacentElement("afterend", bar);
        return bar;
    }

    function paint(id) {
        var info = cache[id];
        if (!ensureBar()) return;
        saveBtn.disabled = !info;
        var on = !!(info && info.saved);
        saveBtn.classList.toggle("on", on);
        saveBtn.querySelector(".st-i").textContent = on ? "★" : "☆";
        saveBtn.querySelector(".st-l").textContent = t(on ? "sv.saved" : "sv.save");
        var vids = cfg.video && info ? info.videos : [];
        vidBtn.hidden = !vids.length;
        if (vids.length) vidBtn.querySelector(".st-l").textContent = t("sv.video") + (vids[0].t ? " · " + fmt(vids[0].t) : "");
    }

    function refresh() {
        var q = current();
        if (!q || !q.id) return;
        var id = q.id;
        if (id === shownId && cache[id]) return;
        shownId = id;
        paint(id);
        if (cache[id]) return;
        fetch("/Study/Info/" + id, { credentials: "same-origin", cache: "no-store" })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (d) { if (d) { cache[id] = d; if (shownId === id) paint(id); } })
            .catch(function () { });
    }

    function toggleSave() {
        var q = current();
        if (!q || !cache[q.id]) return;
        var id = q.id, on = !cache[id].saved;
        saveBtn.disabled = true;
        fetch("/Study/Save", {
            method: "POST", credentials: "same-origin",
            headers: { "RequestVerificationToken": token(), "Content-Type": "application/x-www-form-urlencoded" },
            body: "id=" + id + "&on=" + on,
        }).then(function (r) {
            if (r.status === 409) throw new Error("limit");
            return r.ok ? r.json() : Promise.reject();
        }).then(function () {
            cache[id].saved = on;
            toast(t(on ? "sv.added" : "sv.removed"));
        }).catch(function (e) { toast(t(e && e.message === "limit" ? "sv.limit" : "sv.fail"), true); })
            .then(function () { paint(id); });
    }

    function openVideo() {
        var q = current();
        var vids = q && cache[q.id] ? cache[q.id].videos : [];
        if (!vids.length) return;
        var m = document.createElement("div");
        m.className = "st-modal"; m.setAttribute("role", "dialog"); m.setAttribute("aria-modal", "true");
        m.innerHTML = '<div class="st-box"><div class="st-head"><span>🎬</span><b></b></div><iframe allow="autoplay; fullscreen; encrypted-media" allowfullscreen referrerpolicy="same-origin"></iframe></div>';
        var head = m.querySelector(".st-head"), frame = m.querySelector("iframe");
        function load(v) {
            m.querySelector("b").textContent = v.title;
            frame.src = "/Topic/Watch/" + v.id + "?embed=true" + (v.t ? "&t=" + v.t : "") + "&q=" + q.id;
        }
        if (vids.length > 1) {
            var sel = document.createElement("select");
            vids.forEach(function (v, i) { var o = document.createElement("option"); o.value = i; o.textContent = v.title; sel.appendChild(o); });
            sel.addEventListener("change", function () { load(vids[+sel.value]); });
            head.appendChild(sel);
        }
        var x = document.createElement("button");
        x.type = "button"; x.className = "st-x"; x.textContent = "✕"; x.setAttribute("aria-label", t("sv.close"));
        head.appendChild(x);
        function close() { frame.src = "about:blank"; m.remove(); document.removeEventListener("keydown", esc); }
        function esc(e) { if (e.key === "Escape") close(); }
        x.addEventListener("click", close);
        m.addEventListener("click", function (e) { if (e.target === m) close(); });
        document.addEventListener("keydown", esc);
        document.body.appendChild(m);
        load(vids[0]);
    }

    function start() {
        var qt = document.getElementById("questionText");
        if (!qt) return;
        new MutationObserver(function () { setTimeout(refresh, 0); }).observe(qt, { childList: true, characterData: true, subtree: true });
        document.addEventListener("i18n:change", function () { if (shownId) paint(shownId); });
        refresh();
    }
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
