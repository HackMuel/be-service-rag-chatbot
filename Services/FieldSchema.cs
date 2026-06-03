namespace be_service.Services;

public enum FieldAccess
{
    Allowed,
    Sensitive,
    Unavailable
}

public record FieldDefinition(string Domain, FieldAccess Access);

public static class FieldSchema
{
    private static readonly Dictionary<string, FieldDefinition> _fields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Karyawan — Allowed
            ["nama"]               = new("karyawan", FieldAccess.Allowed),
            ["nik"]                = new("karyawan", FieldAccess.Allowed),
            ["divisi"]             = new("karyawan", FieldAccess.Allowed),
            ["jabatan"]            = new("karyawan", FieldAccess.Allowed),
            ["posisi"]             = new("karyawan", FieldAccess.Allowed),
            ["shift"]              = new("karyawan", FieldAccess.Allowed),
            ["status_karyawan"]    = new("karyawan", FieldAccess.Allowed),
            ["email_kantor"]       = new("karyawan", FieldAccess.Allowed),
            ["jadwal_lembur"]      = new("karyawan", FieldAccess.Allowed),
            ["rekap_lembur"]       = new("karyawan", FieldAccess.Allowed),

            // Karyawan — Sensitive
            ["gaji"]              = new("karyawan", FieldAccess.Sensitive),
            ["slip_gaji"]         = new("karyawan", FieldAccess.Sensitive),
            ["penghasilan"]       = new("karyawan", FieldAccess.Sensitive),
            ["rekening"]          = new("karyawan", FieldAccess.Sensitive),
            ["nomor_rekening"]    = new("karyawan", FieldAccess.Sensitive),
            ["npwp"]              = new("karyawan", FieldAccess.Sensitive),
            ["nomor_hp_pribadi"]  = new("karyawan", FieldAccess.Sensitive),
            ["alamat_rumah"]      = new("karyawan", FieldAccess.Sensitive),

            // Server — Allowed
            ["jadwal_backup"]     = new("server", FieldAccess.Allowed),
            ["ip_address"]        = new("server", FieldAccess.Allowed),
            ["status_server"]     = new("server", FieldAccess.Allowed),

            // Server — Unavailable
            ["password"]          = new("server", FieldAccess.Unavailable),
            ["kata_sandi"]        = new("server", FieldAccess.Unavailable),
            ["kredensial"]        = new("server", FieldAccess.Unavailable),
            ["ssh_key"]           = new("server", FieldAccess.Unavailable),
            ["token"]             = new("server", FieldAccess.Unavailable),
            ["api_key"]           = new("server", FieldAccess.Unavailable),

            // Maintenance — Allowed
            ["kode_maintenance"]    = new("maintenance", FieldAccess.Allowed),
            ["status_maintenance"]  = new("maintenance", FieldAccess.Allowed),
            ["lokasi_maintenance"]  = new("maintenance", FieldAccess.Allowed),
            ["teknisi"]             = new("maintenance", FieldAccess.Allowed),
            ["tanggal_maintenance"] = new("maintenance", FieldAccess.Allowed),
        };

    public static FieldAccess GetAccess(string fieldKey)
    {
        return _fields.TryGetValue(fieldKey, out var def)
            ? def.Access
            : FieldAccess.Unavailable;
    }
}

public static class FieldKeywordMap
{
    private static readonly (string[] Keywords, string FieldKey)[] _map =
    {
        (new[] { "gaji", "slip gaji", "take home pay", "upah", "penghasilan", "honor" }, "gaji"),
        (new[] { "rekening", "nomor rekening", "no rekening" }, "rekening"),
        (new[] { "npwp" }, "npwp"),
        (new[] { "nomor hp", "hp pribadi", "nomor handphone" }, "nomor_hp_pribadi"),
        (new[] { "alamat rumah", "alamat tinggal" }, "alamat_rumah"),
        (new[] { "password", "kata sandi", "pasword" }, "password"),
        (new[] { "kredensial", "credential" }, "kredensial"),
        (new[] { "ssh key", "private key" }, "ssh_key"),
        (new[] { "token akses", "access token" }, "token"),
        (new[] { "api key", "apikey", "secret" }, "api_key"),
    };

    public static List<string> ExtractFieldKeys(string question)
    {
        var result = new List<string>();

        foreach (var (keywords, fieldKey) in _map)
        {
            if (keywords.Any(k => question.Contains(k, StringComparison.OrdinalIgnoreCase)))
                result.Add(fieldKey);
        }

        return result;
    }
}

public class FieldValidationResult
{
    public bool IsBlocked { get; init; }
    public string BlockReason { get; init; } = string.Empty;

    public static FieldValidationResult Pass() => new() { IsBlocked = false };

    public static FieldValidationResult Block(string reason) =>
        new() { IsBlocked = true, BlockReason = reason };
}
