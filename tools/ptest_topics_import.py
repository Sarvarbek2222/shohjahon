"""
ptest.uz mavzularini bazaga yozadi va eski (Source='old') savollarni o'chiradi.

  * ptest_topics_database.json dagi har bir mavzu savoli bazadagi ptest savoli bilan solishtiriladi
    (matn + variantlar to'plami + rasm). Mavzular yangi savol qo'shmaydi — hammasi biletlardagi savollar.
  * QuestionTopics jadvaliga savol <-> mavzu bog'lanishi yoziladi (ptest tartibida). Bir savol bir nechta
    mavzuda bo'lishi mumkin. Questions.Topic ga esa birinchi (asosiy) mavzu yoziladi — statistika shuni ishlatadi.
  * --delete-old: eski savollar, ularning variantlari va endi hech qaysi savolda ishlatilmaydigan eski rasmlar
    o'chiriladi. (TestAnswers / UserMistakes dagi shu savollarga tegishli yozuvlar ham CASCADE bilan o'chadi.)
  * tools/ptest_topics_import.sql — boshqa serverga qo'llash uchun (ptest_import.sql dan keyin).

    python tools/ptest_topics_import.py --json ..\\Dekanat\\ptest_topics_database.json --delete-old
"""

import argparse
import hashlib
import json
import os
import re
import sys

import pymysql

sys.stdout.reconfigure(encoding="utf-8")
here = os.path.dirname(os.path.abspath(__file__))
project = os.path.dirname(here)
sys.path.insert(0, here)
from ptest_import import parse_conn, question_key  # noqa: E402

DDL = """CREATE TABLE IF NOT EXISTS `QuestionTopics` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `QuestionId` int NOT NULL,
  `Topic` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
  `SortOrder` int NOT NULL DEFAULT 0,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `IX_QuestionTopics_QuestionId_Topic` (`QuestionId`, `Topic`),
  KEY `IX_QuestionTopics_Topic_SortOrder` (`Topic`, `SortOrder`),
  CONSTRAINT `FK_QuestionTopics_Questions` FOREIGN KEY (`QuestionId`) REFERENCES `Questions` (`Id`) ON DELETE CASCADE
) CHARACTER SET utf8mb4"""


def app_topics():
    """Models/Topics.cs dagi Topics.All — kirill kalitlar, rasmiy tartibda (ptest bilan bir xil)."""
    src = open(os.path.join(project, "Models", "Topics.cs"), encoding="utf-8-sig").read()
    body = src[src.index("All = new[]"):src.index("};")]
    return re.findall(r'"([^"]+)"', body)


