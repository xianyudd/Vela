using System.Collections.Immutable;
using Vela.Core.Models;

namespace Vela.Application.Profiles;

/// <summary>
/// Immutable snapshot of the persisted profile store.
/// </summary>
/// <param name="SchemaVersion">The current schema version.</param>
/// <param name="LastProfileId">The identifier of the last selected profile.</param>
/// <param name="LogRetentionDays">Number of days to retain run logs.</param>
/// <param name="Profiles">All persisted profiles.</param>
public sealed record ProfileStoreState(
    int SchemaVersion,
    Guid LastProfileId,
    int LogRetentionDays,
    ImmutableArray<Profile> Profiles);
