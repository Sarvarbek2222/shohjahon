/*
 * AVTOPRAVA_SAM video pleeri (video darslar). Yuklangan fayl (HTML5 <video>) va YouTube (IFrame API, o'z boshqaruvi bilan)
 * bir xil interfeysda ishlaydi.
 *
 *   var p = new LessonPlayer(el, {
 *       kind: "file" | "youtube", src: "/uploads/videos/x.mp4", ytId: "dQw4w9WgXcQ",
 *       chapters: [{ start: 0, title: "Kirish" }, ...],     // vaqt bo'yicha tartiblangan
 *       startAt: 125,                                        // "davom ettirish" taklifi uchun
 *       onTick(t, duration, playing), onChapter(index), onEnded(), onReady(duration)
 *   });
 *
 * Imkoniyatlar: bo'limli vaqt chizig'i (ustiga kelganda — vaqt va bo'lim nomi), bufer, katta Play tugmasi,
 * ±10 soniya, tezlik (0.5–2×), ovoz (eslab qolinadi), PiP, kinoteatr rejimi, to'liq ekran, avtomatik yashiriladigan
 * boshqaruv, telefonda ikki marta bosish (±10 s), klaviatura: Space/K, J/L, ←/→, ↑/↓, M, F, T, C, N/P, 0–9, ?.
 */
