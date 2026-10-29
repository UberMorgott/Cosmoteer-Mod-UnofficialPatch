using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Performance;

/// <summary>
/// Experimental opt-in: .NET Server GC via System.GC.Server in the game's runtimeconfig.json (read by the host at the
/// next launch). Opt-in = the flag file at <see cref="FlagPath"/> (outside Workshop content, so mod updates keep it).
/// Only our key is touched; a marker next to the config records the prior value and the hash of the file we wrote,
/// so opt-out restores that value only if nobody (game update, user) changed the file since.
/// </summary>
internal static class ServerGc
{
    private const string Key = "System.GC.Server";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // Vanilla's default save root (Paths.GetDefaultRootSavePath, first branch) without the Steam ID, which is unknown
    // this early. shortcut: a relocated Saved Games known folder is not followed; upgrade if players ask.
    internal static string FlagPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Cosmoteer", "unofficial-patch-server-gc.txt");

    public static void Apply()
    {
        try
        {
            string name = Assembly.GetEntryAssembly()?.GetName().Name ?? "Cosmoteer";
            if (Sync(Path.Combine(AppContext.BaseDirectory, name + ".runtimeconfig.json"), File.Exists(FlagPath)) is { } msg)
            {
                Log.Info(msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error("server-gc", ex); // config left as it was; the game runs with its current GC
        }
    }

    /// <summary>Brings the config in line with <paramref name="optIn"/>. Returns a log line, or null when nothing changed. Throws, leaving the config untouched, on bad JSON or IO errors.</summary>
    internal static string? Sync(string config, bool optIn)
    {
        string marker = config + ".unofficialpatch";
        byte[] bytes = File.ReadAllBytes(config);
        JsonObject root = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'))!.AsObject();
        JsonNode? current = root["runtimeOptions"]?["configProperties"]?[Key];
        if (optIn)
        {
            if (current?.GetValueKind() == JsonValueKind.True)
            {
                return null;
            }
            string prior = current?.ToJsonString() ?? "absent";
            string backup = config + ".unofficialpatch.bak";
            if (!File.Exists(backup))
            {
                File.WriteAllBytes(backup, bytes); // original bytes, once; never restored automatically
            }
            JsonObject options = Child(root, "runtimeOptions");
            Child(options, "configProperties")[Key] = true;
            byte[] patched = Encoding.UTF8.GetBytes(root.ToJsonString(Indented));
            // Marker first: if the config write then fails, its hash never matches and opt-out leaves the file alone.
            File.WriteAllText(marker, prior + "\n" + Hash(patched));
            Replace(config, patched);
            return $"Server GC enabled in {config} (was {prior}); restart the game to apply.";
        }
        if (!File.Exists(marker))
        {
            return null;
        }
        string[] owned = File.ReadAllText(marker).Split('\n');
        if (owned.Length != 2 || owned[1] != Hash(bytes))
        {
            File.Delete(marker);
            return $"Server GC: {config} changed since the mod enabled it (game update or manual edit); left untouched.";
        }
        JsonObject props = root["runtimeOptions"]!["configProperties"]!.AsObject();
        if (owned[0] == "absent")
        {
            props.Remove(Key);
        }
        else
        {
            props[Key] = JsonNode.Parse(owned[0]);
        }
        Replace(config, Encoding.UTF8.GetBytes(root.ToJsonString(Indented)));
        File.Delete(marker);
        return $"Server GC disabled in {config} (restored {owned[0]}); restart the game to apply.";
    }

    private static JsonObject Child(JsonObject parent, string name) =>
        (parent[name] ??= new JsonObject()).AsObject();

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>Temp file next to the original, flushed to disk, atomically moved over it, then re-read and compared.</summary>
    private static void Replace(string path, byte[] data)
    {
        string tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                fs.Write(data);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp); // no-op after a successful move
        }
        if (!File.ReadAllBytes(path).SequenceEqual(data))
        {
            throw new IOException(path + " differs from what was written");
        }
    }
}
