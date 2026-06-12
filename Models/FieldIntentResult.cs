namespace be_service.Models;

public class FieldIntentResult
{
    public string Intent { get; set; } = "";
    public List<string> RequestedFields { get; set; } = new();
    public List<string> Entities { get; set; } = new();
    public bool HasFieldIntent => RequestedFields.Count > 0;
    public static FieldIntentResult Empty() => new();
}
