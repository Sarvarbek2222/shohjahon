"""
ptest.uz savollarini AvtoPravaSam bazasiga qo'shadi (eski savollar o'chirilmaydi).

  * Savollar Questions jadvaliga Source='ptest', BiletNumber (1..66), BiletOrder (1..20) bilan yoziladi.
    Eski savollar Source='old' bo'lib qoladi va faqat "Mavzular" bo'limida ishlatiladi.
  * Rasmlar wwwroot/uploads/pt_<xesh>.webp ga siqib yoziladi: eng uzun tomoni 1280px, WebP sifat 86
    (ko'zga farq sezilmaydi, hajm ~60% kichik). Bir xil rasmlar bitta faylga birlashtiriladi.
  * Har bir ptest savoli eski bazadan qidiriladi: topilsa uning ruscha / kirillcha tarjimasi, izohi va mavzusi
    ko'chiriladi. Kirillchasi topilmaganlarini sayt ishga tushganda o'zi transliteratsiya qiladi.
  * Bajarilgan SQL tools/ptest_import.sql ga ham yoziladi — boshqa serverga ko'chirish uchun.

Ishga tushirish (loyiha papkasidan):
    python tools/ptest_import.py --json ..\\Dekanat\\ptest_parallel_master_database.json --images ..\\Dekanat\\ptest_all_images
    python tools/ptest_import.py ... --replace      # ptest savollari bazada bor bo'lsa, qaytadan yozish
"""

import argparse
import hashlib
import json
import os
import re
import sys
from collections import defaultdict

import pymysql
from PIL import Image

MAX_SIDE = 1280
WEBP_QUALITY = 86
PREFIX = "pt_"

APOSTROPHES = "ʻʼ’‘`´ʹ′"


def norm(text):
    """Solishtirish uchun: kichik harf, apostroflar bir xil, tinish belgilarisiz."""
    s = (text or "").lower()
    for a in APOSTROPHES:
        s = s.replace(a, "'")
    s = re.sub(r"[^\w']+", " ", s)
    return " ".join(s.split())


# Kalit uchun kirill harflarni lotinga: sayt lotin maydonidagi « А » ni « A » ga o'giradi, kalit o'zgarmasin
_CYR = dict(zip("абвгдеёжзийклмнопрстуфхцчшщъыьэюяўқғҳ",
                ["a", "b", "v", "g", "d", "e", "yo", "j", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t",
                 "u", "f", "x", "ts", "ch", "sh", "sh", "", "i", "", "e", "yu", "ya", "o'", "q", "g'", "h"]))


def key(text):
    """Tarjimani topish kaliti: tartibga bog'liq emas, yozuvdagi kichik farqlarga chidamli."""
    return "".join(_CYR.get(ch, ch) for ch in norm(text))


def question_key(question, answers):
    """Savol kaliti = matn + variantlar to'plami (tartibsiz). Bir xil matnli, lekin har xil savollar ajraladi."""
    return key(question) + "||" + "|".join(sorted(key(a) for a in answers))


def parse_conn(conn_str):
    parts = dict(p.split("=", 1) for p in conn_str.split(";") if "=" in p)
    low = {k.strip().lower(): v.strip() for k, v in parts.items()}
    return dict(host=low.get("server", "localhost"), port=int(low.get("port", 3306)),
                user=low.get("user", low.get("uid", "root")), password=low.get("password", low.get("pwd", "")),
                database=low["database"], charset="utf8mb4")


# ---------------------------------------------------------------------------
# Rasmlar
# ---------------------------------------------------------------------------

def optimize_image(src, uploads_dir, cache):
    """Rasmni siqib uploads ga yozadi, fayl nomini qaytaradi. Bir xil rasm — bitta fayl."""
    with open(src, "rb") as f:
        raw = f.read()
    digest = hashlib.md5(raw).hexdigest()[:16]
    if digest in cache:
        return cache[digest], 0, 0
    name = f"{PREFIX}{digest}.webp"
    dst = os.path.join(uploads_dir, name)
    if not os.path.exists(dst):
        im = Image.open(src)
        has_alpha = im.mode in ("RGBA", "LA") or (im.mode == "P" and "transparency" in im.info)
        im = im.convert("RGBA" if has_alpha else "RGB")
        w, h = im.size
        scale = min(1.0, MAX_SIDE / max(w, h))
        if scale < 1.0:
            im = im.resize((round(w * scale), round(h * scale)), Image.LANCZOS)
        im.save(dst, "WEBP", quality=WEBP_QUALITY, method=6)
    cache[digest] = name
    return name, len(raw), os.path.getsize(dst)


