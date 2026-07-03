using Polars.CSharp;

namespace PolarsGridViewer;

public static class DataFrameProvider
{
    /// <summary>Filtro do diálogo de abertura (parquet + csv).</summary>
    public const string OpenDialogFilter =
        "Arquivos de dados (*.parquet;*.csv)|*.parquet;*.csv|" +
        "Arquivos Parquet (*.parquet)|*.parquet|" +
        "Arquivos CSV (*.csv)|*.csv|" +
        "Todos os arquivos (*.*)|*.*";

    /// <summary>
    /// Garante uma fonte parquet: .parquet passa direto; .csv é convertido uma
    /// única vez para um parquet temporário (ScanCsv → SinkParquet, streaming,
    /// sem materializar na RAM) e o app segue operando 100% sobre parquet —
    /// preservando o princípio de re-scan barato dos filtros e scripts.
    /// Retorna o caminho da fonte e se ela é temporária (chamador apaga).
    /// </summary>
    // Amostras para a inferência de tipos do CSV. O padrão do Polars (100
    // linhas) erra fácil (int que vira float na linha 200 → "could not parse");
    // 10k custa ~centenas de ms em CSV de 100 MB (medido). Se ainda assim
    // errar, re-tentamos com 1M — caro (~15 s/100 MB), mas só paga quem
    // precisa. NÃO usar ulong.MaxValue: "capacity overflow" nativo (validado).
    private const ulong InferSchemaRows = 10_000;
    private const ulong InferSchemaRowsRetry = 1_000_000;