(function () {
    var STORE = "lp.prefs";
    function prefs() { try { return JSON.parse(localStorage.getItem(STORE)) || {}; } catch (e) { return {}; } }
    function savePrefs(p) { try { localStorage.setItem(STORE, JSON.stringify(p)); } catch (e) { } }
    function t(key, args) { return window.I18n ? I18n.t(key, args) : key; }

    function fmt(sec) {
        sec = Math.max(0, Math.floor(sec || 0));
        var h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
        return (h ? h + ":" + (m < 10 ? "0" : "") : "") + m + ":" + (s < 10 ? "0" : "") + s;
    }
    window.LessonPlayerFmt = fmt;

    var I = {
        play: '<path d="M8 5.14v13.72a1 1 0 0 0 1.52.85l10.6-6.86a1 1 0 0 0 0-1.7L9.52 4.29A1 1 0 0 0 8 5.14z" fill="currentColor" stroke="none"/>',
        pause: '<rect x="6" y="4.5" width="4" height="15" rx="1.2" fill="currentColor" stroke="none"/><rect x="14" y="4.5" width="4" height="15" rx="1.2" fill="currentColor" stroke="none"/>',
        back: '<path d="M11 17 6 12l5-5"/><path d="M18 17l-5-5 5-5"/>',
        fwd: '<path d="m13 17 5-5-5-5"/><path d="m6 17 5-5-5-5"/>',
        vol: '<path d="M11 5 6 9H2v6h4l5 4V5z" fill="currentColor"/><path d="M15.5 8.5a5 5 0 0 1 0 7M19 5a10 10 0 0 1 0 14"/>',
        mute: '<path d="M11 5 6 9H2v6h4l5 4V5z" fill="currentColor"/><path d="m23 9-6 6M17 9l6 6"/>',
        prev: '<path d="M19 20 9 12l10-8v16z" fill="currentColor"/><path d="M5 19V5"/>',
        next: '<path d="m5 4 10 8-10 8V4z" fill="currentColor"/><path d="M19 5v14"/>',
        card: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M7 10h10M7 14h6"/>',
        pip: '<rect x="2" y="4" width="20" height="16" rx="2"/><rect x="12" y="11" width="8" height="6" rx="1" fill="currentColor"/>',
        theater: '<rect x="2" y="6" width="20" height="12" rx="2"/>',
        full: '<path d="M8 3H5a2 2 0 0 0-2 2v3M21 8V5a2 2 0 0 0-2-2h-3M3 16v3a2 2 0 0 0 2 2h3M16 21h3a2 2 0 0 0 2-2v-3"/>',
        exitFull: '<path d="M8 3v3a2 2 0 0 1-2 2H3M21 8h-3a2 2 0 0 1-2-2V3M3 16h3a2 2 0 0 1 2 2v3M16 21v-3a2 2 0 0 1 2-2h3"/>',
        help: '<circle cx="12" cy="12" r="10"/><path d="M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3M12 17h.01"/>',
    };
    function ic(name) {
        return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + I[name] + "</svg>";
    }
    function btn(cls, icon, key) {
        return '<button type="button" class="lp-btn ' + cls + '" data-tip-key="' + key + '" aria-label="' + t(key) + '">' + ic(icon) + "</button>";
    }

    // ── YouTube IFrame API bir marta yuklanadi ──
    var ytWaiters = null;
    function loadYouTube(cb) {
        if (window.YT && window.YT.Player) return cb();
        if (ytWaiters) return ytWaiters.push(cb);
        ytWaiters = [cb];
        var prev = window.onYouTubeIframeAPIReady;
        window.onYouTubeIframeAPIReady = function () {
            if (prev) try { prev(); } catch (e) { }
            ytWaiters.forEach(function (f) { f(); });
        };
        var s = document.createElement("script");
        s.src = "https://www.youtube.com/iframe_api";
        document.head.appendChild(s);
    }

    function LessonPlayer(el, opt) {
        var self = this;
        this.opt = opt;
        this.chapters = (opt.chapters || []).slice().sort(function (a, b) { return a.start - b.start; });
        this.chapterIndex = -1;
        this.duration = opt.duration || 0;
        var pr = prefs();

        // Konteyner — boshqaruv tugmalari pleer kengligiga moslashadi (@container)
        if (el.parentElement && !el.parentElement.classList.contains("lp-wrap")) {
            var wrap = document.createElement("div");
            wrap.className = "lp-wrap";
            el.parentElement.insertBefore(wrap, el);
            wrap.appendChild(el);
        }
        el.classList.add("lp");
        el.tabIndex = 0;
        el.innerHTML =
            '<div class="lp-stage"></div>' +
            '<div class="lp-shade"></div>' +
            '<div class="lp-spinner" hidden></div>' +
            '<button type="button" class="lp-big" aria-label="' + t("lp.play") + '">' + ic("play") + "</button>" +
            '<div class="lp-flash" aria-hidden="true"></div>' +
            '<div class="lp-tapzones" aria-hidden="true"><i data-tap="-10"></i><i data-tap="10"></i></div>' +
            '<div class="lp-card" hidden></div>' +
            '<div class="lp-resume" hidden><span class="lp-resume-text"></span>' +
            '<button type="button" class="lp-pill primary" data-resume="yes"></button><button type="button" class="lp-pill" data-resume="no"></button></div>' +
            '<div class="lp-end" hidden></div>' +
            '<div class="lp-help" hidden></div>' +
            '<div class="lp-controls">' +
            '  <div class="lp-bar" role="slider" tabindex="-1" aria-label="' + t("lp.seek") + '">' +
            '    <div class="lp-track"><div class="lp-buf"></div><div class="lp-played"></div></div><div class="lp-marks"></div>' +
            '    <div class="lp-knob"></div><div class="lp-hover" hidden><b class="lp-hover-time"></b><span class="lp-hover-title"></span></div>' +
            "  </div>" +
            '  <div class="lp-row">' +
            '    <div class="lp-group">' + btn("lp-play", "play", "lp.play") + btn("lp-back", "back", "lp.back10") + btn("lp-fwd", "fwd", "lp.fwd10") +
            '      <div class="lp-vol">' + btn("lp-mute", "vol", "lp.mute") + '<input type="range" class="lp-volume" min="0" max="1" step="0.05" aria-label="' + t("lp.volume") + '"></div>' +
            '      <span class="lp-time"><b class="lp-cur">0:00</b> / <span class="lp-dur">0:00</span></span>' +
            '      <span class="lp-chapter"></span>' +
            "    </div>" +
            '    <div class="lp-group">' + btn("lp-prev", "prev", "lp.prevChapter") + btn("lp-next", "next", "lp.nextChapter") +
            btn("lp-cardbtn", "card", "lp.card") +
            '      <div class="lp-speed"><button type="button" class="lp-btn lp-speed-btn" data-tip-key="lp.speed" aria-label="' + t("lp.speed") + '">1×</button><div class="lp-menu" hidden></div></div>' +
            btn("lp-pip", "pip", "lp.pip") + btn("lp-theater", "theater", "lp.theater") + btn("lp-full", "full", "lp.fullscreen") + btn("lp-helpbtn", "help", "lp.shortcuts") +
            "    </div>" +
            "  </div>" +
            "</div>";

        var $ = function (s) { return el.querySelector(s); };
        this.el = el;
        this.ui = {
            stage: $(".lp-stage"), shade: $(".lp-shade"), big: $(".lp-big"), spinner: $(".lp-spinner"), flash: $(".lp-flash"),
            card: $(".lp-card"), resume: $(".lp-resume"), end: $(".lp-end"), help: $(".lp-help"),
            bar: $(".lp-bar"), buf: $(".lp-buf"), played: $(".lp-played"), marks: $(".lp-marks"), knob: $(".lp-knob"),
            hover: $(".lp-hover"), hoverTime: $(".lp-hover-time"), hoverTitle: $(".lp-hover-title"),
            play: $(".lp-play"), back: $(".lp-back"), fwd: $(".lp-fwd"), mute: $(".lp-mute"), volume: $(".lp-volume"),
            cur: $(".lp-cur"), dur: $(".lp-dur"), chapter: $(".lp-chapter"), prev: $(".lp-prev"), next: $(".lp-next"),
            cardBtn: $(".lp-cardbtn"), speedBtn: $(".lp-speed-btn"), menu: $(".lp-menu"), pip: $(".lp-pip"),
            theater: $(".lp-theater"), full: $(".lp-full"), helpBtn: $(".lp-helpbtn"),
        };
        this.cardOn = pr.card !== false;
        this.ui.cardBtn.classList.toggle("on", this.cardOn);
        if (!this.chapters.length) { this.ui.prev.hidden = true; this.ui.next.hidden = true; }

        // Suv belgisi: o'quvchi logini va telefoni video ustida suzib yuradi (yozib olinsa — kim yozgani bilinadi)
        if (opt.watermark) LessonPlayer.watermark(el, opt.watermark);
        el.addEventListener("contextmenu", function (e) { e.preventDefault(); });

        // ── Manba adapteri ──
        if (opt.kind === "youtube") this._initYouTube(pr); else this._initFile(pr);

        this._wire(pr);
        this._renderHelp();
        this._renderSpeedMenu(pr.rate || 1);
        document.addEventListener("i18n:change", function () { self._relabel(); });
    }

    LessonPlayer.prototype._initFile = function (pr) {
        var self = this;
        var v = document.createElement("video");
        v.src = this.opt.src;
        v.preload = "metadata";
        v.playsInline = true;
        v.setAttribute("playsinline", "");
        // Yuklab olishga qarshi: brauzerning "Saqlash"/yuklab olish menyulari va sudrab olish o'chirilgan
        v.setAttribute("controlslist", "nodownload noremoteplayback noplaybackrate");
        v.disableRemotePlayback = true;
        v.setAttribute("draggable", "false");
        v.addEventListener("contextmenu", function (e) { e.preventDefault(); });
        this.ui.stage.appendChild(v);
        this.video = v;
        v.volume = pr.volume != null ? pr.volume : 1;
        v.muted = !!pr.muted;
        v.playbackRate = pr.rate || 1;
        this.api = {
            play: function () { var p = v.play(); if (p && p.catch) p.catch(function () { }); },
            pause: function () { v.pause(); },
            time: function () { return v.currentTime; },
            seek: function (s) { v.currentTime = s; },
            duration: function () { return isFinite(v.duration) ? v.duration : 0; },
            paused: function () { return v.paused; },
            setRate: function (r) { v.playbackRate = r; },
            volume: function (x) { if (x != null) v.volume = x; return v.volume; },
            muted: function (m) { if (m != null) v.muted = m; return v.muted; },
            buffered: function () { try { return v.buffered.length ? v.buffered.end(v.buffered.length - 1) : 0; } catch (e) { return 0; } },
        };
        v.addEventListener("loadedmetadata", function () { self._ready(); });
        v.addEventListener("play", function () { self._state(); });
        v.addEventListener("pause", function () { self._state(); });
        v.addEventListener("waiting", function () { self.ui.spinner.hidden = false; });
        v.addEventListener("playing", function () { self.ui.spinner.hidden = true; });
        v.addEventListener("canplay", function () { self.ui.spinner.hidden = true; });
        v.addEventListener("ended", function () { self._ended(); });
        v.addEventListener("volumechange", function () { self._volUi(); });
        if (!document.pictureInPictureEnabled) this.ui.pip.hidden = true;
    };

    LessonPlayer.prototype._initYouTube = function (pr) {
        var self = this;
        this.ui.pip.hidden = true;
        var holder = document.createElement("div");
        this.ui.stage.appendChild(holder);
        this.ui.spinner.hidden = false;
        var yt = null, ready = false;
        this.api = {
            play: function () { if (ready) yt.playVideo(); },
            pause: function () { if (ready) yt.pauseVideo(); },
            time: function () { return ready ? yt.getCurrentTime() || 0 : 0; },
            seek: function (s) { if (ready) yt.seekTo(s, true); },
            duration: function () { return ready ? yt.getDuration() || 0 : 0; },
            paused: function () { return !ready || yt.getPlayerState() !== 1; },
            setRate: function (r) { if (ready) yt.setPlaybackRate(r); },
            volume: function (x) { if (!ready) return 1; if (x != null) yt.setVolume(Math.round(x * 100)); return yt.getVolume() / 100; },
            muted: function (m) { if (!ready) return false; if (m === true) yt.mute(); else if (m === false) yt.unMute(); return yt.isMuted(); },
            buffered: function () { return ready ? (yt.getVideoLoadedFraction() || 0) * (yt.getDuration() || 0) : 0; },
        };
        loadYouTube(function () {
            yt = new YT.Player(holder, {
                videoId: self.opt.ytId,
                playerVars: { controls: 0, disablekb: 1, fs: 0, rel: 0, modestbranding: 1, playsinline: 1, iv_load_policy: 3, cc_load_policy: 0 },
                events: {
                    onReady: function () {
                        ready = true;
                        self.ui.spinner.hidden = true;
                        yt.setVolume(Math.round((pr.volume != null ? pr.volume : 1) * 100));
                        if (pr.muted) yt.mute();
                        if (pr.rate) yt.setPlaybackRate(pr.rate);
                        self._ready();
                    },
                    onStateChange: function (e) {
                        self.ui.spinner.hidden = e.data !== 3;
                        if (e.data === 0) self._ended();
                        self._state();
                    },
                },
            });
        });
    };

    LessonPlayer.prototype._ready = function () {
        var d = this.api.duration();
        if (d > 0) this.duration = d;
        this.ui.dur.textContent = fmt(this.duration);
        this._renderMarks();
        this._volUi();
        var at = this.opt.startAt || 0;
        if (at > 5 && this.duration && at < this.duration - 5) this._offerResume(at);
        if (this.opt.onReady) this.opt.onReady(this.duration);
        if (!this._loop) this._startLoop();
    };

    LessonPlayer.prototype._startLoop = function () {
        var self = this;
        this._loop = true;
        var last = -1;
        (function frame() {
            var time = self.api.time();
            if (time !== last) { last = time; self._progressUi(time); }
            requestAnimationFrame(frame);
        })();
        // Sahifaga xabar — sekundiga 4 marta (saqlash, faol bo'limni belgilash)
        setInterval(function () {
            if (self.opt.onTick) self.opt.onTick(self.api.time(), self.duration, !self.api.paused());
        }, 250);
    };

    LessonPlayer.prototype._progressUi = function (time) {
        var d = this.duration || this.api.duration() || 1;
        var pct = Math.min(100, time / d * 100);
        this.ui.played.style.width = pct + "%";
        this.ui.knob.style.left = pct + "%";
        this.ui.buf.style.width = Math.min(100, this.api.buffered() / d * 100) + "%";
        this.ui.cur.textContent = fmt(time);
        this.ui.bar.setAttribute("aria-valuenow", Math.round(time));
        var idx = this._chapterAt(time);
        if (idx !== this.chapterIndex) {
            this.chapterIndex = idx;
            this.ui.chapter.textContent = idx >= 0 ? this.chapters[idx].title : "";
            this.ui.marks.querySelectorAll("i").forEach(function (m, i) { m.classList.toggle("past", i <= idx); });
            if (this.opt.onChapter) this.opt.onChapter(idx);
        }
    };

    LessonPlayer.prototype._chapterAt = function (time) {
        var idx = -1;
        for (var i = 0; i < this.chapters.length; i++) if (this.chapters[i].start <= time + 0.25) idx = i;
        return idx;
    };

    LessonPlayer.prototype._renderMarks = function () {
        var d = this.duration || 1, m = this.ui.marks;
        m.textContent = "";
        this.chapters.forEach(function (c) {
            if (c.start <= 0 || c.start >= d) return;
            var i = document.createElement("i");
            i.style.left = (c.start / d * 100) + "%";
            m.appendChild(i);
        });
    };

    LessonPlayer.prototype._state = function () {
        var paused = this.api.paused();
        this.el.classList.toggle("playing", !paused);
        this.ui.play.innerHTML = ic(paused ? "play" : "pause");
        this.ui.play.setAttribute("aria-label", t(paused ? "lp.play" : "lp.pause"));
        this.ui.play.setAttribute("data-tip-key", paused ? "lp.play" : "lp.pause");
        if (!paused) { this.ui.end.hidden = true; this.ui.resume.hidden = true; }
        this._poke();
    };

    LessonPlayer.prototype._ended = function () {
        this.el.classList.remove("playing");
        if (this.opt.onEnded) this.opt.onEnded();
    };

    LessonPlayer.prototype._volUi = function () {
        var muted = this.api.muted(), vol = this.api.volume();
        this.ui.mute.innerHTML = ic(muted || vol === 0 ? "mute" : "vol");
        this.ui.volume.value = muted ? 0 : vol;
        this.ui.volume.style.setProperty("--v", (muted ? 0 : vol * 100) + "%");
    };

    LessonPlayer.prototype._offerResume = function (at) {
        var self = this, r = this.ui.resume;
        r.querySelector(".lp-resume-text").textContent = t("lp.resumeQ", { time: fmt(at) });
        r.querySelector('[data-resume="yes"]').textContent = t("lp.resumeYes");
        r.querySelector('[data-resume="no"]').textContent = t("lp.resumeNo");
        r.hidden = false;
        r.querySelector('[data-resume="yes"]').onclick = function () { self.seek(at); self.play(); r.hidden = true; };
        r.querySelector('[data-resume="no"]').onclick = function () { self.seek(0); self.play(); r.hidden = true; };
    };

    // ── Ochiq metodlar ──
    LessonPlayer.prototype.play = function () { this.api.play(); };
    LessonPlayer.prototype.pause = function () { this.api.pause(); };
    LessonPlayer.prototype.toggle = function () { if (this.api.paused()) this.play(); else this.pause(); };
    LessonPlayer.prototype.time = function () { return this.api.time(); };
    LessonPlayer.prototype.seek = function (s) {
        var d = this.duration || this.api.duration();
        s = Math.max(0, d ? Math.min(s, d - 0.2) : s);
        this.api.seek(s);
        this._progressUi(s);
        this.ui.end.hidden = true;
    };
    LessonPlayer.prototype.skip = function (delta) {
        this.seek(this.api.time() + delta);
        this._flash((delta > 0 ? "+" : "−") + Math.abs(delta) + "s");
    };
    LessonPlayer.prototype.goChapter = function (i, autoplay) {
        if (i < 0 || i >= this.chapters.length) return;
        this.seek(this.chapters[i].start + 0.05);
        if (autoplay !== false) this.play();
    };
    LessonPlayer.prototype.setCard = function (html) {
        this.ui.card.innerHTML = html || "";
        this.ui.card.hidden = !html || !this.cardOn;
    };
    LessonPlayer.prototype.showEnd = function (html) {
        this.ui.end.innerHTML = html;
        this.ui.end.hidden = false;
    };
    LessonPlayer.prototype.setRate = function (r) {
        this.api.setRate(r);
        this.ui.speedBtn.textContent = r + "×";
        var p = prefs(); p.rate = r; savePrefs(p);
        this.ui.menu.querySelectorAll("button").forEach(function (b) { b.classList.toggle("on", +b.dataset.rate === r); });
        this._flash(r + "×");
    };

    LessonPlayer.prototype._flash = function (text) {
        var f = this.ui.flash;
        f.textContent = text;
        f.classList.remove("show");
        void f.offsetWidth;
        f.classList.add("show");
    };

    LessonPlayer.prototype._poke = function () {
        var self = this;
        this.el.classList.add("ui-on");
        clearTimeout(this._hideT);
        this._hideT = setTimeout(function () {
            if (!self.api.paused() && !self.el.querySelector(".lp-controls:hover") && self.ui.menu.hidden) self.el.classList.remove("ui-on");
        }, 2600);
    };

    LessonPlayer.prototype._renderSpeedMenu = function (rate) {
        var self = this, m = this.ui.menu;
        m.innerHTML = "";
        [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2].forEach(function (r) {
            var b = document.createElement("button");
            b.type = "button";
            b.dataset.rate = r;
            b.textContent = r === 1 ? t("lp.normal") : r + "×";
            b.classList.toggle("on", r === rate);
            b.onclick = function () { self.setRate(r); m.hidden = true; };
            m.appendChild(b);
        });
        this.ui.speedBtn.textContent = rate + "×";
    };

    LessonPlayer.prototype._renderHelp = function () {
        var rows = [["Space / K", "lp.kPlay"], ["← / →", "lp.k5"], ["J / L", "lp.k10"], ["↑ / ↓", "lp.kVol"], ["M", "lp.kMute"],
            ["N / P", "lp.kChapter"], ["0–9", "lp.kPercent"], ["F", "lp.kFull"], ["T", "lp.kTheater"], ["C", "lp.kCard"], ["< / >", "lp.kSpeed"]];
        this.ui.help.innerHTML = '<div class="lp-help-box"><b>' + t("lp.shortcuts") + "</b><dl>" +
            rows.map(function (r) { return "<dt><kbd>" + r[0] + "</kbd></dt><dd>" + t(r[1]) + "</dd>"; }).join("") +
            '</dl><button type="button" class="lp-pill primary" data-help-close>OK</button></div>';
        var self = this;
        this.ui.help.querySelector("[data-help-close]").onclick = function () { self.ui.help.hidden = true; self.el.focus(); };
    };

    LessonPlayer.prototype._relabel = function () {
        this.el.querySelectorAll("[data-tip-key]").forEach(function (b) { b.setAttribute("aria-label", t(b.getAttribute("data-tip-key"))); });
        this._renderHelp();
        this._renderSpeedMenu(prefs().rate || 1);
    };

    LessonPlayer.prototype._fullscreen = function () {
        var on = document.fullscreenElement || document.webkitFullscreenElement;
        if (on) (document.exitFullscreen || document.webkitExitFullscreen).call(document);
        else {
            var req = this.el.requestFullscreen || this.el.webkitRequestFullscreen;
            if (req) { var p = req.call(this.el); if (p && p.catch) p.catch(function () { }); }
            else if (this.video && this.video.webkitEnterFullscreen) this.video.webkitEnterFullscreen(); // iPhone
        }
    };

    LessonPlayer.prototype._wire = function (pr) {
        var self = this, ui = this.ui, el = this.el;

        ui.big.onclick = function () { self.play(); };
        ui.play.onclick = function () { self.toggle(); };
        ui.back.onclick = function () { self.skip(-10); };
        ui.fwd.onclick = function () { self.skip(10); };
        ui.prev.onclick = function () {
            var i = self._chapterAt(self.api.time());
            // Bo'lim boshidan 3 soniya o'tgan bo'lsa — shu bo'lim boshiga, aks holda oldingisiga
            if (i >= 0 && self.api.time() - self.chapters[i].start > 3) self.goChapter(i); else self.goChapter(Math.max(0, i - 1));
        };
        ui.next.onclick = function () { self.goChapter(self._chapterAt(self.api.time()) + 1); };
        ui.mute.onclick = function () {
            var m = !self.api.muted();
            self.api.muted(m);
            var p = prefs(); p.muted = m; savePrefs(p);
            setTimeout(function () { self._volUi(); }, 60);
        };
        ui.volume.oninput = function () {
            var v = +ui.volume.value;
            self.api.volume(v);
            self.api.muted(v === 0);
            var p = prefs(); p.volume = v; p.muted = v === 0; savePrefs(p);
            setTimeout(function () { self._volUi(); }, 60);
        };
        ui.cardBtn.onclick = function () {
            self.cardOn = !self.cardOn;
            ui.cardBtn.classList.toggle("on", self.cardOn);
            ui.card.hidden = !self.cardOn || !ui.card.innerHTML;
            var p = prefs(); p.card = self.cardOn; savePrefs(p);
        };
        ui.speedBtn.onclick = function (e) { e.stopPropagation(); ui.menu.hidden = !ui.menu.hidden; };
        document.addEventListener("click", function (e) { if (!ui.menu.hidden && !ui.menu.parentElement.contains(e.target)) ui.menu.hidden = true; });
        ui.pip.onclick = function () {
            if (!self.video) return;
            if (document.pictureInPictureElement) document.exitPictureInPicture().catch(function () { });
            else self.video.requestPictureInPicture().catch(function () { });
        };
        ui.theater.onclick = function () {
            var on = !document.documentElement.classList.contains("lp-theater-on");
            document.documentElement.classList.toggle("lp-theater-on", on);
            ui.theater.classList.toggle("on", on);
        };
        ui.full.onclick = function () { self._fullscreen(); };
        var fsSync = function () {
            var on = (document.fullscreenElement || document.webkitFullscreenElement) === el;
            el.classList.toggle("is-full", on);
            ui.full.innerHTML = ic(on ? "exitFull" : "full");
        };
        document.addEventListener("fullscreenchange", fsSync);
        document.addEventListener("webkitfullscreenchange", fsSync);
        ui.helpBtn.onclick = function () { ui.help.hidden = !ui.help.hidden; };

        // Bosish: pauza/davom; ikki marta bosish: to'liq ekran
        var clickT = null;
        ui.shade.addEventListener("click", function () {
            if (clickT) return;
            clickT = setTimeout(function () { clickT = null; self.toggle(); }, 220);
        });
        ui.shade.addEventListener("dblclick", function () { clearTimeout(clickT); clickT = null; self._fullscreen(); });

        // Telefon: chap/o'ng chetni ikki marta tegish — ∓10 soniya
        var lastTap = { t: 0, side: 0 };
        el.querySelectorAll("[data-tap]").forEach(function (z) {
            z.addEventListener("touchend", function (e) {
                var now = Date.now(), side = +z.dataset.tap;
                if (now - lastTap.t < 320 && lastTap.side === side) { e.preventDefault(); self.skip(side); lastTap.t = 0; }
                else { lastTap = { t: now, side: side }; self._poke(); }
            });
        });

        // Vaqt chizig'i: bosish/sudrash, ustiga kelganda — vaqt va bo'lim nomi
        var drag = false;
        function posToTime(clientX) {
            var r = ui.bar.getBoundingClientRect();
            var x = Math.max(0, Math.min(r.width, clientX - r.left));
            return { time: x / r.width * (self.duration || 0), x: x, w: r.width };
        }
        function hover(clientX) {
            var p = posToTime(clientX);
            ui.hover.hidden = false;
            ui.hover.style.left = Math.max(60, Math.min(p.w - 60, p.x)) + "px";
            ui.hoverTime.textContent = fmt(p.time);
            var ci = self._chapterAt(p.time);
            ui.hoverTitle.textContent = ci >= 0 ? self.chapters[ci].title : "";
        }
        ui.bar.addEventListener("pointerdown", function (e) {
            drag = true;
            ui.bar.setPointerCapture(e.pointerId);
            self.seek(posToTime(e.clientX).time);
            el.classList.add("seeking");
        });
        ui.bar.addEventListener("pointermove", function (e) {
            hover(e.clientX);
            if (drag) self.seek(posToTime(e.clientX).time);
        });
        ui.bar.addEventListener("pointerup", function () { drag = false; el.classList.remove("seeking"); });
        ui.bar.addEventListener("pointerleave", function () { if (!drag) ui.hover.hidden = true; });

        el.addEventListener("mousemove", function () { self._poke(); });
        el.addEventListener("touchstart", function () { self._poke(); }, { passive: true });

        // Klaviatura (pleer fokusda yoki sahifada boshqa maydon tanlanmagan bo'lsa)
        document.addEventListener("keydown", function (e) {
            var tag = document.activeElement ? document.activeElement.tagName : "";
            if (/INPUT|TEXTAREA|SELECT/.test(tag) && document.activeElement !== ui.volume) return;
            if (e.ctrlKey || e.metaKey || e.altKey) return;
            var k = e.key, handled = true;
            if (k === " " || k === "k" || k === "K") self.toggle();
            else if (k === "ArrowRight") self.skip(5);
            else if (k === "ArrowLeft") self.skip(-5);
            else if (k === "l" || k === "L") self.skip(10);
            else if (k === "j" || k === "J") self.skip(-10);
            else if (k === "ArrowUp" || k === "ArrowDown") {
                var v = Math.max(0, Math.min(1, self.api.volume() + (k === "ArrowUp" ? 0.1 : -0.1)));
                self.api.volume(v); self.api.muted(false);
                var p = prefs(); p.volume = v; p.muted = false; savePrefs(p);
                setTimeout(function () { self._volUi(); }, 60);
                self._flash(Math.round(v * 100) + "%");
            }
            else if (k === "m" || k === "M") ui.mute.click();
            else if (k === "f" || k === "F") self._fullscreen();
            else if (k === "t" || k === "T") ui.theater.click();
            else if (k === "c" || k === "C") ui.cardBtn.click();
            else if (k === "n" || k === "N") ui.next.click();
            else if (k === "p" || k === "P") ui.prev.click();
            else if (k === "?") ui.helpBtn.click();
            else if (k === ">" || k === "<") {
                var rates = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2], cur = prefs().rate || 1;
                var i = rates.indexOf(cur); if (i < 0) i = 2;
                self.setRate(rates[Math.max(0, Math.min(rates.length - 1, i + (k === ">" ? 1 : -1)))]);
            }
            else if (/^[0-9]$/.test(k)) self.seek((self.duration || 0) * (+k) / 10);
            else handled = false;
            if (handled) { e.preventDefault(); self._poke(); }
        });
    };

    /** Suv belgisi (video va jonli efir uchun umumiy): har 6 soniyada tasodifiy joyga ko'chadi, to'liq ekranda ham ko'rinadi. */
    LessonPlayer.watermark = function (host, text) {
        var wm = document.createElement("div");
        wm.className = "lp-wm";
        wm.setAttribute("aria-hidden", "true");
        wm.textContent = text;
        host.appendChild(wm);
        function move() {
            wm.style.left = (5 + Math.random() * 55) + "%";
            wm.style.top = (8 + Math.random() * 70) + "%";
        }
        move();
        setInterval(move, 6000);
        // Element o'chirib tashlansa (DevTools orqali) — qayta qo'yiladi
        new MutationObserver(function () { if (!wm.isConnected) host.appendChild(wm); }).observe(host, { childList: true });
        return wm;
    };

    window.LessonPlayer = LessonPlayer;
})();
