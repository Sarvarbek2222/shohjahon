/*
 * Jonli darslar (o'quvchi xonasi va admin boshqaruv xonasi). SignalR: /hubs/live.
 *   LiveUi.times()               — [data-utc] vaqtlarni foydalanuvchining mahalliy vaqtida ko'rsatadi
 *   LiveUi.countdowns()          — [data-countdown] ortga sanash
 *   LiveUi.watch(cfg)            — o'quvchi: holat, tomoshabinlar, savol yuborish, reaksiyalar
 *   LiveUi.control(cfg)          — admin: kim kirdi/chiqdi (jonli), savollar (kimdan), yoqish/o'chirish, mahkamlash
 */
(function () {
    function t(k, a) { return I18n.t(k, a); }
    function esc(s) { var d = document.createElement("div"); d.textContent = s == null ? "" : s; return d.innerHTML; }
    function pad(n) { return (n < 10 ? "0" : "") + n; }
    function localDate(iso) { return new Date(iso); }
    function fmtTime(d) { return pad(d.getHours()) + ":" + pad(d.getMinutes()); }
    function fmtDate(d) { return pad(d.getDate()) + "." + pad(d.getMonth() + 1) + "." + d.getFullYear(); }
    function fmtFull(d) { return fmtDate(d) + " " + fmtTime(d); }

    var LiveUi = {};

    LiveUi.times = function (root) {
        (root || document).querySelectorAll("[data-utc]").forEach(function (el) {
            var d = localDate(el.getAttribute("data-utc"));
            if (isNaN(d)) return;
            var f = el.getAttribute("data-fmt");
            el.textContent = f === "time" ? fmtTime(d) : f === "date" ? fmtDate(d) : fmtFull(d);
        });
    };

    LiveUi.countdowns = function () {
        var els = document.querySelectorAll("[data-countdown]");
        if (!els.length) return;
        function tick() {
            els.forEach(function (el) {
                var ms = new Date(el.getAttribute("data-countdown")) - Date.now();
                if (ms <= 0) { el.innerHTML = '<b class="lv-cd-now">' + esc(t("lv.anyMoment")) + "</b>"; return; }
                var s = Math.floor(ms / 1000), d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60), sec = s % 60;
                var parts = (d ? [[d, "lv.cdD"]] : []).concat([[h, "lv.cdH"], [m, "lv.cdM"], [sec, "lv.cdS"]]);
                el.innerHTML = parts.map(function (p) { return '<span class="lv-cd-cell"><b>' + pad(p[0]) + "</b><small>" + esc(t(p[1])) + "</small></span>"; }).join("");
            });
        }
        tick();
        setInterval(tick, 1000);
    };

    function connect(lessonId, onReconnect) {
        var conn = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/live")
            .withAutomaticReconnect([0, 2000, 5000, 10000, 20000, 30000])
            .build();
        var badge = document.querySelector("[data-conn]");
        function setConn(state) { if (badge) { badge.setAttribute("data-conn", state); badge.title = t("lv.conn_" + state); } }
        conn.onreconnecting(function () { setConn("reconnecting"); });
        conn.onreconnected(function () { setConn("on"); if (onReconnect) onReconnect(); });
        conn.onclose(function () { setConn("off"); });
        conn.ready = conn.start().then(function () { setConn("on"); }).catch(function () { setConn("off"); });
        return conn;
    }

    // ═════════ O'quvchi xonasi ═════════
    LiveUi.watch = function (cfg) {
        var status = cfg.status, enabled = true;
        var $ = function (s) { return document.querySelector(s); };
        var conn = connect(cfg.lessonId, join);

        var video = $("#lvVideo"), connecting = $("#lvConnecting"), unmute = $("#lvUnmute"), latency = $("#lvLatency");
        var player = new StreamPlayer(video, cfg.lessonId, function (state, lag) {
            connecting.hidden = state === "playing" || state === "needplay";
            $("#lvUnsupported").hidden = state !== "unsupported";
            if (state === "unsupported") connecting.hidden = true;
            if (state === "playing" || state === "needplay") unmute.hidden = !video.muted;
            if (state === "playing" && lag != null) { latency.hidden = false; latency.textContent = "⏱ " + Math.max(1, Math.round(lag + 2)) + "s"; }
        });
        if (cfg.watermark && window.LessonPlayer) LessonPlayer.watermark($("#lvStage"), cfg.watermark);
        unmute.addEventListener("click", function () { video.muted = false; video.play().catch(function () { }); unmute.hidden = true; });
        $("#lvFull").addEventListener("click", function () {
            var st = $("#lvStage");
            if (document.fullscreenElement) document.exitFullscreen(); else if (st.requestFullscreen) st.requestFullscreen().catch(function () { });
        });
        video.addEventListener("contextmenu", function (e) { e.preventDefault(); });

        function setStatus(s) {
            status = s;
            document.documentElement.setAttribute("data-live-status", s);
            if (s === "live") player.start(); else player.stop();
            renderAsk();
        }

        function renderAsk() {
            var box = $("#lvAsk"), closed = $("#lvAskClosed"), wait = $("#lvAskWait");
            if (!box) return;
            box.hidden = !(status === "live" && enabled);
            closed.hidden = !(status === "live" && !enabled);
            wait.hidden = status === "live";
        }

        function renderMine(list) {
            var ul = $("#lvMine");
            ul.textContent = "";
            $("#lvMineWrap").hidden = !list.length;
            list.forEach(function (q) {
                var li = document.createElement("li");
                li.className = "lv-my lv-st-" + q.status;
                li.dataset.id = q.id;
                li.innerHTML = '<span class="lv-my-text">' + esc(q.text) + '</span><span class="lv-my-st">' + esc(t("lv.qs_" + q.status)) + "</span>";
                ul.appendChild(li);
            });
        }
        var mine = [];

        function showPinned(p) {
            var el = $("#lvPinned");
            el.hidden = !p;
            if (p) { el.querySelector("[data-pin-text]").textContent = p.text; el.querySelector("[data-pin-name]").textContent = p.name; }
        }

        function join() {
            conn.invoke("Join", cfg.lessonId).then(function (s) {
                enabled = s.questionsEnabled;
                setStatus(s.status);
                $("#lvViewers").textContent = s.viewers;
                mine = s.mine || [];
                renderMine(mine);
                showPinned(s.pinned);
            }).catch(function () { });
        }
        conn.ready.then(join);

        conn.on("viewers", function (n) { $("#lvViewers").textContent = n; });
        conn.on("status", function (s) {
            setStatus(s);
            if (s === "ended") setTimeout(function () { location.reload(); }, 1500);
        });
        conn.on("questionsToggled", function (on) {
            enabled = on;
            renderAsk();
            toast(t(on ? "lv.qOpened" : "lv.qClosed"));
        });
        conn.on("myQuestionUpdated", function (u) {
            mine.forEach(function (q) { if (q.id === u.id) q.status = u.status; });
            renderMine(mine);
            if (u.status === "answered") toast(t("lv.yourAnswered"));
        });
        conn.on("pinned", showPinned);
        conn.on("reaction", function (e) { floatEmoji(e); });
        conn.on("broadcast", function (b) {
            if (b.on) { player.stop(); setTimeout(function () { player.start(); }, 300); }
            else { player.stop(); connecting.hidden = false; }
        });
        // Efir hali ulanmagan bo'lsa — vaqti-vaqti bilan tekshirib turiladi
        setInterval(function () { if (status === "live" && !connecting.hidden) player.start(); }, 5000);

        // Savol yuborish
        var form = $("#lvAskForm"), input = $("#lvAskInput"), counter = $("#lvAskCount"), err = $("#lvAskErr");
        if (form) {
            input.addEventListener("input", function () { counter.textContent = input.value.length + "/500"; });
            form.addEventListener("submit", function (e) {
                e.preventDefault();
                var text = input.value.trim();
                if (text.length < 2) return;
                var btn = form.querySelector("button[type=submit]");
                btn.disabled = true;
                conn.invoke("Ask", cfg.lessonId, text).then(function (r) {
                    btn.disabled = false;
                    if (r.ok) {
                        input.value = ""; counter.textContent = "0/500"; err.hidden = true;
                        mine.unshift(r.question); renderMine(mine);
                        toast(t("lv.sent"));
                    } else {
                        err.hidden = false;
                        err.textContent = t("lv.err_" + r.error, { n: r.seconds || 0 });
                        if (r.error === "closed") { enabled = false; renderAsk(); }
                    }
                }).catch(function () { btn.disabled = false; err.hidden = false; err.textContent = t("lv.err_net"); });
            });
            input.addEventListener("keydown", function (e) {
                if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) form.requestSubmit();
            });
        }

        // Reaksiyalar
        document.querySelectorAll("[data-react]").forEach(function (b) {
            b.addEventListener("click", function () {
                if (status !== "live") return;
                conn.invoke("React", cfg.lessonId, b.getAttribute("data-react")).catch(function () { });
                b.classList.remove("pop"); void b.offsetWidth; b.classList.add("pop");
            });
        });

        setStatus(status);
    };

    function floatEmoji(e) {
        var stage = document.querySelector("#lvFloat");
        if (!stage) return;
        var s = document.createElement("span");
        s.className = "lv-float";
        s.textContent = e;
        s.style.left = (10 + Math.random() * 80) + "%";
        s.style.setProperty("--dx", (Math.random() * 60 - 30) + "px");
        stage.appendChild(s);
        setTimeout(function () { s.remove(); }, 2600);
    }

    function toast(text) {
        var el = document.createElement("div");
        el.className = "lv-toast";
        el.textContent = text;
        document.body.appendChild(el);
        setTimeout(function () { el.classList.add("out"); }, 2600);
        setTimeout(function () { el.remove(); }, 3100);
    }

    // ═════════ Admin boshqaruv xonasi ═════════
    LiveUi.control = function (cfg) {
        var $ = function (s) { return document.querySelector(s); };
        var people = {}, questions = [], filter = "new", reactions = 0, pinnedId = null;
        var conn = connect(cfg.lessonId, join);

        function renderPeople() {
            var list = $("#lcPeople"), ids = Object.keys(people);
            $("#lcNow").textContent = ids.length;
            $("#lcPeopleCount").textContent = ids.length;
            list.textContent = "";
            if (!ids.length) { list.innerHTML = '<div class="lc-empty">' + esc(t("lv.nobody")) + "</div>"; return; }
            ids.map(function (id) { return people[id]; }).sort(function (a, b) { return new Date(a.at) - new Date(b.at); }).forEach(function (p) {
                var d = document.createElement("div");
                d.className = "lc-person";
                var init = (p.name || "?").split(/\s+/).slice(0, 2).map(function (w) { return w.charAt(0).toUpperCase(); }).join("");
                d.innerHTML = '<span class="lc-av">' + esc(init) + '<i class="pres-dot on"></i></span><span class="lc-pn">' + esc(p.name) +
                    '</span><span class="lc-pt">' + esc(fmtTime(new Date(p.at))) + "</span>";
                list.appendChild(d);
            });
        }

        function log(kind, name, at) {
            var feed = $("#lcFeed");
            var empty = feed.querySelector(".lc-empty");
            if (empty) empty.remove();
            var d = document.createElement("div");
            d.className = "lc-ev lc-ev-" + kind;
            d.innerHTML = '<span class="lc-ev-dot"></span><span class="lc-ev-t">' + esc(t(kind === "in" ? "lv.evIn" : "lv.evOut", { name: name })) +
                '</span><span class="lc-ev-time">' + esc(fmtTime(new Date(at))) + "</span>";
            feed.insertBefore(d, feed.firstChild);
            while (feed.children.length > 200) feed.lastChild.remove();
        }

        function renderQuestions() {
            var list = $("#lcQuestions");
            var counts = { new: 0, answered: 0, hidden: 0 };
            questions.forEach(function (q) { counts[q.status] = (counts[q.status] || 0) + 1; });
            document.querySelectorAll("[data-qcount]").forEach(function (el) {
                var k = el.getAttribute("data-qcount");
                el.textContent = k === "all" ? questions.length : (counts[k] || 0);
            });
            $("#lcQTotal").textContent = questions.length;
            var shown = questions.filter(function (q) { return filter === "all" || q.status === filter; });
            list.textContent = "";
            if (!shown.length) { list.innerHTML = '<div class="lc-empty">' + esc(t("lv.noQuestions")) + "</div>"; return; }
            shown.forEach(function (q) {
                var d = document.createElement("div");
                d.className = "lc-q lc-q-" + q.status + (q.id === pinnedId ? " pinned" : "");
                d.innerHTML =
                    '<div class="lc-q-top"><b>' + esc(q.name) + '</b><span class="lc-q-user">@' + esc(q.username) + '</span><span class="lc-q-time">' + esc(fmtTime(new Date(q.at))) + "</span></div>" +
                    '<div class="lc-q-text">' + esc(q.text) + "</div>" +
                    '<div class="lc-q-act">' +
                    '<button type="button" data-a="pin" class="' + (q.id === pinnedId ? "on" : "") + '">📌 ' + esc(t(q.id === pinnedId ? "lv.unpin" : "lv.pin")) + "</button>" +
                    (q.status !== "answered" ? '<button type="button" data-a="answered">✓ ' + esc(t("lv.markAnswered")) + "</button>" : '<button type="button" data-a="new">↺ ' + esc(t("lv.markNew")) + "</button>") +
                    (q.status !== "hidden" ? '<button type="button" data-a="hidden">✕ ' + esc(t("lv.hide")) + "</button>" : "") +
                    "</div>";
                d.querySelectorAll("[data-a]").forEach(function (b) {
                    b.onclick = function () {
                        var a = b.getAttribute("data-a");
                        if (a === "pin") conn.invoke("PinQuestion", q.id, q.id !== pinnedId);
                        else conn.invoke("MarkQuestion", q.id, a);
                    };
                });
                list.appendChild(d);
            });
        }

        function join() {
            conn.invoke("Join", cfg.lessonId).then(function (s) {
                people = {};
                (s.people || []).forEach(function (p) { people[p.userId] = { name: p.name, at: p.at }; });
                questions = s.questions || [];
                pinnedId = s.pinned ? s.pinned.id : null;
                reactions = s.reactions || 0;
                $("#lcReactions").textContent = reactions;
                $("#lcToggle").checked = s.questionsEnabled;
                renderPeople();
                renderQuestions();
            }).catch(function () { });
        }
        conn.ready.then(join);

        conn.on("viewerJoined", function (p) { people[p.userId] = { name: p.name, at: p.at }; renderPeople(); log("in", p.name, p.at); peak(); });
        conn.on("viewerLeft", function (p) { delete people[p.userId]; renderPeople(); log("out", p.name, p.at); });
        conn.on("questionNew", function (q) {
            questions.unshift(q);
            renderQuestions();
            var b = $("#lcQBell"); b.classList.remove("ring"); void b.offsetWidth; b.classList.add("ring");
        });
        conn.on("questionUpdated", function (u) {
            questions.forEach(function (q) { if (q.id === u.id) { q.status = u.status; if (!u.pinned && pinnedId === u.id) pinnedId = null; } });
            renderQuestions();
        });
        conn.on("questionPinnedAdmin", function (id) { pinnedId = id; renderQuestions(); });
        conn.on("reaction", function (e) { reactions++; $("#lcReactions").textContent = reactions; floatEmoji(e); });
        conn.on("status", function (s) { if (s !== cfg.status) location.reload(); });

        // Efir paneli
        var token = (document.querySelector('input[name="__RequestVerificationToken"]') || {}).value || "";
        var preview = $("#bcPreview"), empty = $("#bcEmpty"), stateEl = $("#bcState"), stateText = $("#bcStateText");
        var bc = new Broadcaster(cfg.lessonId, token, {
            preview: function (s) { preview.srcObject = s; empty.hidden = !!s; },
            state: function (st) {
                stateEl.setAttribute("data-state", st);
                stateText.textContent = t(st === "on" ? "lv.bcOn" : st === "error" ? "lv.bcError" : st === "ended" ? "lv.stEnded" : "lv.bcOff");
                $("#bcStop").disabled = st !== "on";
                $("#bcMic").disabled = st !== "on";
                $("#bcStats").hidden = st !== "on";
            },
            stats: function (kbps, n) { $("#bcRate").textContent = kbps; $("#bcSent").textContent = n; },
            warn: function (w) { $("#bcWarn").hidden = !w; },
            onSourceEnded: function () { bc.stop(); },
        });
        if (!bc.supported) { stateText.textContent = t("lv.bcUnsupported"); document.querySelectorAll("[data-src]").forEach(function (b) { b.disabled = true; }); }
        document.querySelectorAll("[data-src]").forEach(function (b) {
            b.addEventListener("click", function () {
                document.querySelectorAll("[data-src]").forEach(function (x) { x.classList.toggle("on", x === b); });
                bc.useSource(b.getAttribute("data-src")).catch(function (e) {
                    stateText.textContent = t(e && e.name === "NotAllowedError" ? "lv.bcDenied" : "lv.bcError");
                    b.classList.remove("on");
                });
            });
        });
        $("#bcQuality").addEventListener("change", function (e) { bc.setQuality(+e.target.value); });
        $("#bcMic").addEventListener("click", function () { var en = bc.toggleMic(); $("#bcMic").classList.toggle("muted", !en); });
        $("#bcStop").addEventListener("click", function () { bc.stop(); document.querySelectorAll("[data-src]").forEach(function (x) { x.classList.remove("on"); }); });
        window.addEventListener("beforeunload", function (e) { if (stateEl.getAttribute("data-state") === "on") { e.preventDefault(); e.returnValue = ""; } });

        var peakVal = cfg.peak || 0;
        function peak() { var n = Object.keys(people).length; if (n > peakVal) { peakVal = n; $("#lcPeak").textContent = n; } }

        document.querySelectorAll("[data-qfilter]").forEach(function (b) {
            b.addEventListener("click", function () {
                filter = b.getAttribute("data-qfilter");
                document.querySelectorAll("[data-qfilter]").forEach(function (x) { x.classList.toggle("on", x === b); });
                renderQuestions();
            });
        });
        $("#lcToggle").addEventListener("change", function (e) {
            conn.invoke("SetQuestions", cfg.lessonId, e.target.checked).catch(function () { e.target.checked = !e.target.checked; });
        });

        // Efir davomiyligi
        if (cfg.startedAt) {
            var el = $("#lcElapsed");
            setInterval(function () {
                var s = Math.max(0, Math.floor((Date.now() - new Date(cfg.startedAt)) / 1000));
                el.textContent = (s >= 3600 ? Math.floor(s / 3600) + ":" : "") + pad(Math.floor(s % 3600 / 60)) + ":" + pad(s % 60);
            }, 1000);
        }
    };


    // ═════════ Efir pleeri (o'quvchi): 2 soniyalik WebM bo'laklar → Media Source, jonli chegarada ushlab turiladi ═════════
    function StreamPlayer(video, lessonId, onState) {
        var MS = window.ManagedMediaSource || window.MediaSource;
        var self = this, ms = null, sb = null, next = -1, running = false, mseMime = null, queue = [];
        this.supported = !!MS;

        function mseType(m) {
            var c = /codecs=([^;]+)/.exec(m || "");
            return c ? 'video/webm; codecs="' + c[1].replace(/,/g, ", ") + '"' : "video/webm";
        }
        function waitIdle() {
            return new Promise(function (res) {
                if (!sb || !sb.updating) return res();
                sb.addEventListener("updateend", function h() { sb.removeEventListener("updateend", h); res(); });
            });
        }
        async function append(buf) {
            await waitIdle();
            try { sb.appendBuffer(buf); }
            catch (e) {
                if (e.name === "QuotaExceededError" && video.currentTime > 10) {
                    sb.remove(0, video.currentTime - 5); await waitIdle(); sb.appendBuffer(buf);
                } else throw e;
            }
            await waitIdle();
        }
        function bufferedEnd() { var b = video.buffered; return b.length ? b.end(b.length - 1) : 0; }

        function open(mime) {
            return new Promise(function (resolve, reject) {
                mseMime = mseType(mime);
                if (!MS.isTypeSupported(mseMime)) return reject(new Error("unsupported"));
                ms = new MS();
                if (window.ManagedMediaSource && MS === window.ManagedMediaSource) video.disableRemotePlayback = true;
                video.src = URL.createObjectURL(ms);
                ms.addEventListener("sourceopen", function () {
                    sb = ms.addSourceBuffer(mseMime);
                    sb.mode = "sequence";
                    resolve();
                }, { once: true });
            });
        }

        this.start = async function () {
            if (running) return;
            running = true;
            try {
                var st = await (await fetch("/Live/StreamState/" + lessonId, { credentials: "same-origin", cache: "no-store" })).json();
                if (!st.active) { running = false; onState("waiting"); return; }
                if (!ms) await open(st.mime);
                next = Math.max(0, st.last - 1);
                onState("buffering");
                loop();
            } catch (e) {
                running = false;
                onState(e.message === "unsupported" ? "unsupported" : "waiting");
            }
        };
        this.stop = function () { running = false; };

        async function loop() {
            var misses = 0;
            while (running) {
                var r;
                try { r = await fetch("/Live/Seg/" + lessonId + "?seq=" + next, { credentials: "same-origin", cache: "no-store" }); }
                catch (e) { await sleep(1500); continue; }
                if (r.ok) {
                    misses = 0;
                    try { await append(await r.arrayBuffer()); } catch (e) { await sleep(500); }
                    next++;
                    // Jonli chegaradan uzoqlashsa — oldinga sakrash (kechikish ~3–5 s)
                    var end = bufferedEnd();
                    if (end - video.currentTime > 5) video.currentTime = end - 1.5;
                    if (video.currentTime > 90 && !sb.updating) { try { sb.remove(0, video.currentTime - 45); } catch (e) { } }
                    if (video.paused) video.play().catch(function () { onState("needplay"); });
                    onState("playing", end - video.currentTime);
                } else {
                    misses++;
                    var st = null;
                    try { st = await (await fetch("/Live/StreamState/" + lessonId, { credentials: "same-origin", cache: "no-store" })).json(); } catch (e) { }
                    if (st && !st.active) { running = false; onState("waiting"); return; }
                    // Orqada qolgan bo'lsa (bo'lak xotiradan chiqqan) — eng oxirgisiga o'tish
                    if (st && st.last > next + 2) next = st.last - 1;
                    await sleep(misses > 3 ? 1500 : 400);
                }
            }
        }
        function sleep(ms2) { return new Promise(function (r) { setTimeout(r, ms2); }); }
    }

    // ═════════ Efir uzatuvchi (admin): kamera/ekran + mikrofon → 2 soniyalik WebM bo'laklar → server ═════════
    function Broadcaster(lessonId, token, ui) {
        var stream = null, mic = null, screen = null, audioCtx = null, on = false, rec = null, timer = null;
        var mime = ["video/webm;codecs=vp8,opus", "video/webm;codecs=vp9,opus", "video/webm"].filter(function (m) {
            return window.MediaRecorder && MediaRecorder.isTypeSupported(m);
        })[0];
        var queue = [], sending = false, sent = 0, bytesWindow = [], quality = 720;
        this.supported = !!mime && !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);

        function bitrate() { return quality >= 1080 ? 2500000 : quality >= 720 ? 1200000 : 700000; }
        function stopTracks(s) { if (s) s.getTracks().forEach(function (t) { t.stop(); }); }

        async function getMic() {
            if (mic) return mic;
            mic = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
            return mic;
        }

        this.useSource = async function (kind) {
            var video, audio;
            if (kind === "camera") {
                var cam = await navigator.mediaDevices.getUserMedia({
                    video: { width: { ideal: quality * 16 / 9 }, height: { ideal: quality }, frameRate: { ideal: 25, max: 30 } },
                    audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
                });
                video = cam.getVideoTracks()[0];
                audio = cam.getAudioTracks()[0];
                stopTracks(screen); screen = cam;
            } else {
                var disp = await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: { ideal: 15, max: 30 }, height: { ideal: quality } }, audio: true });
                var m = await getMic().catch(function () { return null; });
                video = disp.getVideoTracks()[0];
                // Ekran ovozi + mikrofon — bitta audio yo'lakka aralashtiriladi
                audioCtx = audioCtx || new AudioContext();
                var dest = audioCtx.createMediaStreamDestination();
                if (disp.getAudioTracks().length) audioCtx.createMediaStreamSource(new MediaStream(disp.getAudioTracks())).connect(dest);
                if (m) audioCtx.createMediaStreamSource(m).connect(dest);
                audio = dest.stream.getAudioTracks()[0];
                stopTracks(screen); screen = disp;
                video.addEventListener("ended", function () { ui.onSourceEnded(); });
            }
            video.contentHint = kind === "screen" ? "detail" : "motion";
            stream = new MediaStream([video].concat(audio ? [audio] : []));
            ui.preview(stream);
            if (!on) { on = true; segment(); }
            ui.state("on");
        };

        this.setQuality = function (q) { quality = q; };
        this.toggleMic = function () {
            if (!stream) return true;
            var a = stream.getAudioTracks()[0];
            if (mic) mic.getAudioTracks().forEach(function (t) { t.enabled = !t.enabled; });
            if (a) a.enabled = !a.enabled;
            return a ? a.enabled : true;
        };

        function segment() {
            if (!on || !stream) return;
            var parts = [];
            try { rec = new MediaRecorder(stream, { mimeType: mime, videoBitsPerSecond: bitrate(), audioBitsPerSecond: 64000 }); }
            catch (e) { ui.state("error"); return; }
            rec.ondataavailable = function (e) { if (e.data && e.data.size) parts.push(e.data); };
            rec.onstop = function () {
                if (parts.length) enqueue(new Blob(parts, { type: mime }));
                segment(); // keyingi bo'lak darhol boshlanadi (har biri mustaqil — kalit kadr bilan)
            };
            rec.start();
            timer = setTimeout(function () { if (rec && rec.state !== "inactive") rec.stop(); }, 2000);
        }

        function enqueue(blob) {
            queue.push(blob);
            // Internet sekin bo'lsa — eski bo'laklar tashlab yuboriladi (kechikish oshib ketmasin)
            if (queue.length > 6) { queue.splice(0, queue.length - 3); ui.warn(true); }
            send();
        }
        async function send() {
            if (sending) return;
            sending = true;
            while (queue.length) {
                var blob = queue.shift();
                try {
                    var r = await fetch("/Live/Push/" + lessonId + "?mime=" + encodeURIComponent(mime), {
                        method: "POST", credentials: "same-origin", body: blob,
                        headers: { "RequestVerificationToken": token, "Content-Type": "application/octet-stream" },
                    });
                    if (r.status === 409) { ui.state("ended"); break; }
                    if (r.ok) {
                        sent++;
                        var now = Date.now();
                        bytesWindow.push([now, blob.size]);
                        bytesWindow = bytesWindow.filter(function (x) { return now - x[0] < 10000; });
                        var bytes = bytesWindow.reduce(function (a, x) { return a + x[1]; }, 0);
                        ui.stats(Math.round(bytes * 8 / 10 / 1000), sent);
                        if (queue.length < 2) ui.warn(false);
                    }
                } catch (e) { ui.warn(true); }
            }
            sending = false;
        }

        this.stop = function () {
            on = false;
            clearTimeout(timer);
            if (rec && rec.state !== "inactive") { rec.onstop = null; rec.stop(); }
            stopTracks(screen); stopTracks(mic); screen = mic = stream = null;
            ui.preview(null);
            ui.state("off");
            fetch("/Live/PushStop/" + lessonId, { method: "POST", credentials: "same-origin", headers: { "RequestVerificationToken": token } }).catch(function () { });
        };
    }

    window.LiveUi = LiveUi;
    document.addEventListener("DOMContentLoaded", function () { LiveUi.times(); LiveUi.countdowns(); });
})();
