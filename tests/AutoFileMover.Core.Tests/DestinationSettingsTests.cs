using AutoFileMover.Core;

namespace AutoFileMover.Core.Tests;

public sealed class DestinationSettingsTests
{
    private static MoverSettings Base() => new() { SourceFolder = @"C:\Saw\In", TargetFolder = @"C:\Saw\Out" };

    [Fact]
    public void Old_single_send_to_folder_is_migrated_to_destination_one()
    {
        var path = Path.Combine(Path.GetTempPath(), "afm-dest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                { "SourceFolder": "C:\\Saw\\In", "TargetFolder": "C:\\Saw\\Out", "SendToFolder": "C:\\Saw\\Queue" }
                """);
            var store = new SettingsStore(path);

            var loaded = store.Load();
            Assert.Single(loaded.Destinations);
            Assert.Equal(@"C:\Saw\Queue", loaded.Destinations[0].Path);
            Assert.Equal("Queue", loaded.Destinations[0].DisplayName);
            Assert.Null(loaded.SendToFolder);

            store.Save(loaded);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("SendToFolder", json);
            Assert.Contains("Destinations", json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Two_destinations_are_valid_and_round_trip()
    {
        var s = Base();
        s.Destinations = [new() { Name = "Saw 1", Path = @"C:\Saw\Q1" }, new() { Name = "Saw 2", Path = @"\\saw2\queue" }];
        Assert.Empty(s.Validate());

        var copy = s.Clone();
        copy.Destinations[0].Path = @"C:\Changed";
        Assert.Equal(@"C:\Saw\Q1", s.Destinations[0].Path); // deep copy
    }

    [Theory]
    [InlineData(@"C:\Saw\Q1", @"C:\Saw\Q1\", "same folder")]
    [InlineData(@"C:\Saw\Out", @"C:\Saw\Q2", "different from the source and target")]
    [InlineData(@"relative\path", @"C:\Saw\Q2", "full path")]
    public void Bad_destinations_are_rejected(string first, string second, string expected)
    {
        var s = Base();
        s.Destinations = [new() { Name = "A", Path = first }, new() { Name = "B", Path = second }];
        Assert.Contains(s.Validate(), e => e.Contains(expected));
    }

    [Fact]
    public void Duplicate_display_names_are_rejected()
    {
        var s = Base();
        s.Destinations = [new() { Path = @"C:\A\Queue" }, new() { Path = @"D:\B\Queue" }];
        Assert.Contains(s.Validate(), e => e.Contains("different name"));
    }

    [Fact]
    public void Partially_filled_settings_can_be_saved_but_engine_still_requires_folders()
    {
        // Regression: Save refused to store a source folder until the target was also set.
        var s = new MoverSettings { SourceFolder = @"C:\Saw\In" };

        Assert.Empty(s.Validate(requireFolders: false));                          // window may save it
        Assert.Contains(s.Validate(), e => e.Contains("target folder is not set")); // engine waits for target
        Assert.DoesNotContain(s.Validate(), e => e.Contains("source folder"));

        // Genuinely wrong values are still blocked when saving.
        s.TargetFolder = @"C:\Saw\In\";
        Assert.Contains(s.Validate(requireFolders: false), e => e.Contains("must be different"));
        s.TargetFolder = @"relative";
        Assert.Contains(s.Validate(requireFolders: false), e => e.Contains("full path"));
    }

    [Fact]
    public void Empty_rows_are_dropped()
    {
        var s = Base();
        s.Destinations = [new() { Name = "Saw 1", Path = "" }, new() { Name = " Saw 2 ", Path = @" C:\Q2 " }];
        s.Normalize();
        Assert.Single(s.Destinations);
        Assert.Equal("Saw 2", s.Destinations[0].Name);
        Assert.Equal(@"C:\Q2", s.Destinations[0].Path);
    }
}
