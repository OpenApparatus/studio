using System.Text.Json;

namespace OpenApparatus.Studio.Services;

public static class FloorPlanJsonSerializer
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(FloorPlanSpec spec) =>
        JsonSerializer.Serialize(spec, Options);

    public static FloorPlanSpec Deserialize(string json) =>
        JsonSerializer.Deserialize<FloorPlanSpec>(json, Options)
            ?? throw new System.IO.InvalidDataException("Empty or invalid JSON.");
}
