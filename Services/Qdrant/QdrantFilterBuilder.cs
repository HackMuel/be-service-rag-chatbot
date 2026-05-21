namespace be_service.Services;

public class QdrantFilterBuilder
{
    public object BuildPayloadFilter(Dictionary<string, string> filters)
    {
        var must = filters
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) &&
                        !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new
            {
                key = x.Key,
                match = new
                {
                    value = x.Value
                }
            })
            .ToArray();

        return new
        {
            must
        };
    }

    public object BuildPayloadFilter(string fieldName, string value)
    {
        return BuildPayloadFilter(new Dictionary<string, string>
        {
            [fieldName] = value
        });
    }

    public bool HasAnyCondition(Dictionary<string, string> filters)
    {
        return filters.Any(x => !string.IsNullOrWhiteSpace(x.Key) &&
                                !string.IsNullOrWhiteSpace(x.Value));
    }
}
