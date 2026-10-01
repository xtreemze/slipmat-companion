using System.IO;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class StreamripCliServiceTests
{
    [Fact]
    public void ReadConfiguredSources_DetectsConfiguredProviderSections()
    {
        var tempFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(
                tempFile,
                """
                [qobuz]
                email = "listener@example.com"

                [tidal]
                access_token = "abc123"

                [deezer]
                # not configured
                """);

            var configuredSources = StreamripCliService.ReadConfiguredSources(tempFile);

            Assert.Contains("qobuz", configuredSources);
            Assert.Contains("tidal", configuredSources);
            Assert.DoesNotContain("deezer", configuredSources);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseSearchResults_ReadsStreamripJsonOutput()
    {
        var tempFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(
                tempFile,
                """
                [
                  {
                    "source": "qobuz",
                    "media_type": "album",
                    "id": "123",
                    "desc": "John Coltrane - Blue Train"
                  }
                ]
                """);

            var results = StreamripCliService.ParseSearchResults(tempFile);

            Assert.Single(results);
            Assert.Equal("qobuz", results[0].Source);
            Assert.Equal("album", results[0].MediaType);
            Assert.Equal("123", results[0].Id);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Search_UsesOutputFileModeAndParsesResults()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var configDir = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "config", "streamrip"));
        var configPath = Path.Combine(configDir.FullName, "config.toml");
        File.WriteAllText(configPath, "[qobuz]\nemail = \"listener@example.com\"\n");

        var commandPath = CreateFakeStreamripExecutable(tempDir.FullName);

        try
        {
            var response = StreamripCliService.Search(
                new StreamripSearchRequest("qobuz", "album", "blue train", 5),
                commandPathOverride: commandPath,
                configPathOverride: configPath);

            Assert.True(response.Available);
            Assert.Null(response.Error);
            Assert.Single(response.Results);
            Assert.Equal("qobuz", response.Results[0].Source);
            Assert.Equal("album", response.Results[0].MediaType);
            Assert.Contains("Blue Train", response.Results[0].Description);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static string CreateFakeStreamripExecutable(string tempDir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var path = Path.Combine(tempDir, "rip.cmd");
            File.WriteAllText(
                path,
                """
                @echo off
                setlocal EnableDelayedExpansion
                set outfile=
                :loop
                if "%~1"=="" goto done
                if "%~1"=="--output-file" (
                  set outfile=%~2
                  shift
                )
                shift
                goto loop
                :done
                > "!outfile!" echo [{"source":"qobuz","media_type":"album","id":"123","desc":"John Coltrane - Blue Train"}]
                exit /b 0
                """);
            return path;
        }

        var scriptPath = Path.Combine(tempDir, "rip");
        File.WriteAllText(
            scriptPath,
            """
            #!/bin/sh
            outfile=""
            while [ "$#" -gt 0 ]; do
              if [ "$1" = "--output-file" ]; then
                outfile="$2"
                shift 2
                continue
              fi
              shift
            done
            printf '%s\n' '[{"source":"qobuz","media_type":"album","id":"123","desc":"John Coltrane - Blue Train"}]' > "$outfile"
            exit 0
            """);

        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        return scriptPath;
    }
}
