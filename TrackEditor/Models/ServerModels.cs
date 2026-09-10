namespace TrackEditor.Models;

// Wire contracts for the server-storage API, mirroring TrackEditor.Web.Models.AccountDtos.

public record TrackSummaryDto(Guid Id, string Name, bool IsShared, DateTime UpdatedUtc);
public record StoredTrackDto(Guid Id, string Name, bool IsShared, string TrackJson);
public record CreateTrackRequest(string Name, string TrackJson);
public record UpdateTrackRequest(string Name, string TrackJson);
public record AppConfigDto(bool ServerStorage, bool AllowRegistration);