def pt_name(path):
    with open(path, "rb") as f:
        return "pt_" + hashlib.md5(f.read()).hexdigest()[:16] + ".webp"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", required=True, help="ptest_topics_database.json")
    ap.add_argument("--delete-old", action="store_true", help="eski (Source='old') savollarni o'chirish")
    args = ap.parse_args()

    json_dir = os.path.dirname(os.path.abspath(args.json))
    topics_json = sorted(json.load(open(args.json, encoding="utf-8")), key=lambda t: t["topic_number"])
    keys = app_topics()
    if len(keys) != len(topics_json):
        sys.exit(f"❌ Ilovada {len(keys)} ta mavzu, ptest'da {len(topics_json)} ta")

    with open(os.path.join(project, "appsettings.json"), encoding="utf-8-sig") as f:
        conn = parse_conn(json.load(f)["ConnectionStrings"]["DefaultConnection"])
    db = pymysql.connect(**conn, autocommit=False)
    cur = db.cursor()
    cur.execute(DDL)

    # Bazadagi ptest savollari: kalit (matn + variantlar + rasm) -> [(Id, bilet, tartib)]
    cur.execute("SELECT Id, QuestionUZ, ImageUrl, BiletNumber, BiletOrder FROM Questions WHERE Source = 'ptest'")
    rows = cur.fetchall()
    cur.execute("SELECT o.QuestionId, o.OptionUZ FROM Options o JOIN Questions q ON q.Id = o.QuestionId "
                "WHERE q.Source = 'ptest' ORDER BY o.Id")
    opts = {}
    for qid, uz in cur.fetchall():
        opts.setdefault(qid, []).append(uz)
    by_key = {}
    for qid, uz, img, b, o in rows:
        by_key.setdefault(question_key(uz, opts.get(qid, [])) + "|" + (img or ""), []).append((qid, b, o))
    for v in by_key.values():
        v.sort()

    links = []          # (canonical Id, bilet, tartib, mavzu kaliti, SortOrder)
    primary = {}        # savol kaliti -> birinchi mavzu (barcha nusxalariga yoziladi)
    missing = []
    for t, topic in zip(topics_json, keys):
        seen = set()
        order = 0
        for q in t["questions"]:
            img = pt_name(os.path.join(json_dir, q["image_path"])) if q.get("image_path") else ""
            k = question_key(q["question"], q["all_answers"]) + "|" + img
            match = by_key.get(k)
            if not match:
                missing.append((t["topic_number"], q["question"][:60]))
                continue
            qid, b, o = match[0]
            if qid in seen:
                continue  # ptest'da mavzu ichida takror savol — bir marta ko'rsatamiz
            seen.add(qid)
            order += 1
            links.append((qid, b, o, topic, order))
            primary.setdefault(k, topic)
    if missing:
        sys.exit(f"❌ Bazada topilmagan mavzu savollari: {len(missing)} — {missing[:5]}")

    try:
        cur.execute("DELETE FROM QuestionTopics")
        cur.executemany("INSERT INTO QuestionTopics (QuestionId, Topic, SortOrder) VALUES (%s, %s, %s)",
                        [(qid, topic, so) for qid, _, _, topic, so in links])
        # Asosiy mavzu: savolning biletlardagi barcha nusxalariga
        cur.execute("UPDATE Questions SET Topic = NULL WHERE Source = 'ptest'")
        upd = [(topic, qid) for k, topic in primary.items() for qid, _, _ in by_key[k]]
        cur.executemany("UPDATE Questions SET Topic = %s WHERE Id = %s", upd)

        deleted_q = deleted_o = removed_files = 0
        if args.delete_old:
            cur.execute("SELECT Id, ImageUrl FROM Questions WHERE Source = 'old'")
            old = cur.fetchall()
            old_ids = [r[0] for r in old]
            if old_ids:
                ph = ",".join(["%s"] * len(old_ids))
                deleted_o = cur.execute(f"DELETE FROM Options WHERE QuestionId IN ({ph})", old_ids)
                deleted_q = cur.execute(f"DELETE FROM Questions WHERE Id IN ({ph})", old_ids)
        db.commit()
    except Exception:
        db.rollback()
        raise

    # Endi hech qaysi savolda ishlatilmaydigan eski rasmlar (ptest rasmlariga tegilmaydi)
    if args.delete_old:
        cur.execute("SELECT DISTINCT ImageUrl FROM Questions WHERE ImageUrl IS NOT NULL AND ImageUrl <> ''")
        used = {r[0] for r in cur.fetchall()}
        uploads = os.path.join(project, "wwwroot", "uploads")
        for f in os.listdir(uploads):
            p = os.path.join(uploads, f)
            if os.path.isfile(p) and not f.startswith("pt_") and f not in used:
                os.remove(p)
                removed_files += 1

    # --- boshqa server uchun SQL (ptest_import.sql dan keyin): Id o'rniga bilet + tartib raqami bilan
    esc = lambda v: "'" + pymysql.converters.escape_string(v) + "'"
    sql = ["-- ptest mavzulari (tools/ptest_topics_import.py yaratgan). ptest_import.sql dan KEYIN qo'llang.",
           "SET NAMES utf8mb4;", DDL + ";", "START TRANSACTION;", "DELETE FROM `QuestionTopics`;"]
    for _, b, o, topic, so in links:
        sql.append(f"INSERT INTO `QuestionTopics` (`QuestionId`,`Topic`,`SortOrder`) SELECT `Id`, {esc(topic)}, {so} "
                   f"FROM `Questions` WHERE `Source` = 'ptest' AND `BiletNumber` = {b} AND `BiletOrder` = {o};")
    sql.append("UPDATE `Questions` SET `Topic` = NULL WHERE `Source` = 'ptest';")
    for k, topic in primary.items():
        cond = " OR ".join(f"(`BiletNumber` = {b} AND `BiletOrder` = {o})" for _, b, o in by_key[k])
        sql.append(f"UPDATE `Questions` SET `Topic` = {esc(topic)} WHERE `Source` = 'ptest' AND ({cond});")
    sql.append("-- Eski savollar (faqat mavzular uchun ishlatilgan edi). DIQQAT: ularga tegishli test javoblari va")
    sql.append("-- foydalanuvchi xatolari ham o'chadi (CASCADE).")
    sql.append("DELETE o FROM `Options` o JOIN `Questions` q ON q.Id = o.QuestionId WHERE q.Source = 'old';")
    sql.append("DELETE FROM `Questions` WHERE `Source` = 'old';")
    sql.append("COMMIT;")
    with open(os.path.join(here, "ptest_topics_import.sql"), "w", encoding="utf-8") as f:
        f.write("\n".join(sql) + "\n")

    cur.execute("SELECT Topic, COUNT(*) FROM QuestionTopics GROUP BY Topic")
    per_topic = dict(cur.fetchall())
    cur.execute("SELECT Source, COUNT(*) FROM Questions GROUP BY Source")
    totals = dict(cur.fetchall())
    db.close()

    expected = sum(t["expected_count"] for t in topics_json)
    multi = len(links) - len({qid for qid, *_ in links})
    print(f"🔗 Bog'lanishlar: {len(links)} (ptest'da {expected}; mavzu ichidagi takrorlar bir marta) | "
          f"bir nechta mavzudagi savollar: {multi}")
    print(f"📚 Mavzular: {len(per_topic)}/{len(keys)}")
    for t, topic in zip(topics_json, keys):
        if per_topic.get(topic, 0) != t["expected_count"]:
            print(f"   {t['topic_number']}-mavzu: ilovada {per_topic.get(topic, 0)}, ptest'da {t['expected_count']}")
    if args.delete_old:
        print(f"🗑  O'chirildi: {deleted_q} ta eski savol, {deleted_o} ta variant, {removed_files} ta eski rasm fayli")
    print(f"📊 Bazada: {totals}")
    print(f"💾 SQL: {os.path.join(here, 'ptest_topics_import.sql')}")


if __name__ == "__main__":
    main()
