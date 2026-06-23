using be_service.Models;

namespace be_service.Abstractions;

public interface IChunkStore
{
    Task InsertChunkAsync(RetrievedChunk chunk);
}