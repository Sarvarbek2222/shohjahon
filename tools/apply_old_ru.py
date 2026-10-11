"""
Eski (Source='old') savollarning ruscha tarjimasi bo'sh joylarini to'ldiradi: tools/ru_translations/old_bank.json
(kalitlar: Q<savol Id>, O<variant Id>). Faqat ruschasi bo'sh maydon yoziladi, tayyor tarjimaga tegilmaydi.

Bundan tashqari tools/old_bank_ru.sql yaratiladi — asosiy bazaga (baza) qo'llash uchun. Undagi har bir UPDATE
savol / variant Id'si bilan birga o'zbekcha matnini ham tekshiradi: Id boshqa savolga tegishli bo'lsa, hech narsa yozilmaydi.

    python tools/apply_old_ru.py
"""

import json
import os
import sys

import pymysql

sys.stdout.reconfigure(encoding="utf-8")
here = os.path.dirname(os.path.abspath(__file__))
project = os.path.dirname(here)
sys.path.insert(0, here)
from ptest_import import parse_conn  # noqa: E402

with open(os.path.join(here, "ru_translations", "old_bank.json"), encoding="utf-8") as f:
    tr = json.load(f)
with open(os.path.join(project, "appsettings.json"), encoding="utf-8-sig") as f:
    conn = parse_conn(json.load(f)["ConnectionStrings"]["DefaultConnection"])

db = pymysql.connect(**conn, autocommit=False)
cur = db.cursor()
cur.execute("SELECT Id, QuestionUZ, QuestionRU FROM Questions WHERE Source = 'old'")
questions = {qid: (uz, ru) for qid, uz, ru in cur.fetchall()}
cur.execute("SELECT o.Id, o.QuestionId, o.OptionUZ, o.OptionRU FROM Options o JOIN Questions q ON q.Id = o.QuestionId WHERE q.Source = 'old'")
options = {oid: (qid, uz, ru) for oid, qid, uz, ru in cur.fetchall()}

missing = [f"Q{i}" for i, (_, ru) in questions.items() if not ru and f"Q{i}" not in tr]
missing += [f"O{i}" for i, (_, _, ru) in options.items() if not ru and f"O{i}" not in tr]
unknown = [k for k in tr if int(k[1:]) not in (questions if k[0] == "Q" else options)]
if missing or unknown:
    sys.exit(f"❌ Tarjimasiz: {missing[:20]} | noma'lum kalit: {unknown[:20]}")

esc = lambda v: "'" + pymysql.converters.escape_string(v) + "'"
sql = ["-- Eski savollar uchun ruscha tarjimalar (tools/apply_old_ru.py yaratgan)", "SET NAMES utf8mb4;", "START TRANSACTION;"]
q_upd = o_upd = 0
try:
    for k, ru in tr.items():
        i = int(k[1:])
        if k[0] == "Q":
            uz, cur_ru = questions[i]
            sql.append(f"UPDATE `Questions` SET `QuestionRU` = {esc(ru)} WHERE `Id` = {i} AND `QuestionUZ` = {esc(uz)} "
                       "AND (`QuestionRU` IS NULL OR `QuestionRU` = '');")
            if not cur_ru:
                cur.execute("UPDATE Questions SET QuestionRU = %s WHERE Id = %s", (ru, i))
                q_upd += 1
        else:
            qid, uz, cur_ru = options[i]
            sql.append(f"UPDATE `Options` SET `OptionRU` = {esc(ru)} WHERE `Id` = {i} AND `QuestionId` = {qid} AND `OptionUZ` = {esc(uz)} "
                       "AND (`OptionRU` IS NULL OR `OptionRU` = '');")
            if not cur_ru:
                cur.execute("UPDATE Options SET OptionRU = %s WHERE Id = %s", (ru, i))
                o_upd += 1
    db.commit()
except Exception:
    db.rollback()
    raise
sql.append("COMMIT;")
with open(os.path.join(here, "old_bank_ru.sql"), "w", encoding="utf-8") as f:
    f.write("\n".join(sql) + "\n")

cur.execute("SELECT SUM(QuestionRU IS NULL OR QuestionRU = '') FROM Questions WHERE Source = 'old'")
left_q = cur.fetchone()[0]
cur.execute("SELECT SUM(o.OptionRU IS NULL OR o.OptionRU = '') FROM Options o JOIN Questions q ON q.Id = o.QuestionId WHERE q.Source = 'old'")
left_o = cur.fetchone()[0]
db.close()
print(f"✅ Yozildi: {q_upd} ta savol, {o_upd} ta variant | eski bazada ruschasiz qoldi: {left_q} savol, {left_o} variant")
print(f"💾 Asosiy baza uchun: {os.path.join(here, 'old_bank_ru.sql')}")
