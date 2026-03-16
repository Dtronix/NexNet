using System.Collections.Immutable;
using System.Linq;

namespace NexNet.Generator.Models;

/// <summary>
/// Data for a [NexusAuthorize] attribute on a method.
/// </summary>
internal sealed record AuthorizeData(
    ImmutableArray<int> Permissions,
    string PermissionEnumFullyQualifiedName,
    bool IsUnderlyingTypeCompatible = true,
    int CacheDurationSeconds = -1)
{
    public bool Equals(AuthorizeData? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return PermissionEnumFullyQualifiedName == other.PermissionEnumFullyQualifiedName
               && IsUnderlyingTypeCompatible == other.IsUnderlyingTypeCompatible
               && CacheDurationSeconds == other.CacheDurationSeconds
               && Permissions.SequenceEqual(other.Permissions);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = PermissionEnumFullyQualifiedName.GetHashCode();
            hash = hash * 31 + IsUnderlyingTypeCompatible.GetHashCode();
            hash = hash * 31 + CacheDurationSeconds;
            foreach (var p in Permissions)
                hash = hash * 31 + p;
            return hash;
        }
    }
}
