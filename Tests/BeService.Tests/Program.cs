using be_service.Services;

var cases = new (string Name, string Actual, string Expected)[]
{
    ("NormalizeNik RU6-1030", ChunkMetadataExtractor.NormalizeNik("RU6-1030"), "RU6-1030"),
    ("NormalizeNik RU 6-1030", ChunkMetadataExtractor.NormalizeNik("RU 6-1030"), "RU6-1030"),
    ("NormalizeNik RU6 1030", ChunkMetadataExtractor.NormalizeNik("RU6 1030"), "RU6-1030"),
    ("NormalizeNik RU61030", ChunkMetadataExtractor.NormalizeNik("RU61030"), "RU6-1030"),
    ("NormalizeMaintenanceCode MT-308", ChunkMetadataExtractor.NormalizeMaintenanceCode("MT-308"), "MT-308"),
    ("NormalizeMaintenanceCode MT 308", ChunkMetadataExtractor.NormalizeMaintenanceCode("MT 308"), "MT-308"),
    ("NormalizeMaintenanceCode MT308", ChunkMetadataExtractor.NormalizeMaintenanceCode("MT308"), "MT-308")
};

foreach (var testCase in cases)
{
    if (testCase.Actual != testCase.Expected)
    {
        throw new InvalidOperationException(
            $"{testCase.Name} failed. Expected '{testCase.Expected}', got '{testCase.Actual}'.");
    }
}

Console.WriteLine("ChunkMetadataExtractor normalization tests passed.");
