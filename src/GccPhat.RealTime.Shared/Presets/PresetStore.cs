using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GccPhat.RealTime.Presets;

/// <summary>Persists <see cref="ArrayGeometryPreset"/> instances as JSON files under the user's AppData folder.</summary>
public sealed class PresetStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GccPhatRealtime", "presets");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<string> ListPresetNames()
    {
        if (!Directory.Exists(Dir))
        {
            return Array.Empty<string>();
        }
        return Directory.EnumerateFiles(Dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ArrayGeometryPreset? Load(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ArrayGeometryPreset>(stream, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(ArrayGeometryPreset preset)
    {
        Directory.CreateDirectory(Dir);
        using FileStream stream = File.Create(PathFor(preset.Name));
        JsonSerializer.Serialize(stream, preset, JsonOptions);
    }

    public void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string PathFor(string name) => Path.Combine(Dir, SanitizeFileName(name) + ".json");

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
