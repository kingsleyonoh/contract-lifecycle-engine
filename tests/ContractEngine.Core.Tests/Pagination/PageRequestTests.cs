using ContractEngine.Core.Pagination;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Core.Tests.Pagination;

/// <summary>
/// Verifies <see cref="PageRequest"/> page-size clamping and defaults. The type itself is a
/// simple record, but the constructor-time clamp ensures callers can never pass abusive sizes
/// (0, negative, &gt; 100) through to the SQL layer.
/// Binding spec: <c>CODEBASE_CONTEXT.md</c> Key Patterns §2 (default 25, max 100).
/// </summary>
public class PageRequestTests
{
    [Fact]
    public void ClampPageSize_WhenZero_ReturnsOne()
    {
        PageRequest.ClampPageSize(0).Should().Be(1);
    }

    [Fact]
    public void ClampPageSize_WhenNegative_ReturnsOne()
    {
        PageRequest.ClampPageSize(-5).Should().Be(1);
    }

    [Fact]
    public void ClampPageSize_WhenAboveMax_ReturnsMax()
    {
        PageRequest.ClampPageSize(500).Should().Be(100);
    }

    [Fact]
    public void ClampPageSize_WithinRange_ReturnsInput()
    {
        PageRequest.ClampPageSize(25).Should().Be(25);
        PageRequest.ClampPageSize(100).Should().Be(100);
        PageRequest.ClampPageSize(1).Should().Be(1);
    }

    [Fact]
    public void Default_HasPageSize25_AndNoCursor_AndDescSort()
    {
        var request = new PageRequest();
        request.PageSize.Should().Be(25);
        request.Cursor.Should().BeNull();
        request.SortDir.Should().Be("desc");
    }
}
