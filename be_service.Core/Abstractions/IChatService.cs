namespace be_service.Abstractions;

public interface IChatService
{
    Task<string> GenerateChatAsync(string prompt );
    Task<string> CompleteAsync(string systemPrompt, string userMessage,
                                double? temperature = null, string? format = null);
}