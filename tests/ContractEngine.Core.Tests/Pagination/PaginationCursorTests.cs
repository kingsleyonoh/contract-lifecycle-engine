using ContractEngine.Core.Pagination;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Core.Tests.Pagination;

/// <summary>
/// Unit tests for <see cref="PaginationCursor"/>. The cursor is the opaque token returned to
/// clients in the pagination envelope; encoding/decoding round-trips must be loss-free so
/// subsequent page requests restore the exact ordering key we used to page.
/// Binding spec: <c>CODEBASE_CONTEXT.md</c> Key Patterns §2 (base64 composite cursor).
/// </summary>
public class PaginationCursorTests
{
    [Fact]
    public void Encode_ThenTryDecode_RoundTripsCreatedAtAndId()
    {
        var createdAt = new DateTime(2026, 4, 15, 12, 34, 56, DateTimeKind.Utc);
        var id = Guid.Parse("12345678-1234-1234-1234-123456789012");

        var encoded = PaginationCursor.Encode(createdAt, id);
        var ok = PaginationCursor.TryDecode(encoded, out var decoded);

        ok.Should().BeTrue();
        decoded.Should().NotBeNull();
        decoded!.Value.CreatedAt.Should().Be(createdAt);
        decoded.Value.Id.Should().Be(id);
    }

    [Fact]
    public void Encode_ProducesBase64UrlSafeCharacters()
    {
        var encoded = PaginationCursor.Encode(DateTime.UtcNow, Guid.NewGuid());
        encoded.Should().NotBeNullOrWhiteSpace();
        // Standard base64: letters, digits, +, /, = — no whitespace / line breaks.
        encoded.Should().MatchRegex("^[A-Za-z0-9+/=]+$");
    }

    [Fact]
    public void TryDecode_EmptyString_ReturnsFalse()
    {
        var ok = PaginationCursor.TryDecode(string.Empty, out var decoded);
        ok.Should().BeFalse();
        decoded.Should().BeNull();
    }

    [Fact]
    public void TryDecode_NullInput_ReturnsFalse()
    {
        var ok = PaginationCursor.TryDecode(null!, out var decoded);
        ok.Should().BeFalse();
        decoded.Should().BeNull();
    }

    [Fact]
    public void TryDecode_MalformedBase64_ReturnsFalse()
    {
        var ok = PaginationCursor.TryDecode("!!!not-base64!!!", out var decoded);
        ok.Should().BeFalse();
        decoded.Should().BeNull();
    }

    [Fact]
    public void TryDecode_ValidBase64ButMissingPipe_ReturnsFalse()
    {
        // Base64 of "no-pipe-here"
        var bogus = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("no-pipe-here"));
        var ok = PaginationCursor.TryDecode(bogus, out var decoded);
        ok.Should().BeFalse();
        decoded.Should().BeNull();
    }

    [Fact]
    public void TryDecode_ValidBase64ButInvalidDate_ReturnsFalse()
    {
        var bogus = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not-a-date|12345678-1234-1234-1234-123456789012"));
        var ok = PaginationCursor.TryDecode(bogus, out var decoded);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryDecode_ValidBase64ButInvalidGuid_ReturnsFalse()
    {
        var bogus = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("2026-04-15T12:00:00Z|not-a-guid"));
        var ok = PaginationCursor.TryDecode(bogus, out var decoded);
        ok.Should().BeFalse();
    }
}
