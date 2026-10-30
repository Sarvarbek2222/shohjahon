/*
 * Test natijasi paneli (barcha test sahifalari, _TestHeader ulaydi). Sahifalarning o'z kodi o'zgarmaydi:
 * ular #resultCard ni ko'rsatadi va `lastResult` ni to'ldiradi — bu skript panelni markazdagi oynaga aylantiradi:
 * foiz halqasi, O'tdi/O'tmadi belgisi, statistika kartalari, imtihon to'xtash sababi, o'tganda — konfetti.
 */
(function () {
    function svg(path) {
        return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + path + "</svg>";
    }
    var ICONS = {
        reviewBtn: svg('<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>'),
        reviewErrorsBtn: svg('<path d="M12 9v4M12 17h.01"/><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/>'),
        retryBtn: svg('<path d="M3 12a9 9 0 1 0 2.64-6.36L3 8"/><path d="M3 3v5h5"/>'),
    };

    function init() {
        var card = document.getElementById("resultCard");
        if (!card || card.hasAttribute("data-rc")) return;
        card.setAttribute("data-rc", "");
        var body = card.querySelector(".card-body") || card;

        var backdrop = document.createElement("div");
        backdrop.className = "rc-backdrop";
        backdrop.hidden = true;
        document.body.appendChild(backdrop);

        var top = document.createElement("div");
        top.className = "rc-top";
        top.innerHTML =
            '<div class="rc-ring"><svg viewBox="0 0 120 120" aria-hidden="true"><circle class="rc-ring-bg" cx="60" cy="60" r="52"/>' +
            '<circle class="rc-ring-fg" cx="60" cy="60" r="52"/></svg><div class="rc-ring-val"><b class="rc-pct">0</b><small>%</small></div></div>' +
            '<div class="rc-badge"></div><div class="rc-sub"></div><div class="rc-reason" hidden></div>';
        var stats = document.createElement("div");
        stats.className = "rc-stats";
        stats.innerHTML =
            '<div class="rc-stat ok"><b data-k="correct">0</b><span data-l="rc.correct"></span></div>' +
            '<div class="rc-stat bad"><b data-k="wrong">0</b><span data-l="rc.wrong"></span></div>' +
            '<div class="rc-stat skip"><b data-k="skipped">0</b><span data-l="rc.skipped"></span></div>';
        body.insertBefore(stats, body.firstChild);
        body.insertBefore(top, body.firstChild);

        // Tugmalar: ikonka + matn (matn alohida span'da — til almashganda ikonka o'chib ketmasin)
        Object.keys(ICONS).forEach(function (id) {
            var b = document.getElementById(id);
            if (!b || b.querySelector(".rc-btn-text")) return;
            var key = b.getAttribute("data-i18n");
            var span = document.createElement("span");
            span.className = "rc-btn-text";
            if (key) { span.setAttribute("data-i18n", key); b.removeAttribute("data-i18n"); }
            span.textContent = b.textContent.trim();
            b.textContent = "";
            b.insertAdjacentHTML("afterbegin", ICONS[id]);
            b.appendChild(span);
            b.classList.add("rc-btn", "rc-" + id);
        });

        var lastKey = "";
        function result() {
            try { return typeof lastResult !== "undefined" ? lastResult : null; } catch (e) { return null; }
        }
        function visible() { return card.style.display !== "none" && getComputedStyle(card).display !== "none"; }

        function render() {
            var r = result();
            var show = visible() && !!r;
            backdrop.hidden = !show;
            document.documentElement.classList.toggle("rc-open", show);
            if (!show) return;

            var passed = card.classList.contains("passed");
            var pct = Math.max(0, Math.min(100, Math.round(r.percent || 0)));
            card.classList.toggle("rc-pass", passed);
            card.classList.toggle("rc-fail", !passed);
            top.querySelector(".rc-pct").textContent = pct;
            top.querySelector(".rc-ring-fg").style.strokeDashoffset = String(326.7 * (1 - pct / 100));
            top.querySelector(".rc-badge").textContent = I18n.t(passed ? "rc.passed" : "rc.failed");
            top.querySelector(".rc-sub").textContent = I18n.t("rc.sub", { correct: r.correct, total: r.total });
            var reason = top.querySelector(".rc-reason");
            reason.hidden = !r.reason;
            if (r.reason) reason.textContent = I18n.t(r.reason === "time" ? "test.endTime" : "test.endMistakes");
            stats.querySelectorAll("[data-k]").forEach(function (b) { b.textContent = r[b.getAttribute("data-k")] || 0; });
            stats.querySelectorAll("[data-l]").forEach(function (s) { s.textContent = I18n.t(s.getAttribute("data-l")); });
            var errBtn = document.getElementById("reviewErrorsBtn");
            var row = errBtn ? errBtn.parentElement : null;
            if (row) row.classList.toggle("rc-one", errBtn.style.display === "none");

            var key = JSON.stringify(r);
            if (key !== lastKey) {
                lastKey = key;
                card.classList.remove("rc-in");
                void card.offsetWidth;
                card.classList.add("rc-in");
                if (passed) confetti();
                var first = card.querySelector(".rc-btn");
                if (first) setTimeout(function () { try { first.focus({ preventScroll: true }); } catch (e) { } }, 50);
            }
        }

        function confetti() {
            if (window.matchMedia && matchMedia("(prefers-reduced-motion: reduce)").matches) return;
            var colors = ["#22c55e", "#38bdf8", "#facc15", "#f472b6", "#a78bfa", "#fb923c"];
            var box = document.createElement("div");
            box.className = "rc-confetti";
            for (var i = 0; i < 70; i++) {
                var p = document.createElement("i");
                p.style.left = Math.random() * 100 + "vw";
                p.style.background = colors[i % colors.length];
                p.style.animationDelay = Math.random() * 0.6 + "s";
                p.style.animationDuration = 1.8 + Math.random() * 1.6 + "s";
                p.style.setProperty("--dx", (Math.random() * 160 - 80) + "px");
                p.style.setProperty("--rot", (Math.random() * 720 - 360) + "deg");
                box.appendChild(p);
            }
            document.body.appendChild(box);
            setTimeout(function () { box.remove(); }, 4200);
        }

        new MutationObserver(render).observe(card, { attributes: true, attributeFilter: ["style", "class"] });
        document.addEventListener("i18n:change", render);
        render();
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init); else init();
})();
