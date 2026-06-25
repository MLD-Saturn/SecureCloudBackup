using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecureCloudBackup.Migration;

/// <summary>
/// Reads a JSON string token directly into a <c>char[]</c> so a secret (the
/// password) never lands in an un-zeroable <c>string</c>. The UTF-8 bytes of the
/// token are decoded straight into a freshly allocated char buffer; any rented
/// scratch is zeroed before return.
/// </summary>
internal sealed class CharArrayJsonConverter : JsonConverter<char[]>
{
    public override char[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return [];
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected a JSON string for the password.");

        // Copy the raw UTF-8 token bytes into scratch we control, decode to an
        // exact-size char[], then zero the scratch. ValueSpan is valid only while
        // the token is current, so the copy must happen here.
        var byteLength = reader.HasValueSequence
            ? checked((int)reader.ValueSequence.Length)
            : reader.ValueSpan.Length;

        var utf8 = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            int written;
            if (reader.HasValueSequence)
                reader.ValueSequence.CopyTo(utf8);
            else
                reader.ValueSpan.CopyTo(utf8);
            written = byteLength;

            // JSON string tokens may contain escapes; decode any \uXXXX etc.
            // CopyString writes the UNESCAPED characters into a char span.
            var maxChars = Encoding.UTF8.GetCharCount(utf8.AsSpan(0, written));
            var chars = new char[maxChars];
            var actual = reader.CopyString(chars);
            if (actual == chars.Length)
                return chars;

            // Trim to the actual decoded length without leaving a longer copy around.
            var trimmed = new char[actual];
            Array.Copy(chars, trimmed, actual);
            Array.Clear(chars);
            return trimmed;
        }
        finally
        {
            Array.Clear(utf8, 0, byteLength);
            ArrayPool<byte>.Shared.Return(utf8);
        }
    }

    public override void Write(Utf8JsonWriter writer, char[] value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
