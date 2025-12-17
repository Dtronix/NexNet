using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace NexNet.Generator.Models;

/// <summary>
/// Serializable location data for diagnostic reporting.
/// Stores enough information to reconstruct a Location.
/// </summary>
internal sealed record LocationData(
    string FilePath,
    int StartPosition,      // Absolute position in source
    int Length,
    int StartLine,          // For display
    int StartColumn
)
{
    /// <summary>
    /// Creates LocationData from a Roslyn Location.
    /// </summary>
    public static LocationData? FromLocation(Location? location)
    {
        if (location is null || location.Kind == LocationKind.None || location.IsInMetadata)
            return null;

        var lineSpan = location.GetLineSpan();
        return new LocationData(
            location.SourceTree?.FilePath ?? string.Empty,
            location.SourceSpan.Start,
            location.SourceSpan.Length,
            lineSpan.StartLinePosition.Line,
            lineSpan.StartLinePosition.Character
        );
    }

    /// <summary>
    /// Creates LocationData from a symbol's first location.
    /// </summary>
    public static LocationData? FromSymbol(ISymbol? symbol)
    {
        return FromLocation(symbol?.Locations.FirstOrDefault());
    }

    /// <summary>
    /// Creates LocationData from a syntax node.
    /// </summary>
    public static LocationData? FromSyntax(SyntaxNode? node)
    {
        return FromLocation(node?.GetLocation());
    }

    /// <summary>
    /// Creates LocationData from a syntax token (e.g., identifier).
    /// </summary>
    public static LocationData? FromToken(SyntaxToken token)
    {
        return FromLocation(token.GetLocation());
    }

    /// <summary>
    /// Reconstructs a Roslyn Location for diagnostic reporting.
    /// Returns Location.None if the source tree is not available.
    /// </summary>
    public Location ToLocation(Compilation? compilation)
    {
        if (compilation is null || string.IsNullOrEmpty(FilePath))
            return Location.None;

        var tree = compilation.SyntaxTrees
            .FirstOrDefault(t => string.Equals(t.FilePath, FilePath, StringComparison.OrdinalIgnoreCase));

        if (tree is null)
            return Location.None;

        var span = new TextSpan(StartPosition, Length);
        return Location.Create(tree, span);
    }

    /// <summary>
    /// Reconstructs a Location using a provided syntax tree lookup.
    /// More efficient when compilation is not available.
    /// </summary>
    public Location ToLocation(Func<string, SyntaxTree?> treeResolver)
    {
        if (string.IsNullOrEmpty(FilePath))
            return Location.None;

        var tree = treeResolver(FilePath);
        if (tree is null)
            return Location.None;

        var span = new TextSpan(StartPosition, Length);
        return Location.Create(tree, span);
    }
}
