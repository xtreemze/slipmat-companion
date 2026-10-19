using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class AnalysisArtifactRetentionServiceTests : IDisposable
{
    private static readonly string OlderKey = "asv1-" + new string('a', 64);
    private static readonly string NewerKey = "asv1-" + new string('b', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "analysis-retention-" + Guid.NewGuid().ToString("N"));
    private readonly AnalysisArtifactRetentionService _service;

    public AnalysisArtifactRetentionServiceTests()
    {
        _service = new AnalysisArtifactRetentionService(() => _root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Disabled_policy_preserves_existing_analysis_and_waveform_artifacts()
    {
        WriteArtifact(OlderKey, "analysis", "json", 3);
        WriteArtifact(OlderKey, "waveforms", "pps_10.dat", 4);
        var old = DateTime.UtcNow.AddYears(-2);
        File.SetLastWriteTimeUtc(StorePaths.SidecarPath(_root, OlderKey), old);
        WriteStamp(OlderKey, "stamp");
        var config = new PluginConfiguration();

        var result = _service.ApplyConfiguredPolicy(config, DateTimeOffset.UtcNow);

        Assert.Equal(0, result.DeletedSubjects);
        Assert.True(File.Exists(StorePaths.SidecarPath(_root, OlderKey)));
        Assert.True(File.Exists(StorePaths.WaveformDatPath(_root, OlderKey, "awf_v1_riff_mono_u8_peak", 10)));
        Assert.True(File.Exists(StorePaths.AnalysisSourceStampPath(_root, OlderKey)));
    }

    [Fact]
    public void Size_cleanup_evicts_least_recent_subject_as_a_group_and_preserves_source_stamps()
    {
        WriteArtifact(OlderKey, "analysis", "json", 3);
        WriteArtifact(OlderKey, "waveforms", "pps_10.dat", 4);
        WriteArtifact(NewerKey, "analysis", "json", 5);
        WriteArtifact(NewerKey, "waveforms", "pps_10.dat", 6);
        WriteStamp(OlderKey, "old stamp");
        WriteStamp(NewerKey, "new stamp");
        TouchArtifacts(OlderKey, DateTime.UtcNow.AddMinutes(-10));
        TouchArtifacts(NewerKey, DateTime.UtcNow);

        var result = _service.ApplyRetention(_root, maxBytes: 15, maxAge: null, DateTimeOffset.UtcNow);

        Assert.Equal(1, result.DeletedSubjects);
        Assert.False(File.Exists(StorePaths.SidecarPath(_root, OlderKey)));
        Assert.False(Directory.Exists(Path.Combine(_root, "waveforms", OlderKey)));
        Assert.True(File.Exists(StorePaths.AnalysisSourceStampPath(_root, OlderKey)));
        Assert.True(File.Exists(StorePaths.SidecarPath(_root, NewerKey)));
        Assert.True(Directory.Exists(Path.Combine(_root, "waveforms", NewerKey)));
        Assert.Equal(11, result.RemainingBytes);
    }

    [Fact]
    public void Expired_subjects_are_removed_but_analysis_source_stamps_are_not()
    {
        WriteArtifact(OlderKey, "analysis", "json", 3);
        WriteArtifact(OlderKey, "waveforms", "pps_10.dat", 4);
        WriteStamp(OlderKey, "stamp");
        TouchArtifacts(OlderKey, DateTime.UtcNow.AddDays(-3));

        var result = _service.ApplyRetention(
            _root,
            maxBytes: null,
            maxAge: TimeSpan.FromDays(1),
            DateTimeOffset.UtcNow);

        Assert.Equal(1, result.DeletedSubjects);
        Assert.False(File.Exists(StorePaths.SidecarPath(_root, OlderKey)));
        Assert.False(Directory.Exists(Path.Combine(_root, "waveforms", OlderKey)));
        Assert.True(File.Exists(StorePaths.AnalysisSourceStampPath(_root, OlderKey)));
    }

    [Fact]
    public void Active_subject_is_not_evicted_until_its_lease_is_released()
    {
        WriteArtifact(OlderKey, "analysis", "json", 3);
        var lease = _service.TryAcquire(_root, OlderKey);
        Assert.NotNull(lease);

        var whileActive = _service.ApplyRetention(_root, maxBytes: 0, maxAge: null, DateTimeOffset.UtcNow);
        Assert.Equal(0, whileActive.DeletedSubjects);
        Assert.True(File.Exists(StorePaths.SidecarPath(_root, OlderKey)));

        lease!.Dispose();
        var afterRelease = _service.ApplyRetention(_root, maxBytes: 0, maxAge: null, DateTimeOffset.UtcNow);
        Assert.Equal(1, afterRelease.DeletedSubjects);
        Assert.False(File.Exists(StorePaths.SidecarPath(_root, OlderKey)));
    }

    [Fact]
    public void Acquiring_a_subject_updates_its_lru_position()
    {
        WriteArtifact(OlderKey, "analysis", "json", 3);
        WriteArtifact(NewerKey, "analysis", "json", 4);
        TouchArtifacts(OlderKey, DateTime.UtcNow.AddMinutes(-10));
        TouchArtifacts(NewerKey, DateTime.UtcNow.AddMinutes(-5));

        using (_service.TryAcquire(_root, OlderKey)) { }
        var result = _service.ApplyRetention(_root, maxBytes: 4, maxAge: null, DateTimeOffset.UtcNow);

        Assert.Equal(1, result.DeletedSubjects);
        Assert.True(File.Exists(StorePaths.SidecarPath(_root, OlderKey)));
        Assert.False(File.Exists(StorePaths.SidecarPath(_root, NewerKey)));
    }

    [Fact]
    public void Usage_counts_only_reproducible_analysis_and_waveform_files()
    {
        WriteArtifact(OlderKey, "analysis", "json", 3);
        WriteArtifact(OlderKey, "waveforms", "pps_10.dat", 4);
        WriteStamp(OlderKey, "123456789");
        Directory.CreateDirectory(Path.Combine(_root, "physical-media-materials", "pmv1-test"));
        File.WriteAllText(Path.Combine(_root, "physical-media-materials", "pmv1-test", "map.png"), "1234567890");

        var status = _service.GetUsage(_root, new PluginConfiguration());

        Assert.Equal(7, status.UsedBytes);
        Assert.Equal(1, status.SubjectCount);
        Assert.False(status.Enabled);
        Assert.Equal(0, status.ActiveSubjects);
    }

    [Fact]
    public void Retention_leaves_unrecognized_files_in_the_waveform_store_untouched()
    {
        WriteArtifact(OlderKey, "waveforms", "pps_10.dat", 4);
        var unknown = Path.Combine(_root, "waveforms", OlderKey, "operator-data", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(unknown)!);
        File.WriteAllText(unknown, "keep");

        var result = _service.ApplyRetention(_root, maxBytes: 0, maxAge: null, DateTimeOffset.UtcNow);

        Assert.Equal(1, result.DeletedSubjects);
        Assert.True(File.Exists(unknown));
        Assert.Equal(0, result.RemainingBytes);
    }

    [Fact]
    public void Configured_limits_are_reported_and_applied()
    {
        WriteArtifact(OlderKey, "analysis", "json", 3);
        TouchArtifacts(OlderKey, DateTime.UtcNow.AddDays(-2));
        var config = new PluginConfiguration
        {
            AnalysisArtifactMaxSizeGiB = 1,
            AnalysisArtifactMaxAgeHours = 24,
        };

        var status = _service.GetUsage(_root, config);
        var result = _service.ApplyConfiguredPolicy(config, DateTimeOffset.UtcNow);

        Assert.True(status.Enabled);
        Assert.Equal(24, status.MaximumAgeHours);
        Assert.Equal(1024L * 1024 * 1024, status.MaximumBytes);
        Assert.Equal(1, result.DeletedSubjects);
        Assert.Equal(0, result.RemainingBytes);
    }

    [Fact]
    public async Task Eviction_marks_a_current_source_stamp_without_removing_it()
    {
        var media = Path.Combine(_root, "source.wav");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(media, new byte[] { 1, 2, 3 });
        var sourceStamp = AnalysisSourceStamp.Capture(media)!;
        await AnalysisSourceStamp.WriteAsync(_root, OlderKey, sourceStamp, CancellationToken.None);
        WriteArtifact(OlderKey, "analysis", "json", 3);

        _service.ApplyRetention(_root, maxBytes: 0, maxAge: null, DateTimeOffset.UtcNow);

        Assert.True(File.Exists(StorePaths.AnalysisSourceStampPath(_root, OlderKey)));
        Assert.True(AnalysisSourceStamp.IsCurrent(_root, OlderKey, media));
        Assert.True(AnalysisSourceStamp.WereArtifactsEvicted(_root, OlderKey, media));

        await AnalysisSourceStamp.WriteAsync(_root, OlderKey, AnalysisSourceStamp.Capture(media)!, CancellationToken.None);
        Assert.False(AnalysisSourceStamp.WereArtifactsEvicted(_root, OlderKey, media));
    }

    private void WriteArtifact(string key, string area, string name, int bytes)
    {
        var path = area == "analysis"
            ? StorePaths.SidecarPath(_root, key)
            : StorePaths.WaveformDatPath(_root, key, "awf_v1_riff_mono_u8_peak", 10);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private void WriteStamp(string key, string value)
    {
        var path = StorePaths.AnalysisSourceStampPath(_root, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(
            new AnalysisSourceStamp.Stamp(value.Length, DateTime.UtcNow.Ticks)));
    }

    private void TouchArtifacts(string key, DateTime timestamp)
    {
        var sidecar = StorePaths.SidecarPath(_root, key);
        if (File.Exists(sidecar)) File.SetLastWriteTimeUtc(sidecar, timestamp);
        var waveformRoot = Path.Combine(_root, "waveforms", key);
        if (!Directory.Exists(waveformRoot)) return;
        foreach (var file in Directory.EnumerateFiles(waveformRoot, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, timestamp);
        }
    }
}
