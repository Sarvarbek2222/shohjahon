"""
Qo'lda qilingan ruscha tarjimalarni (tools/ru_translations/*.json, kalitlar: Q<savol Id>, O<variant Id> —
joriy bazadagi Id'lar; qo'llanilgan fayllar ru_translations/applied ga o'tkaziladi)
  1) bazadagi ruscha matni bo'sh ptest savollari va variantlariga yozadi;
  2) Id ga bog'liq bo'lmagan ko'rinishda tools/ptest_ru.json ga saqlaydi (bilet + savol raqami + variant matni) —
     ptest_import.py boshqa serverda import qilganda shu fayldan foydalanadi.

    python tools/apply_ru.py
"""

import glob
import json
import os
import sys

import pymysql

sys.stdout.reconfigure(encoding="utf-8")
here = os.path.dirname(os.path.abspath(__file__))
project = os.path.dirname(here)

sys.path.insert(0, here)
from ptest_import import parse_conn, key, question_key  # noqa: E402

tr = {}
for p in sorted(glob.glob(os.path.join(here, "ru_translations", "*.json"))):
    with open(p, encoding="utf-8") as f:
        tr.update(json.load(f))

with open(os.path.join(project, "appsettings.json"), encoding="utf-8-sig") as f:
    conn = parse_conn(json.load(f)["ConnectionStrings"]["DefaultConnection"])
db = pymysql.connect(**conn, autocommit=False)
cur = db.cursor()

cur.execute("SELECT Id, BiletNumber, BiletOrder, QuestionRU, QuestionUZ FROM Questions WHERE Source = 'ptest'")
rows_q = cur.fetchall()
questions = {qid: (b, o, ru) for qid, b, o, ru, _ in rows_q}
question_uz = {qid: uz for qid, _, _, _, uz in rows_q}
cur.execute("SELECT o.Id, o.QuestionId, o.OptionUZ, o.OptionRU FROM Options o JOIN Questions q ON q.Id = o.QuestionId "
            "WHERE q.Source = 'ptest' ORDER BY o.Id")
options = cur.fetchall()

# --- tekshiruv: tarjimasiz qolgan yoki ortiqcha kalit bo'lmasin
missing = [f"Q{qid}" for qid, (_, _, ru) in questions.items() if not ru and f"Q{qid}" not in tr]
missing += [f"O{oid}" for oid, qid, _, ru in options if not ru and f"O{oid}" not in tr]
unknown = [k for k in tr if (k[0] == "Q" and int(k[1:]) not in questions) or
           (k[0] == "O" and int(k[1:]) not in {o[0] for o in options})]
empty = [k for k, v in tr.items() if not str(v).strip()]
if missing or unknown or empty:
    sys.exit(f"❌ Tarjimasiz: {missing[:20]} | noma'lum kalit: {unknown[:20]} | bo'sh: {empty[:20]}")

q_upd = o_upd = 0
try:
    for qid, (_, _, ru) in questions.items():
        if not ru and f"Q{qid}" in tr:
            cur.execute("UPDATE Questions SET QuestionRU = %s WHERE Id = %s", (tr[f"Q{qid}"], qid))
            q_upd += 1
    for oid, qid, uz, ru in options:
        if not ru and f"O{oid}" in tr:
            cur.execute("UPDATE Options SET OptionRU = %s WHERE Id = %s", (tr[f"O{oid}"], oid))
            o_upd += 1
    db.commit()
except Exception:
    db.rollback()
    raise

# --- Id ga bog'liq bo'lmagan nusxa (import uchun). Kalit — savol matni + variantlar to'plami,
#     variant tarjimalari — variant matni bo'yicha: ptest'da savollar va variantlar tartibi har safar o'zgaradi.
ru_path = os.path.join(here, "ptest_ru.json")
portable = {}
if os.path.exists(ru_path):
    with open(ru_path, encoding="utf-8") as f:
        portable = json.load(f)

current_keys = set()
for qid, (b, o, _) in questions.items():
    q_opts = [(oid, uz, ru) for oid, oq, uz, ru in options if oq == qid]
    qk = question_key(question_uz[qid], [uz for _, uz, _ in q_opts])
    current_keys.add(qk)
    if f"Q{qid}" not in tr and not any(f"O{oid}" in tr for oid, _, _ in q_opts):
        continue
    entry = portable.get(qk, {"uz": question_uz[qid], "options": {}})
    if f"Q{qid}" in tr:
        entry["question"] = tr[f"Q{qid}"]
    for oid, uz, ru in q_opts:
        if tr.get(f"O{oid}") or ru:
            entry.setdefault("options", {})[key(uz)] = tr.get(f"O{oid}") or ru
    portable[qk] = entry

# Bazada endi yo'q savollar uchun yozuvlar (masalan, xato yig'ilgan eski 20-savollar) olib tashlanadi
stale = [k for k in portable if k not in current_keys]
for k in stale:
    del portable[k]
print(f"🧹 Eskirgan tarjima yozuvlari olib tashlandi: {len(stale)}")
with open(ru_path, "w", encoding="utf-8") as f:
    json.dump(portable, f, ensure_ascii=False, indent=1, sort_keys=True)

cur.execute("SELECT SUM(QuestionRU IS NULL OR QuestionRU = '') FROM Questions WHERE Source = 'ptest'")
left_q = cur.fetchone()[0]
cur.execute("SELECT SUM(o.OptionRU IS NULL OR o.OptionRU = '') FROM Options o JOIN Questions q ON q.Id = o.QuestionId WHERE q.Source = 'ptest'")
left_o = cur.fetchone()[0]
db.close()
print(f"✅ Yozildi: {q_upd} ta savol, {o_upd} ta variant | ruschasiz qoldi: {left_q} savol, {left_o} variant")
print(f"💾 {os.path.join(here, 'ptest_ru.json')} ({len(portable)} ta savol)")