    public static async Task<(string ParquetPath, bool IsTemp)> EnsureParquetAsync(
        string path, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Arquivo não encontrado.", path);

        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return (path, false);

        return await Task.Run(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "PolarsGridViewer");
            Directory.CreateDirectory(tempDir);

            // O leitor CSV do Polars exige UTF-8. Excel salvo como "CSV" simples
            // usa Windows-1252 (ç/ã viram "invalid utf-8 sequence"); o
            // CsvEncoding.LossyUTF8 leria, mas destrói os acentos (validado por
            // probe) — então transcodificamos para um CSV UTF-8 temporário.
            var utf8Csv = IsValidUtf8(path) ? path : TranscodeToUtf8(path, tempDir);
            try
            {
                var separator = DetectCsvSeparator(utf8Csv);
                var decimalComma = DetectDecimalComma(utf8Csv, separator);
                var baseName = Path.GetFileNameWithoutExtension(path);
                try
                {
                    return (ConvertCsv(utf8Csv, separator, decimalComma, InferSchemaRows, tempDir, baseName), true);
                }
                catch (Exception ex) when (
                    ex.Message.Contains("could not parse") ||
                    ex.Message.Contains("invalid primitive value"))
                {
                    // A amostra errou um tipo (ex.: coluna int que vira float
                    // adiante) — re-tenta com amostra muito maior. O valor que
                    // falhou vem na mensagem: se ele contradiz a convenção
                    // decimal escolhida (ex.: `8.1` com decimalComma ligado),
                    // inverte — senão a coluna viraria string SILENCIOSAMENTE.
                    var offender = System.Text.RegularExpressions.Regex
                        .Match(ex.Message, "could not parse `\"?([^`\"]+)\"?`").Groups[1].Value;
                    if (decimalComma && System.Text.RegularExpressions.Regex.IsMatch(offender, @"^-?\d+\.\d+$"))
                        decimalComma = false;
                    else if (!decimalComma && System.Text.RegularExpressions.Regex.IsMatch(offender, @"^-?\d+,\d+$"))
                        decimalComma = true;

                    progress?.Report("Refinando tipos do CSV (amostra ampliada)...");
                    return (ConvertCsv(utf8Csv, separator, decimalComma, InferSchemaRowsRetry, tempDir, baseName), true);
                }
            }
            finally
            {
                if (!ReferenceEquals(utf8Csv, path))
                    File.Delete(utf8Csv);
            }
        }, cancellationToken);
    }

    private static string ConvertCsv(
        string utf8Csv, char separator, bool decimalComma, ulong inferRows, string tempDir, string baseName)
    {
        var temp = Path.Combine(tempDir, $"{baseName}_{Guid.NewGuid():N}.parquet");
        try
        {
            using var lf = LazyFrame.ScanCsv(
                utf8Csv, separator: separator, decimalComma: decimalComma,
                tryParseDates: true, inferSchemaLength: inferRows);
            lf.SinkParquet(temp);
            return temp;
        }
        catch
        {
            // sink pode deixar parquet parcial/vazio para trás (o nativo tenta
            // um "fallback to Eager Write" antes de lançar)
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Fareja a convenção decimal nos dados (não deduzir do separador: existe
    /// CSV com `;` e ponto decimal — e com decimalComma errado a coluna vira
    /// string SILENCIOSAMENTE). Conta campos "12,34" vs "12.34" em ~200 linhas;
    /// maioria decide, empate cai na convenção do separador (`;` → vírgula).
    /// Separador `,` nunca tem decimal vírgula sem aspas → sempre false.
    /// </summary>
    private static bool DetectDecimalComma(string path, char separator)
    {
        if (separator == ',')
            return false;

        int comma = 0, dot = 0;
        foreach (var line in File.ReadLines(path).Skip(1).Take(200))
        {
            foreach (var field in line.Split(separator))
            {
                var f = field.Trim().Trim('"');
                if (System.Text.RegularExpressions.Regex.IsMatch(f, @"^-?\d+,\d+$"))
                    comma++;
                else if (System.Text.RegularExpressions.Regex.IsMatch(f, @"^-?\d+\.\d+$"))
                    dot++;
            }
        }

        if (comma != dot)
            return comma > dot;
        return separator == ';';
    }

    /// <summary>
    /// Valida uma amostra do arquivo como UTF-8 (4 MB bastam: o primeiro
    /// acento em cp1252 já falha). Recua até 3 bytes no fim da amostra para
    /// não condenar um caractere multi-byte cortado ao meio.
    /// </summary>
    private static bool IsValidUtf8(string path)
    {
        using var fs = File.OpenRead(path);
        var buffer = new byte[Math.Min(4 * 1024 * 1024, fs.Length)];
        int len = fs.Read(buffer, 0, buffer.Length);

        if (len == buffer.Length && len > 3)
        {
            int end = len;
            while (end > 0 && (buffer[end - 1] & 0b1100_0000) == 0b1000_0000)
                end--;                        // continuações de um char cortado
            if (end > 0 && buffer[end - 1] >= 0b1100_0000)
                end--;                        // o byte líder do char cortado
            len = end;
        }

        return System.Text.Unicode.Utf8.IsValid(buffer.AsSpan(0, len));
    }

    /// <summary>
    /// Reescreve o CSV como UTF-8, em streaming. BOM UTF-16 é honrado pelo
    /// StreamReader; sem BOM, assume Windows-1252 (o "ANSI" do Excel pt-BR).
    /// </summary>
    private static string TranscodeToUtf8(string path, string tempDir)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var fallback = System.Text.Encoding.GetEncoding(1252);

        var temp = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(path)}_{Guid.NewGuid():N}_utf8.csv");
        using var reader = new StreamReader(path, fallback, detectEncodingFromByteOrderMarks: true);
        using var writer = new StreamWriter(temp, append: false, new System.Text.UTF8Encoding(false));

        var buffer = new char[64 * 1024];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            writer.Write(buffer, 0, read);
        return temp;
    }

    /// <summary>Heurística: separador com mais ocorrências na 1ª linha.</summary>
    private static char DetectCsvSeparator(string path)
    {
        var header = File.ReadLines(path).FirstOrDefault() ?? "";
        var best = new[] { ';', ',', '\t' }
            .Select(c => (Sep: c, Count: header.Count(ch => ch == c)))
            .OrderByDescending(t => t.Count)
            .First();
        return best.Count > 0 ? best.Sep : ',';
    }

    /// <summary>
    /// Lê apenas o schema do parquet (metadados — não carrega dados).
    /// </summary>
    public static async Task<string[]> GetColumnNamesAsync(string parquetPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Arquivo .parquet não encontrado.", parquetPath);

        return await Task.Run(() =>
        {
            using var lf = LazyFrame.ScanParquet(parquetPath);
            return lf.Schema.ToFrozenDictionary().Keys.ToArray();
        }, cancellationToken);
    }

    /// <summary>
    /// Coleta do parquet apenas as colunas pedidas (projection pushdown:
    /// o Polars só lê do disco as colunas do Select).
    /// </summary>
    public static async Task<DataFrame> GetDataFrameAsync(string parquetPath, string[] columns, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Arquivo .parquet não encontrado.", parquetPath);

        return await Task.Run(() =>
        {
            using var lf = LazyFrame.ScanParquet(parquetPath).Select(columns);
            return lf.Collect();
        }, cancellationToken);
    }
}
