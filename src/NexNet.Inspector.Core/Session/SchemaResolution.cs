using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.Core.Session;

/// <summary>Decides which schema decodes a session, from what it embeds, the workspace schema and the user's choice.</summary>
/// <param name="Status">What is known about the embedded schema.</param>
/// <param name="Source">Which schema decodes.</param>
/// <param name="Schema">The schema that decodes; null for raw.</param>
/// <param name="Embedded">The embedded schema when it was read; kept so the choice can change later.</param>
public readonly record struct SchemaResolution(SchemaStatus Status, SchemaSource Source, NexusSchema? Schema, NexusSchema? Embedded)
{
    /// <summary>Resolves a session's schema.</summary>
    /// <param name="namesSchema">True when the session names a schema hash.</param>
    /// <param name="embedded">The embedded schema, when found and read.</param>
    /// <param name="workspace">The workspace schema, when the user set one.</param>
    /// <param name="choice">The user's choice.</param>
    public static SchemaResolution Resolve(bool namesSchema, NexusSchema? embedded, NexusSchema? workspace, SchemaChoice choice)
    {
        var status = embedded is not null
            ? workspace is not null && workspace.Hash != embedded.Hash ? SchemaStatus.Mismatch : SchemaStatus.Embedded
            : namesSchema ? SchemaStatus.Unreadable : SchemaStatus.Missing;

        return choice switch
        {
            SchemaChoice.Raw => new(status, SchemaSource.None, null, embedded),
            SchemaChoice.Workspace when workspace is not null => new(status, SchemaSource.Workspace, workspace, embedded),
            _ when embedded is not null => new(status, SchemaSource.Embedded, embedded, embedded),
            // Nothing embedded: the workspace schema is the only way to decode at all (OQ3 decodes raw without one).
            _ when workspace is not null => new(status, SchemaSource.Workspace, workspace, embedded),
            _ => new(status, SchemaSource.None, null, embedded),
        };
    }
}
