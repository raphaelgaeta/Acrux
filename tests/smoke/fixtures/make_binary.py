"""Regenerates the binary fixtures: tipos.parquet (one column per Arrow type
the grid adapter reads) and planilha.xlsx (two sheets). Needs pyarrow.
Usage: python3 make_binary.py <output dir>"""
import sys, zipfile, datetime as dt, decimal
import pyarrow as pa, pyarrow.parquet as pq

out = sys.argv[1]
pq.write_table(pa.table({
    "i8": pa.array([1, -2, None], pa.int8()), "i16": pa.array([300, None, -3], pa.int16()),
    "i32": pa.array([70000, 2, None], pa.int32()), "i64": pa.array([2**40, None, 3], pa.int64()),
    "u8": pa.array([255, 1, None], pa.uint8()), "u32": pa.array([4_000_000_000, None, 1], pa.uint32()),
    "u64": pa.array([2**63, 1, None], pa.uint64()), "f32": pa.array([1.5, None, -2.25], pa.float32()),
    "f64": pa.array([3.5, 1e-9, None], pa.float64()), "bool": pa.array([True, False, None]),
    "texto": pa.array(["São Paulo", "linha 1\nlinha 2", None]), "largo": pa.array(["a", None, "ç"], pa.large_string()),
    "data": pa.array([dt.date(2026, 11, 7), None, dt.date(1999, 1, 1)], pa.date32()),
    "ts": pa.array([dt.datetime(2026, 11, 7, 10, 30), None, dt.datetime(2000, 1, 1)], pa.timestamp("us")),
    "ts_tz": pa.array([dt.datetime(2026, 11, 7, 13, 30, tzinfo=dt.timezone.utc), None, None], pa.timestamp("ms", tz="America/Sao_Paulo")),
    "decimal": pa.array([decimal.Decimal("12.34"), None, decimal.Decimal("-0.50")], pa.decimal128(10, 2)),
    "blob": pa.array([b"\x00\xff", None, b"abc"], pa.binary()),
    "lista": pa.array([[1, 2], None, []], pa.list_(pa.int64())),
    "hora": pa.array([dt.time(10, 30), None, dt.time(23, 59, 59)], pa.time64("us")),
    "duracao": pa.array([dt.timedelta(seconds=90), None, dt.timedelta(days=1)], pa.duration("s")),
}), f"{out}/tipos.parquet")

NS = 'xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"'
REL = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"

def sheet(rows):
    out = []
    for r, vals in enumerate(rows, 1):
        cells = "".join(f'<c r="{chr(65 + i)}{r}"><v>{v}</v></c>' if isinstance(v, (int, float)) else
                        f'<c r="{chr(65 + i)}{r}" t="inlineStr"><is><t>{v}</t></is></c>' for i, v in enumerate(vals))
        out.append(f'<row r="{r}">{cells}</row>')
    return f'<?xml version="1.0" encoding="UTF-8"?><worksheet {NS}><sheetData>{"".join(out)}</sheetData></worksheet>'

with zipfile.ZipFile(f"{out}/planilha.xlsx", "w") as z:
    z.writestr("[Content_Types].xml", '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>')
    z.writestr("_rels/.rels", f'<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{REL}/officeDocument" Target="xl/workbook.xml"/></Relationships>')
    z.writestr("xl/workbook.xml", f'<?xml version="1.0" encoding="UTF-8"?><workbook {NS} xmlns:r="{REL}"><sheets><sheet name="Vendas" sheetId="1" r:id="rId1"/><sheet name="Estoque" sheetId="2" r:id="rId2"/></sheets></workbook>')
    z.writestr("xl/_rels/workbook.xml.rels", f'<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{REL}/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="{REL}/worksheet" Target="worksheets/sheet2.xml"/></Relationships>')
    z.writestr("xl/worksheets/sheet1.xml", sheet([["produto", "qtd"], ["pão de queijo", 3], ["açaí", 5]]))
    z.writestr("xl/worksheets/sheet2.xml", sheet([["item", "saldo"], ["café", 7.5]]))
