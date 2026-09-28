using System.Net.Http.Headers;
using System.Text;

namespace DocuEngAIne.Infrastructure.Llm;

/// <summary>
/// Formats HTTP headers for logs with Authorization and API-key values replaced.
/// </summary>
public static class LlmLogRedaction
{
    public const string Redacted = "[redacted]";

    public static string Describe(HttpRequestHeaders headers)
    {
        var builder = new StringBuilder();
        foreach (var header in headers)
        {
            if (builder.Length > 0)
                builder.Append(' ');

            builder.Append(header.Key);
            builder.Append('=');
            builder.Append(IsSensitive(header.Key) ? RedactValue(header.Key, header.Value) : string.Join(',', header.Value));
        }

        return builder.ToString();
    }

    public static bool ContainsSecret(string text, string? secret)
        => !string.IsNullOrEmpty(secret) && text.Contains(secret, StringComparison.Ordinal);

    private static bool IsSensitive(string name)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("api-key", StringComparison.OrdinalIgnoreCase);

    private static string RedactValue(string name, IEnumerable<string> values)
    {
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            var first = values.FirstOrDefault() ?? string.Empty;
            var schemeEnd = first.IndexOf(' ');
            var scheme = schemeEnd > 0 ? first[..schemeEnd] : "Bearer";
            return $"{scheme} {Redacted}";
        }

        return Redacted;
    }
}
