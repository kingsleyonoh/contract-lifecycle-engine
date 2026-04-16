using System.Globalization;
using System.Text;

namespace ContractEngine.Core.Pagination;

/// <summary>
/// Encodes the composite <c>(CreatedAt, Id)</c> cursor into the opaque base64 token returned to
/// clients in the pagination envelope (<c>CODEBASE_CONTEXT.md</c> Key Patterns §2; PRD §8b).
///
/// Wire format: base64(UTF-8 bytes of <c>{created_at_iso}|{id_guid}</c>). We chose pipe-delimited
/// text over JSON because (a) the payload is fixed-shape and doesn't benefit from JSON's
/// flexibility, (b) it's half the size on the wire, and (c) it's trivial to parse back without
/// a JSON dependency in Core.
/// </summary>
public static class PaginationCursor
{
    /// <summary>
    /// Encodes a <paramref name="createdAt"/> / <paramref name="id"/> pair into an opaque cursor
    /// string. The returned value is standard base64 (with padding); callers embed it in the
    /// <c>pagination.next_cursor</c> response field as-is.
    /// </summary>
    public static string Encode(DateTime createdAt, Guid id)
    {
        // Normalise to UTC ISO-8601 with round-trip precision so decode returns the same instant.
        var utc = createdAt.Kind == DateTimeKind.Utc
            ? createdAt
            : DateTime.SpecifyKind(createdAt, DateTimeKind.Utc);
        var payload = $"{utc.ToString("O", CultureInfo.InvariantCulture)}|{id:D}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Attempts to decode an opaque cursor back into the <c>(CreatedAt, Id)</c> pair. Returns
    /// <c>false</c> and sets <paramref name="decoded"/> to null for ANY malformed input — callers
    /// must treat a failed decode as "start from the beginning", not as an error.
    /// </summary>
    public static bool TryDecode(string cursor, out (DateTime CreatedAt, Guid Id)? decoded)
    {
        decoded = null;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var pipeIdx = payload.IndexOf('|');
        if (pipeIdx <= 0 || pipeIdx == payload.Length - 1)
        {
            return false;
        }

        var dateStr = payload[..pipeIdx];
        var idStr = payload[(pipeIdx + 1)..];

        // RoundtripKind preserves the kind already encoded in the ISO string ("...Z"). It cannot
        // be combined with AssumeUniversal — if the encoded value ever loses its "Z" (shouldn't
        // happen because Encode always writes UTC), ToUniversalTime() below normalises Local to
        // UTC as a belt-and-braces fallback.
        if (!DateTime.TryParse(
                dateStr,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var createdAt))
        {
            return false;
        }

        if (!Guid.TryParse(idStr, out var id))
        {
            return false;
        }

        decoded = (createdAt.ToUniversalTime(), id);
        return true;
    }
}
