using System.Text.Json;

namespace OpenApparatus.Studio.Services;

public static class MultiRoomEnvironmentJsonSerializer
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(MultiRoomEnvironmentSpec spec) =>
        JsonSerializer.Serialize(spec, Options);

    public static MultiRoomEnvironmentSpec Deserialize(string json) =>
        JsonSerializer.Deserialize<MultiRoomEnvironmentSpec>(json, Options)
            ?? throw new System.IO.InvalidDataException("Empty or invalid JSON.");
}
