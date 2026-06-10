namespace be_service.Models;

// A single chunk produced by ChunkingService, carrying its provenance so the
// ingestion pipeline can persist chunkType + sectionTitle without re-deriving
// them from content. Kept separate from RetrievedChunk (the storage/retrieval
// DTO) to avoid coupling chunking output to the full payload shape.
public class ContentChunk
{
    public string Content { get; set; } = string.Empty;

    // structured_row | structured_fact | narrative_section | narrative_chunk
    public string ChunkType { get; set; } = string.Empty;

    // Heading for the chunk; empty when none could be detected (caller may then
    // fall back to ChunkMetadataExtractor.ExtractSectionTitle).
    public string SectionTitle { get; set; } = string.Empty;

    public ContentChunk() { }

    public ContentChunk(string content, string chunkType, string sectionTitle = "")
    {
        Content = content;
        ChunkType = chunkType;
        SectionTitle = sectionTitle;
    }
}