# ---------------------------------------------------------------------------
# Eski bazadan moslarini topish
# ---------------------------------------------------------------------------

def load_old(cur):
    cur.execute("SELECT Id, QuestionUZ, QuestionRU, QuestionUZK, Topic, ExplanationUZ, ExplanationRU, ExplanationUZK "
                "FROM Questions WHERE Source <> 'ptest'")
    qs = {r[0]: dict(id=r[0], uz=r[1], ru=r[2], uzk=r[3], topic=r[4], exp_uz=r[5], exp_ru=r[6], exp_uzk=r[7], options=[])
          for r in cur.fetchall()}
    cur.execute("SELECT QuestionId, OptionUZ, OptionRU, OptionUZK, IsCorrect FROM Options ORDER BY Id")
    for qid, uz, ru, uzk, ok in cur.fetchall():
        if qid in qs:
            qs[qid]["options"].append(dict(uz=uz, ru=ru, uzk=uzk, ok=bool(ok), n=norm(uz)))
    by_text = defaultdict(list)
    for q in qs.values():
        by_text[norm(q["uz"])].append(q)
    return by_text


def find_match(pq, by_text):
    cands = by_text.get(norm(pq["question"]), [])
    if not cands:
        return None
    p_opts = {norm(a) for a in pq["all_answers"]}
    p_correct = norm(pq["correct_answer"])
    good = []
    for c in cands:
        c_opts = {o["n"] for o in c["options"]}
        c_correct = next((o["n"] for o in c["options"] if o["ok"]), None)
        if c_correct != p_correct:
            continue
        overlap = len(p_opts & c_opts) / max(len(p_opts | c_opts), 1)
        if overlap >= 0.8:
            good.append((overlap, c))
    if not good:
        return None
    good.sort(key=lambda x: -x[0])
    # Bir nechta teng nomzod bo'lsa (masalan, har xil rasmli bir xil savol) — tarjimasi bir xil bo'lsagina olamiz
    best = [c for s, c in good if s == good[0][0]]
    if len({(c["ru"], c["uzk"]) for c in best}) > 1:
        return None
    return best[0]


# ---------------------------------------------------------------------------

