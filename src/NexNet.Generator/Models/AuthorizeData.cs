using System.Collections.Immutable;
using System.Linq;

namespace NexNet.Generator.Models;

/// <summary>
/// Data for a [NexusAuthorize] attribute on a method.
/// </summary>
internal sealed record AuthorizeData(
    ImmutableArray<int> Permissions,
    string PermissionEnumFullyQualifiedName)
{
    public bool Equals(AuthorizeData? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return PermissionEnumFullyQualifiedName == other.PermissionEnumFullyQualifiedName
               && Permissions.SequenceEqual(other.Permissions);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = PermissionEnumFullyQualifiedName.GetHashCode();
            foreach (var p in Permissions)
                hash = hash * 31 + p;
            return hash;
        }
    }
}
