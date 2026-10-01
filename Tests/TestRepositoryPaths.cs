using System;
using System.IO;

namespace Jellyfin.Plugin.AudioGateway.Tests;

internal static class TestRepositoryPaths
{
    public static string Example(string filename)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "schema", "examples", filename);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Unable to locate schema/examples/{filename} from {AppContext.BaseDirectory}");
    }
}
