"""Writes the messy files of the benchmark, and expected.json with the truth
about every column: its kind and a check on its values (a sum, the sum of
the days since 1970 for dates, the count of a text with accents), so a tool
that reads a column wrong without a word is caught.

  vendas_br.csv  2M rows, the way Excel saves CSV on a Portuguese-language
                 Windows: Windows-1252, ';', decimal commas, day/month dates
                 (the first one, 03/02/2026, reads both ways), CRLF. "desconto"
                 holds whole numbers for 200,000 rows, then decimals too.
  orders_us.csv  1M rows, UTF-8 from a US system: month/day dates. order_date
                 proves its order (days past 12); ship_date never has a day
                 past 12, so only the source knows it is month/day.
  invoices_us.csv 500,000 rows, UTF-8 from a US system, whose only date column
                 never has a day past 12: nothing in the file proves the order.
  loja.db        SQLite, 300,000 rows. SQLite lets a column hold any type:
                 "preco" (REAL) and "codigo" (INTEGER) hold some text, "codigo"
                 starts with a NULL, "obs" has no declared type, and "quando"
                 is ISO 8601 text with a UTC offset.
Fixed seed. Usage: python3 make_messy_files.py <out dir>"""
import json, os, random, sqlite3, sys
from datetime import date, datetime, timedelta, timezone

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
rng = random.Random(20261111)
EPOCH = date(1970, 1, 1)
expected = {}

# --- vendas_br.csv -------------------------------------------------------
CITIES = ["São Paulo", "Belém", "Florianópolis", "Goiânia", "Ribeirão Preto", "Curitiba"]
PRODUCTS = ["Café", "Pão de queijo", "Açaí", "Guaraná", "Coxinha", "Pastel"]
D0, D1 = date(2025, 1, 1), date(2026, 10, 31)
rows = 2_000_000
lines = ["data;cidade;produto;quantidade;valor;desconto"]
days = qty = cents = tenths = sp = pq = 0
for i in range(rows):
    d = date(2026, 2, 3) if i == 0 else D0 if i == 1 else D1 if i == 2 else D0 + timedelta(rng.randrange((D1 - D0).days + 1))
    city, product = rng.choice(CITIES), rng.choice(PRODUCTS)
    q, c = rng.randint(1, 9), rng.randint(100, 999_999)
    t = rng.randint(0, 20) * 10 + (5 if i >= 200_000 and rng.random() < 0.1 else 0)   # tenths of a percent
    lines.append(f"{d:%d/%m/%Y};{city};{product};{q};{c // 100},{c % 100:02d};{t // 10}" + (f",{t % 10}" if t % 10 else ""))
    days += (d - EPOCH).days; qty += q; cents += c; tenths += t
    sp += city == "São Paulo"; pq += product == "Pão de queijo"
with open(os.path.join(out, "vendas_br.csv"), "wb") as f:
    f.write(("\r\n".join(lines) + "\r\n").encode("cp1252"))
expected["vendas_br.csv"] = {"rows": rows, "columns": {
    "data": {"kind": "date", "days": days},
    "cidade": {"kind": "text", "sentinel": "São Paulo", "count": sp},
    "produto": {"kind": "text", "sentinel": "Pão de queijo", "count": pq},
    "quantidade": {"kind": "number", "sum": qty},
    "valor": {"kind": "number", "sum": cents / 100},
    "desconto": {"kind": "number", "sum": tenths / 10},
}}

# --- orders_us.csv -------------------------------------------------------
CUSTOMERS = ["Acme Corp", "Globex", "Initech", "Umbrella", "Stark Industries"]
rows = 1_000_000
lines = ["order_date,ship_date,customer,qty,amount"]
odays = sdays = qty = cents = acme = 0
for i in range(rows):
    o = D0 + timedelta(rng.randrange((D1 - D0).days + 1))
    s = date(2026, rng.randint(1, 12), rng.randint(1, 12))   # never a day past 12
    customer, q, c = rng.choice(CUSTOMERS), rng.randint(1, 50), rng.randint(100, 999_999)
    lines.append(f"{o:%m/%d/%Y},{s:%m/%d/%Y},{customer},{q},{c // 100}.{c % 100:02d}")
    odays += (o - EPOCH).days; sdays += (s - EPOCH).days; qty += q; cents += c; acme += customer == "Acme Corp"
