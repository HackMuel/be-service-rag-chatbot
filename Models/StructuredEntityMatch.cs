namespace be_service.Models;

public class StructuredEntityMatch
{
    public string FieldName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string RecordType { get; set; } = string.Empty;
    public int Priority { get; set; }
}
