namespace ContractEngine.Core.Pagination;

/// <summary>
/// Marker interface for entities that can be paginated via the composite
/// <c>(CreatedAt, Id)</c> cursor (see <c>CODEBASE_CONTEXT.md</c> Key Patterns §2). Entities must
/// expose both fields publicly so the Infrastructure-layer EF Core extension can build the WHERE
/// clause that resumes paging from the last-seen row without taking a dependency on the concrete
/// entity type.
/// </summary>
public interface IHasCursor
{
    DateTime CreatedAt { get; }
    Guid Id { get; }
}