with open(os.path.join(out, "orders_us.csv"), "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(lines) + "\n")
expected["orders_us.csv"] = {"rows": rows, "columns": {
    "order_date": {"kind": "date", "days": odays},
    "ship_date": {"kind": "date", "days": sdays, "ambiguous": True},
    "customer": {"kind": "text", "sentinel": "Acme Corp", "count": acme},
    "qty": {"kind": "number", "sum": qty},
    "amount": {"kind": "number", "sum": cents / 100},
}}

# --- invoices_us.csv -----------------------------------------------------
rows = 500_000
lines = ["invoice_date,customer,total"]
idays = cents = acme = 0
for i in range(rows):
    d = date(2026, rng.randint(1, 12), rng.randint(1, 12))
    customer, c = rng.choice(CUSTOMERS), rng.randint(100, 999_999)
    lines.append(f"{d:%m/%d/%Y},{customer},{c // 100}.{c % 100:02d}")
    idays += (d - EPOCH).days; cents += c; acme += customer == "Acme Corp"
with open(os.path.join(out, "invoices_us.csv"), "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(lines) + "\n")
expected["invoices_us.csv"] = {"rows": rows, "columns": {
    "invoice_date": {"kind": "date", "days": idays, "ambiguous": True},
    "customer": {"kind": "text", "sentinel": "Acme Corp", "count": acme},
    "total": {"kind": "number", "sum": cents / 100},
}}

# --- loja.db -------------------------------------------------------------
path = os.path.join(out, "loja.db")
if os.path.exists(path):
    os.remove(path)
con = sqlite3.connect(path)
con.execute("CREATE TABLE vendas (id INTEGER PRIMARY KEY, preco REAL, codigo INTEGER, obs, quando TEXT)")
rows, data = 300_000, []
consulta = abc = obs_text = secs = 0
for i in range(rows):
    preco = "sob consulta" if rng.random() < 0.01 else round(rng.uniform(1, 999), 2)
    codigo = None if i == 0 else "abc" if rng.random() < 0.005 else rng.randint(1000, 9999)
    obs = "ok" if rng.random() < 0.5 else rng.randint(1, 99)
    when = datetime(2026, 1, 1, tzinfo=timezone(timedelta(hours=-3))) + timedelta(minutes=rng.randrange(400_000))
    data.append((i + 1, preco, codigo, obs, when.isoformat()))
    consulta += preco == "sob consulta"; abc += codigo == "abc"; obs_text += obs == "ok"
    secs += int(when.timestamp())
con.executemany("INSERT INTO vendas VALUES (?, ?, ?, ?, ?)", data)
con.commit(); con.close()
expected["loja.db"] = {"rows": rows, "table": "vendas", "columns": {
    "id": {"kind": "number", "sum": rows * (rows + 1) // 2},
    "preco": {"kind": "mixed", "sentinel": "sob consulta", "count": consulta, "nonnull": rows},
    "codigo": {"kind": "mixed", "sentinel": "abc", "count": abc, "nonnull": rows - 1},
    "obs": {"kind": "mixed", "sentinel": "ok", "count": obs_text, "nonnull": rows},
    "quando": {"kind": "instant", "seconds": secs},
}}

json.dump(expected, open(os.path.join(out, "expected.json"), "w"), indent=1, ensure_ascii=False)
for name in expected:
    print(f"{name}: {os.path.getsize(os.path.join(out, name)) / 1e6:.0f} MB")
