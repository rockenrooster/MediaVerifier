using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MediaVerifier.Core.Model;

/// <summary>
/// Evidence serialisation (task M-06): JSON with keys in a stable (ordinal) order, numbers in the
/// invariant format, values limited to numbers, strings, booleans and small arrays of those.
/// </summary>
public static class EvidenceJson
{
    public static string Serialize(IReadOnlyDictionary<string, object>? evidence)
    {
        if (evidence is null || evidence.Count == 0)
        {
            return "{}";
        }

        ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            foreach (string key in evidence.Keys.Order(StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                WriteValue(writer, evidence[key], key);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Reads evidence back. Whole numbers come back as <see cref="long"/>, others as <see cref="double"/>,
    /// arrays as <c>object[]</c>. Malformed JSON gives an empty dictionary.
    /// </summary>
    public static Dictionary<string, object> Deserialize(string? json)
    {
        Dictionary<string, object> result = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                object? value = ReadValue(property.Value);
                if (value is not null)
                {
                    result[property.Name] = value;
                }
            }
        }
        catch (JsonException)
        {
            result.Clear();
        }

        return result;
    }

    /// <summary>Reads a number from evidence whatever numeric type it was stored as.</summary>
    public static bool TryGetDouble(IReadOnlyDictionary<string, object>? evidence, string key, out double value)
    {
        value = 0;
        if (evidence is null || !evidence.TryGetValue(key, out object? raw))
        {
            return false;
        }

        switch (raw)
        {
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case long l:
                value = l;
                return true;
            case int i:
                value = i;
                return true;
            case decimal m:
                value = (double)m;
                return true;
            case IConvertible convertible when raw is not string and not bool:
                value = convertible.ToDouble(CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, string key)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case short sh:
                writer.WriteNumberValue(sh);
                break;
            case byte by:
                writer.WriteNumberValue(by);
                break;
            case uint ui:
                writer.WriteNumberValue(ui);
                break;
            case ulong ul:
                writer.WriteNumberValue(ul);
                break;
            case float f:
                WriteFinite(writer, f, key);
                break;
            case double d:
                WriteFinite(writer, d, key);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case Enum e:
                writer.WriteStringValue(e.ToString());
                break;
            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (object? item in sequence)
                {
                    if (item is IEnumerable and not string)
                    {
                        throw new ArgumentException($"Evidence '{key}' nests arrays; only flat arrays are allowed.");
                    }

                    WriteValue(writer, item, key);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentException($"Evidence '{key}' has unsupported type {value.GetType().Name}.");
        }
    }

    private static void WriteFinite(Utf8JsonWriter writer, double value, string key)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException($"Evidence '{key}' is not a finite number.");
        }

        writer.WriteNumberValue(value);
    }

    private static void WriteFinite(Utf8JsonWriter writer, float value, string key)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentException($"Evidence '{key}' is not a finite number.");
        }

        writer.WriteNumberValue(value);
    }

    private static object? ReadValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out long l) ? (object)l : element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(ReadValue).Where(v => v is not null).ToArray()!,
        _ => null,
    };
}
