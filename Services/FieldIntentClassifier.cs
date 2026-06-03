using System.Text.Json;
using be_service.Models;

namespace be_service.Services;

public class FieldIntentClassifier
{
    private readonly OllamaService _ollamaService;
    private readonly ILogger<FieldIntentClassifier> _logger;

    private const string SystemPrompt = """
        Kamu adalah classifier intent untuk sistem RAG internal perusahaan.
        Tugasmu: ekstrak informasi dari query pengguna dan kembalikan JSON saja — tanpa teks tambahan, tanpa markdown fence.

        Schema output (JSON saja):
        {"intent":"field_query|general_query","requested_fields":["fieldKey1",...],"entities":["nama1",...]}

        Daftar field key yang valid:
        SENSITIF: gaji, slip_gaji, penghasilan, rekening, nomor_rekening, npwp, nomor_hp_pribadi, alamat_rumah
        TIDAK TERSEDIA: password, kata_sandi, kredensial, ssh_key, token, api_key
        DIPERBOLEHKAN: nama, nik, divisi, jabatan, shift, status_karyawan, rekap_lembur, jadwal_lembur,
                       kode_maintenance, teknisi, tanggal_maintenance, jadwal_backup, status_server

        Aturan sinonim:
        - "gaji", "upah", "take home pay", "penghasilan", "honor", "slip gaji", "gajinya", "slip gajinya", "berapa dapatnya" → gaji
        - "rekening", "nomor rekening", "no rek", "no rekening" → rekening
        - "password", "kata sandi", "pasword", "sandi" → password
        - "kredensial", "credential", "login" → kredensial
        - "token akses", "access token" → token
        - "api key", "apikey", "secret key" → api_key

        Contoh:
        Q: "berapa gaji sinta lestari" → {"intent":"field_query","requested_fields":["gaji"],"entities":["sinta lestari"]}
        Q: "tolong kasih tau slip gajinya budi" → {"intent":"field_query","requested_fields":["gaji"],"entities":["budi"]}
        Q: "berapa penghasilan karyawan divisi IT" → {"intent":"field_query","requested_fields":["penghasilan"],"entities":[]}
        Q: "apa password server internal" → {"intent":"field_query","requested_fields":["password"],"entities":[]}
        Q: "nomor rekening ani" → {"intent":"field_query","requested_fields":["rekening"],"entities":["ani"]}
        Q: "tampilkan data lembur divisi IT" → {"intent":"general_query","requested_fields":[],"entities":[]}
        Q: "siapa karyawan shift A" → {"intent":"general_query","requested_fields":[],"entities":[]}
        """;

    public FieldIntentClassifier(
        OllamaService ollamaService,
        ILogger<FieldIntentClassifier> logger)
    {
        _ollamaService = ollamaService;
        _logger = logger;
    }

    public async Task<FieldIntentResult> ClassifyAsync(string question)
    {
        try
        {
            var raw = await _ollamaService.CompleteAsync(SystemPrompt, question);
            return ParseResult(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "FieldIntentClassifier failed (queryLength={Length}). Falling back to keyword-only detection.",
                question.Length);

            return FieldIntentResult.Empty();
        }
    }

    private static FieldIntentResult ParseResult(string raw)
    {
        // Extract the first {...} block from anywhere in the response — handles
        // plain text, markdown fences, and models that add preamble text.
        var start = raw.IndexOf('{');
        var end   = raw.LastIndexOf('}');

        if (start < 0 || end <= start)
            return FieldIntentResult.Empty();

        var json = raw[start..(end + 1)];

        using var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new FieldIntentResult
        {
            Intent = root.TryGetProperty("intent", out var intent)
                ? intent.GetString() ?? ""
                : ""
        };

        if (root.TryGetProperty("requested_fields", out var fields))
        {
            result.RequestedFields = fields.EnumerateArray()
                .Select(x => x.GetString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        if (root.TryGetProperty("entities", out var entities))
        {
            result.Entities = entities.EnumerateArray()
                .Select(x => x.GetString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        return result;
    }
}
