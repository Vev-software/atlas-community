using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vev.Atlas.Domain.Sharing;

/// <summary>
/// RFC 8785 (JCS) JSON canonicalization, the form the landscape share digest signature is computed over
/// (see the atlas-contracts landscape digest documentation). Object keys are sorted by UTF-16 code units,
/// there is no whitespace, characters above U+2027 are escaped, and whole numbers are written as integers.
/// A consumer canonicalizes the received <c>digest</c> object the same way before verifying.
/// </summary>
internal static class Jcs
{
    public static byte[] CanonicalizeUtf8(JsonNode node) => Encoding.UTF8.GetBytes(Canonicalize(node));

    public static string Canonicalize(JsonNode node)
    {
        var builder = new StringBuilder();
        Write(node, builder);
        return builder.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var key in obj.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(key, sb);
                    sb.Append(':');
                    Write(obj[key], sb);
                }

                sb.Append('}');
                break;
            case JsonArray array:
                sb.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(array[i], sb);
                }

                sb.Append(']');
                break;
            default:
                WriteScalar(node, sb);
                break;
        }
    }

    private static void WriteScalar(JsonNode node, StringBuilder sb)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        var element = document.RootElement;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.String:
                WriteString(element.GetString()!, sb);
                break;
            case JsonValueKind.Number:
                WriteNumber(element, sb);
                break;
            default:
                throw new InvalidOperationException("Unsupported JSON value in a digest.");
        }
    }

    private static void WriteNumber(JsonElement element, StringBuilder sb)
    {
        if (element.TryGetInt64(out var whole))
        {
            sb.Append(whole.ToString(CultureInfo.InvariantCulture));
            return;
        }

        var number = element.GetDouble();
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            throw new InvalidOperationException("JCS does not support NaN or Infinity.");
        }

        sb.Append(number == Math.Truncate(number) && Math.Abs(number) < 1e15
            ? ((long)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteString(string text, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x2027)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
