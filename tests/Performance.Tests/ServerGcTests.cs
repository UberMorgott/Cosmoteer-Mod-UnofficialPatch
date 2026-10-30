using System;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace Performance.Tests;

public sealed class ServerGcTests : IDisposable
{
    private const string Vanilla = """
        {
            "runtimeOptions": {
                "tfm": "net10.0",
                "configProperties": {
                    "System.Reflection.Metadata.MetadataUpdater.IsSupported": false
                }
            }
        }
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("servergc").FullName;
    private string Config => Path.Combine(_dir, "Cosmoteer.runtimeconfig.json");
    private string Marker => Config + ".unofficialpatch";
    private string Backup => Config + ".unofficialpatch.bak";

    public void Dispose()
    {
        if (File.Exists(Config))
        {
            File.SetAttributes(Config, FileAttributes.Normal);
        }
        Directory.Delete(_dir, true);
    }

    private JsonNode? Gc() => JsonNode.Parse(File.ReadAllText(Config))!["runtimeOptions"]!["configProperties"]![Server];
    private const string Server = "System.GC.Server";

    [Fact]
    public void AbsentKeyIsAddedOtherPropsKeptAndOptOutRemovesIt()
    {
        File.WriteAllText(Config, Vanilla);
        Assert.Contains("restart the game", ServerGc.Sync(Config, true));
        Assert.True(Gc()!.GetValue<bool>());
        JsonNode root = JsonNode.Parse(File.ReadAllText(Config))!;
        Assert.Equal("net10.0", (string?)root["runtimeOptions"]!["tfm"]);
        Assert.False(root["runtimeOptions"]!["configProperties"]!["System.Reflection.Metadata.MetadataUpdater.IsSupported"]!.GetValue<bool>());
        Assert.Equal(Vanilla, File.ReadAllText(Backup));
        Assert.Null(ServerGc.Sync(Config, true)); // already true: no-op

        Assert.Contains("restart the game", ServerGc.Sync(Config, false));
        Assert.Null(Gc());
        Assert.False(File.Exists(Marker));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Vanilla), JsonNode.Parse(File.ReadAllText(Config))));
        Assert.Null(ServerGc.Sync(Config, false));
    }

    [Fact]
    public void UserFalseIsOverriddenOnOptInAndRestoredOnOptOut()
    {
        File.WriteAllText(Config, Vanilla.Replace("IsSupported\": false", "IsSupported\": false, \"System.GC.Server\": false"));
        ServerGc.Sync(Config, true);
        Assert.True(Gc()!.GetValue<bool>());
        ServerGc.Sync(Config, false);
        Assert.False(Gc()!.GetValue<bool>());
    }

    [Fact]
    public void AlreadyTrueByUserIsNoOpAndNeverRemoved()
    {
        string text = Vanilla.Replace("IsSupported\": false", "IsSupported\": false, \"System.GC.Server\": true");
        File.WriteAllText(Config, text);
        Assert.Null(ServerGc.Sync(Config, true));
        Assert.Null(ServerGc.Sync(Config, false));
        Assert.Equal(text, File.ReadAllText(Config));
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public void MissingConfigPropertiesIsCreated()
    {
        File.WriteAllText(Config, """{"runtimeOptions":{"tfm":"net10.0"}}""");
        ServerGc.Sync(Config, true);
        Assert.True(Gc()!.GetValue<bool>());
    }

    [Fact]
    public void LaterEditByGameOrUserIsLeftAloneOnOptOut()
    {
        File.WriteAllText(Config, Vanilla);
        ServerGc.Sync(Config, true);
        string updated = File.ReadAllText(Config).Replace("net10.0", "net11.0");
        File.WriteAllText(Config, updated);
        Assert.Contains("left untouched", ServerGc.Sync(Config, false));
        Assert.Equal(updated, File.ReadAllText(Config));
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void BackupIsWrittenOnceAndNeverOverwritten()
    {
        File.WriteAllText(Config, Vanilla);
        ServerGc.Sync(Config, true);
        ServerGc.Sync(Config, false);
        File.WriteAllText(Config, Vanilla.Replace("net10.0", "net11.0")); // game update
        ServerGc.Sync(Config, true);
        Assert.Equal(Vanilla, File.ReadAllText(Backup));
    }

    [Fact]
    public void MalformedJsonThrowsAndLeavesFileUntouched()
    {
        File.WriteAllText(Config, "{ not json");
        Assert.ThrowsAny<Exception>(() => ServerGc.Sync(Config, true));
        Assert.Equal("{ not json", File.ReadAllText(Config));
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void ReadOnlyConfigThrowsAndLeavesFileUntouched()
    {
        File.WriteAllText(Config, Vanilla);
        File.SetAttributes(Config, FileAttributes.ReadOnly);
        Assert.ThrowsAny<Exception>(() => ServerGc.Sync(Config, true));
        Assert.Equal(Vanilla, File.ReadAllText(Config));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Contains("left untouched", ServerGc.Sync(Config, false)); // marker hash never matched the unchanged file
    }
}
