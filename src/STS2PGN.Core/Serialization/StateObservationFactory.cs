using System.Text.Json;
using STS2PGN.Model;

namespace STS2PGN.Serialization;

/// <summary>
/// Creates state observations whose digest is derived from canonical portable state.
/// </summary>
public static class StateObservationFactory
{
    public static StateObservationPayload Create(long after, string context, JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Portable state must be a JSON object.", nameof(state));
        }

        return new StateObservationPayload(
            after,
            context,
            CanonicalJson.ComputeSha256(state),
            FormatConstants.CurrentStateProjectionVersion,
            state);
    }

    public static StateObservationPayload Create<TState>(long after, string context, TState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Create(after, context, JsonSerializer.SerializeToElement(state));
    }
}
