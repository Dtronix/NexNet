namespace NexNet.Interop;

/// <summary>
/// A query-string token of the WebSocket or HttpSocket upgrade request (<see cref="ClientSpec.QueryToken"/>): the
/// client appends <c>name=value</c>, each URL-encoded, to the URL's query.
/// </summary>
/// <param name="Name">The query parameter name; not empty.</param>
/// <param name="Value">The token.</param>
public sealed record QueryToken(string Name, string Value);
