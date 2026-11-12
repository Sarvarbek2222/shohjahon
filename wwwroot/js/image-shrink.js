/*
 * Savol rasmi yuborilishidan oldin brauzerda kichraytiriladi (Views/Question/Create, Edit).
 * Sabab: serverdagi Nginx standart holatda 1 MB dan katta so'rovni "413 Request Entity Too Large" bilan qaytaradi,
 * telefon rasmlari esa 2–8 MB bo'ladi. Rasm eng uzun tomoni 1600 px gacha kichraytiriladi va ~900 KB dan oshmaydi
 * (sifat ko'zga sezilmaydi). Shaffof belgilar uchun WebP; brauzer WebP yarata olmasa — oq fonli JPEG.
 */
(function () {
    "use strict";
    var MAX_SIDE = 1600, MAX_BYTES = 900 * 1024, SKIP_BELOW = 600 * 1024;
    var pending = null;

    function kb(n) { return n >= 1048576 ? (n / 1048576).toFixed(1) + " MB" : Math.round(n / 1024) + " KB"; }

    function toBlob(canvas, type, q) {
        return new Promise(function (res) { canvas.toBlob(res, type, q); });
    }

    async function shrink(file) {
        var bmp = await createImageBitmap(file);
        var scale = Math.min(1, MAX_SIDE / Math.max(bmp.width, bmp.height));
        if (file.size <= SKIP_BELOW && scale === 1) return null;
        var c = document.createElement("canvas");
        c.width = Math.round(bmp.width * scale);
        c.height = Math.round(bmp.height * scale);
        var g = c.getContext("2d");
        var type = "image/webp";
        var probe = await toBlob(c, type, 0.9);
        if (!probe || probe.type !== type) {
            type = "image/jpeg";
            g.fillStyle = "#fff";
            g.fillRect(0, 0, c.width, c.height);
        }
        g.imageSmoothingQuality = "high";
        g.drawImage(bmp, 0, 0, c.width, c.height);
        var blob = null;
        for (var q = 0.88; q >= 0.5; q -= 0.08) {
            blob = await toBlob(c, type, q);
            if (blob && blob.size <= MAX_BYTES) break;
        }
        if (!blob || blob.size >= file.size) return null;
        var base = (file.name || "rasm").replace(/\.[^.]+$/, "");
        return new File([blob], base + (type === "image/webp" ? ".webp" : ".jpg"), { type: type, lastModified: Date.now() });
    }

    function note(input, text) {
        var el = document.getElementById("imageShrinkNote");
        if (!el) {
            el = document.createElement("p");
            el.id = "imageShrinkNote";
            el.className = "qf-hint qf-note";
            (input.closest(".upload-zone, [id=uploadZone]") || input).parentElement.appendChild(el);
        }
        el.textContent = text;
        el.style.display = text ? "" : "none";
    }

    // Capture bosqichida — sahifaning o'z (oldindan ko'rish) ishlovchisidan oldin ishga tushadi
    document.addEventListener("change", function (e) {
        var input = e.target;
        if (!input || input.id !== "imageInput" || !input.files || !input.files[0]) return;
        var file = input.files[0];
        if (!/^image\//.test(file.type) || !window.createImageBitmap) return;
        note(input, "");
        pending = shrink(file).then(function (small) {
            if (small) {
                var dt = new DataTransfer();
                dt.items.add(small);
                input.files = dt.files;
                note(input, (window.I18n ? I18n.t("form.imageShrunk", { from: kb(file.size), to: kb(small.size) }) : kb(small.size)));
            }
        }).catch(function () { /* kichraytirib bo'lmasa — asl fayl yuboriladi */ })
            .finally(function () { pending = null; });
    }, true);

    // Kichraytirish tugamasdan yuborilsa — tugashini kutib, keyin yuboriladi
    document.addEventListener("submit", function (e) {
        var form = e.target;
        if (!pending || !form.querySelector("#imageInput")) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        var p = pending;
        p.then(function () { form.requestSubmit(); });
    }, true);
})();
