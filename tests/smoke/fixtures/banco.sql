-- SQLite fixture for the smoke test; run.sh builds banco.db from it.
-- Shaped like a real agent database (canais/mensagens), plus the cases that
-- once read wrong: any type in any column, NULL first, a name with spaces.
CREATE TABLE canais (id INTEGER PRIMARY KEY, titulo TEXT, ultimo_id INTEGER);
INSERT INTO canais VALUES (1, 'Ofertas do Dia', 120), (2, 'Cupons & Promoções', 87);

CREATE TABLE mensagens (canal_id INTEGER, msg_id INTEGER, data TEXT, texto TEXT, links TEXT);
INSERT INTO mensagens VALUES
  (1, 118, '2026-11-07T19:47:04+00:00', 'Fone 🔥 por R$ 99,90', '["https://example.com/a"]'),
  (1, 119, '2026-11-08T02:10:00+00:00', 'Café em grão 1 kg', NULL),
  (2, 86,  '2020-01-01T12:00:00+00:00', 'Cupom antigo', '[]'),
  (2, 87,  '2999-01-01T00:00:00+00:00', 'Data no futuro: o --last a deixa de fora', '[]');

-- a stray text or real in an INTEGER column stays text, never becomes 0
CREATE TABLE misturada (id INTEGER, codigo INTEGER);
INSERT INTO misturada VALUES (1, 10), (2, 20), (3, 'abc'), (4, 3.5), (5, NULL);

-- the first row is NULL: the ADO.NET reader would type these by it
CREATE TABLE bordas (c1 INTEGER, c2 BLOB, c3 REAL);
INSERT INTO bordas VALUES (NULL, NULL, NULL), ('def', x'00ff', 2), (7, x'01', 1.5);

CREATE TABLE "itens do pedido" (pedido_id INTEGER, item TEXT);
INSERT INTO "itens do pedido" VALUES (1, 'açaí'), (1, 'pão de queijo'), (2, 'café');

CREATE TABLE vazia (id INTEGER, nome TEXT);

CREATE VIEW v_por_canal AS SELECT canal_id, COUNT(*) AS n FROM mensagens GROUP BY canal_id;
