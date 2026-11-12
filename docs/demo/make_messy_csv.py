"""Writes vendas.csv the way Excel saves a sheet as CSV on a Portuguese-language
Windows: Windows-1252, ';' between fields, decimal commas, day/month dates and
CRLF line ends. The first date (03/02/2026) reads both ways. Fixed seed: every
run writes the same file.
Usage: python3 make_messy_csv.py <out dir>"""
import os, random, sys

random.seed(1)
CITIES = ["São Paulo", "Belém", "Florianópolis", "Goiânia", "Ribeirão Preto"]
PRODUCTS = ["Café", "Pão de queijo", "Açaí", "Guaraná"]

lines = ["data;cidade;produto;quantidade;valor"]
for i in range(20_000):
    day, month = random.randint(1, 28), random.randint(1, 10)
    if i == 0:
        day, month = 3, 2
    city, product = random.choice(CITIES), random.choice(PRODUCTS)
    quantity, value = random.randint(1, 9), random.randint(100, 99_999) / 100
    lines.append(f"{day:02d}/{month:02d}/2026;{city};{product};{quantity};{value:.2f}".replace(".", ","))

path = os.path.join(sys.argv[1], "vendas.csv")
with open(path, "wb") as f:
    f.write(("\r\n".join(lines) + "\r\n").encode("cp1252"))
print(path)
