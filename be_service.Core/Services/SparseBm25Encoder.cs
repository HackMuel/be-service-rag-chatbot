using System.Text.RegularExpressions;

namespace be_service.Services;

public class SparseBm25Encoder
{
    // FNV-1a hash space — 2^20 buckets minimises collisions for a small corpus
    // while keeping memory bounded. Sparse index space is not a fixed dimension
    // limit in Qdrant, so any uint value is valid.
    private const uint HashSpace = 1u << 20;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Indonesian function words
        "yang", "dan", "di", "ke", "dari", "ini", "itu", "ada", "tidak",
        "dengan", "untuk", "pada", "dalam", "adalah", "akan", "juga",
        "atau", "saya", "kami", "kamu", "mereka", "jika", "oleh",
        "dapat", "bisa", "sudah", "belum", "pun", "apa", "saja",
        "telah", "jadi", "hal", "cara", "dan", "ber", "ter",
        // English function words
        "the", "and", "for", "are", "was", "not", "but", "they",
        "this", "that", "with", "from", "have", "been"
    };

    // Encodes text to a sparse TF-weighted vector.
    // Returns {tokenIndex: normalizedTF} — values in (0, 1].
    // Hash collisions accumulate weights, which is intentional:
    // colliding tokens are rare and the combined weight is still informative.
    public Dictionary<uint, float> Encode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new Dictionary<uint, float>();

        var tokens = Tokenize(text);
        if (tokens.Count == 0)
            return new Dictionary<uint, float>();

        var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
            tf[token] = tf.GetValueOrDefault(token) + 1;

        var docLength = (float)tokens.Count;
        var result = new Dictionary<uint, float>(tf.Count);

        foreach (var (token, count) in tf)
        {
            var index = FnvHash(token) % HashSpace;
            result[index] = result.GetValueOrDefault(index) + count / docLength;
        }

        return result;
    }

    private static List<string> Tokenize(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"\b[a-z0-9]{3,}\b")
            .Select(m => m.Value)
            .Where(t => !StopWords.Contains(t))
            .ToList();
    }

    // FNV-1a: deterministic across runs, unlike string.GetHashCode()
    private static uint FnvHash(string s)
    {
        uint hash = 2166136261u;
        foreach (var c in s)
        {
            hash ^= (uint)c;
            hash *= 16777619u;
        }
        return hash;
    }
}