def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    here = os.path.dirname(os.path.abspath(__file__))
    project = os.path.dirname(here)

    ap = argparse.ArgumentParser()
    ap.add_argument("--json", required=True, help="ptest_parallel_master_database.json")
    ap.add_argument("--images", required=True, help="ptest_all_images papkasi")
    ap.add_argument("--replace", action="store_true", help="bazadagi ptest savollarini o'chirib, qaytadan yozish")
    ap.add_argument("--dry-run", action="store_true", help="bazaga yozmasdan faqat hisobot")
    args = ap.parse_args()

    with open(os.path.join(project, "appsettings.json"), encoding="utf-8-sig") as f:
        conn_args = parse_conn(json.load(f)["ConnectionStrings"]["DefaultConnection"])
    with open(args.json, encoding="utf-8") as f:
        tickets = sorted(json.load(f), key=lambda t: t["ticket_number"])

    # --- tekshiruv: chala ma'lumot bilan import qilmaymiz
    problems = []
    for t in tickets:
        if len(t["questions"]) != 20:
            problems.append(f"{t['ticket_number']}-bilet: {len(t['questions'])} savol")
        for q in t["questions"]:
            if not q or not q.get("question") or q.get("correct_answer") not in q.get("all_answers", []):
                problems.append(f"{t['ticket_number']}-bilet {q and q.get('question_number')}-savol to'liq emas")
    if problems:
        sys.exit("❌ JSON chala:\n  " + "\n  ".join(problems[:20]))

    print(f"🗄  Baza: {conn_args['database']} | biletlar: {len(tickets)} | savollar: {sum(len(t['questions']) for t in tickets)}")
    db = pymysql.connect(**conn_args, autocommit=False)
    cur = db.cursor()

    # Ustunlar (sayt ham ishga tushganda xuddi shunday qo'shadi — DbUpgrade.EnsureQuestionSourceColumnsAsync)
    cur.execute("SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Questions'")
    cols = {r[0] for r in cur.fetchall()}
    ddl = []
    if "Source" not in cols:
        ddl.append("ALTER TABLE `Questions` ADD COLUMN `Source` varchar(16) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'old'")
    if "BiletNumber" not in cols:
        ddl.append("ALTER TABLE `Questions` ADD COLUMN `BiletNumber` int NULL")
    if "BiletOrder" not in cols:
        ddl.append("ALTER TABLE `Questions` ADD COLUMN `BiletOrder` int NULL")
    if not args.dry_run:
        for s in ddl:
            cur.execute(s)

    existing = 0
    if "Source" in cols:
        cur.execute("SELECT COUNT(*) FROM Questions WHERE Source = 'ptest'")
        existing = cur.fetchone()[0]
    if existing and not args.replace:
        sys.exit(f"⚠️ Bazada allaqachon {existing} ta ptest savoli bor. Qaytadan yozish uchun --replace qo'shing.")

    by_text = load_old(cur) if "Source" in cols or not args.dry_run else load_old_fallback(cur)

    # --- rasmlar
    uploads = os.path.join(project, "wwwroot", "uploads")
    os.makedirs(uploads, exist_ok=True)
    img_cache, before, after, img_count = {}, 0, 0, 0

    ru_path = os.path.join(here, "ptest_ru.json")
    ru_manual = {}
    if os.path.exists(ru_path):
        with open(ru_path, encoding="utf-8") as f:
            ru_manual = json.load(f)

    rows = []
    matched = 0
    for t in tickets:
        for q in t["questions"]:
            image = None
            if q.get("image_path"):
                src = q["image_path"]
                if not os.path.isabs(src):
                    src = os.path.join(args.images, os.path.basename(src))
                image, b, a = optimize_image(src, uploads, img_cache)
                before += b
                after += a
                img_count += 1

            m = find_match(q, by_text)
            matched += m is not None
            opt_by_norm = {o["n"]: o for o in m["options"]} if m else {}
            # Eski bazada ruschasi bo'lmasa — qo'lda qilingan tarjima (tools/ptest_ru.json)
            # Kalit — savol matni + variantlar to'plami: ptest'da savollar va variantlar tartibi har safar o'zgaradi
            manual = ru_manual.get(question_key(q["question"], q["all_answers"]), {})
            options = []
            for text in q["all_answers"]:
                o = opt_by_norm.get(norm(text))
                ru = (o and o["ru"]) or manual.get("options", {}).get(key(text))
                options.append(dict(uz=text, ru=ru, uzk=o and o["uzk"], ok=text == q["correct_answer"]))
            rows.append(dict(
                bilet=t["ticket_number"], order=q["question_number"], uz=q["question"],
                ru=(m and m["ru"]) or manual.get("question"), uzk=m and m["uzk"], topic=m and m["topic"],
                exp_uz=m and m["exp_uz"], exp_ru=m and m["exp_ru"], exp_uzk=m and m["exp_uzk"],
                image=image, options=options))

    # --- SQL
    def lit(v):
        if v is None:
            return "NULL"
        if isinstance(v, bool):
            return "1" if v else "0"
        if isinstance(v, int):
            return str(v)
        return "'" + pymysql.converters.escape_string(str(v)) + "'"

    sql = ["-- ptest.uz savollari (tools/ptest_import.py yaratgan)", "SET NAMES utf8mb4;", *[s + ";" for s in ddl], "START TRANSACTION;"]
    if args.replace:
        sql.append("DELETE o FROM `Options` o JOIN `Questions` q ON q.Id = o.QuestionId WHERE q.Source = 'ptest';")
        sql.append("DELETE FROM `Questions` WHERE Source = 'ptest';")
    q_cols = "(`QuestionUZ`,`QuestionRU`,`QuestionUZK`,`Topic`,`ImageUrl`,`ExplanationUZ`,`ExplanationRU`,`ExplanationUZK`,`Source`,`BiletNumber`,`BiletOrder`)"
    statements = []
    for r in rows:
        q_sql = (f"INSERT INTO `Questions` {q_cols} VALUES ({lit(r['uz'])},{lit(r['ru'])},{lit(r['uzk'])},{lit(r['topic'])},"
                 f"{lit(r['image'])},{lit(r['exp_uz'])},{lit(r['exp_ru'])},{lit(r['exp_uzk'])},'ptest',{r['bilet']},{r['order']})")
        o_sql = ("INSERT INTO `Options` (`QuestionId`,`OptionUZ`,`OptionRU`,`OptionUZK`,`IsCorrect`) VALUES " +
                 ",".join(f"(@qid,{lit(o['uz'])},{lit(o['ru'])},{lit(o['uzk'])},{lit(o['ok'])})" for o in r["options"]))
        statements.append((q_sql, o_sql))
        sql += [q_sql + ";", "SET @qid = LAST_INSERT_ID();", o_sql + ";"]
    sql.append("COMMIT;")
    sql_path = os.path.join(here, "ptest_import.sql")
    with open(sql_path, "w", encoding="utf-8") as f:
        f.write("\n".join(sql) + "\n")

    # --- bazaga yozish (bitta tranzaksiya: xato bo'lsa hech narsa yozilmaydi)
    if not args.dry_run:
        try:
            if args.replace:
                cur.execute("DELETE o FROM `Options` o JOIN `Questions` q ON q.Id = o.QuestionId WHERE q.Source = 'ptest'")
                cur.execute("DELETE FROM `Questions` WHERE Source = 'ptest'")
            for q_sql, o_sql in statements:
                cur.execute(q_sql)
                cur.execute("SET @qid = LAST_INSERT_ID()")
                cur.execute(o_sql)
            db.commit()
        except Exception:
            db.rollback()
            raise

        # Endi hech bir savolda ishlatilmaydigan eski ptest rasmlarini o'chiramiz
        used = set(img_cache.values())
        removed = 0
        for f in os.listdir(uploads):
            if f.startswith(PREFIX) and f.endswith(".webp") and f not in used:
                os.remove(os.path.join(uploads, f))
                removed += 1
        if removed:
            print(f"🧹 Ishlatilmaydigan eski ptest rasmlari o'chirildi: {removed} ta")

    if args.dry_run and "Source" not in cols:
        cur.execute("SELECT 'old', COUNT(*) FROM Questions")
    else:
        cur.execute("SELECT Source, COUNT(*) FROM Questions GROUP BY Source")
    totals = dict(cur.fetchall())
    db.close()

    print(f"🖼  Rasmlar: {img_count} ta savolda, {len(img_cache)} ta noyob fayl | "
          f"{before / 2**20:.1f} MB -> {after / 2**20:.1f} MB ({100 - 100 * after / max(before, 1):.0f}% kichik)")
    print(f"🔗 Eski bazadan topilgan (tarjima/mavzu olindi): {matched}/{len(rows)}")
    no_ru_q = sum(1 for r in rows if not r["ru"])
    no_ru_o = sum(1 for r in rows for o in r["options"] if not o["ru"])
    print(f"🇷🇺 Ruschasiz qoldi: {no_ru_q} savol, {no_ru_o} variant")
    print(f"📊 Bazada: {totals}")
    print(f"💾 SQL: {sql_path}")
    if args.dry_run:
        print("ℹ️  --dry-run: bazaga hech narsa yozilmadi.")


def load_old_fallback(cur):
    """Source ustuni hali yo'q bo'lsa (dry-run) — barcha savollar eski."""
    cur.execute("SELECT Id, QuestionUZ, QuestionRU, QuestionUZK, Topic, ExplanationUZ, ExplanationRU, ExplanationUZK FROM Questions")
    qs = {r[0]: dict(id=r[0], uz=r[1], ru=r[2], uzk=r[3], topic=r[4], exp_uz=r[5], exp_ru=r[6], exp_uzk=r[7], options=[])
          for r in cur.fetchall()}
    cur.execute("SELECT QuestionId, OptionUZ, OptionRU, OptionUZK, IsCorrect FROM Options ORDER BY Id")
    for qid, uz, ru, uzk, ok in cur.fetchall():
        if qid in qs:
            qs[qid]["options"].append(dict(uz=uz, ru=ru, uzk=uzk, ok=bool(ok), n=norm(uz)))
    by_text = defaultdict(list)
    for q in qs.values():
        by_text[norm(q["uz"])].append(q)
    return by_text


if __name__ == "__main__":
    main()
