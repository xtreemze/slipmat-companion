using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>One existing leaf value to compare and replace. Paths are relative to the root.</summary>
/// <param name="Path">Element names relative to the root.</param>
/// <param name="Expected">Exact current text.</param>
/// <param name="Replacement">Replacement text.</param>
public sealed record CloudMaintenanceXmlEdit(IReadOnlyList<string> Path, string Expected, string Replacement);

/// <summary>In-memory candidate only; neither lease nor filesystem safety is established here.</summary>
/// <param name="BeforeHash">Original SHA-256.</param>
/// <param name="BeforeBytes">Original bytes.</param>
/// <param name="AfterHash">Candidate SHA-256.</param>
/// <param name="AfterBytes">Candidate bytes.</param>
public sealed record CloudMaintenanceXmlPlan(string BeforeHash, byte[] BeforeBytes, string AfterHash, byte[] AfterBytes);

/// <summary>
/// Bounded, exact-value XML planning. The caller must validate domain bindings and
/// hold the host barrier and journal lease; this type deliberately performs no writes.
/// </summary>
public static class CloudMaintenanceConfiguration
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    /// <summary>Prepare candidate bytes after validating the entire original hash and each unique leaf.</summary>
    public static CloudMaintenanceXmlPlan Prepare(byte[] before, string expectedHash, string rootName, IReadOnlyList<CloudMaintenanceXmlEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(edits);
        if (before.Length > MaximumBytes || edits.Count == 0 || string.IsNullOrEmpty(rootName))
            throw new InvalidOperationException("Invalid XML patch request.");
        var beforeHash = Convert.ToHexString(SHA256.HashData(before));
        if (!string.Equals(beforeHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configuration hash mismatch.");

        using var input = new MemoryStream(before, writable: false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root is null || root.Name.NamespaceName.Length != 0 || root.Name.LocalName != rootName)
            throw new InvalidOperationException("Configuration root mismatch.");
        var touched = new HashSet<XElement>();
        var changed = false;
        foreach (var edit in edits)
        {
            if (edit.Path.Count == 0 || edit.Path.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Invalid XML patch path.");
            var element = root;
            foreach (var name in edit.Path)
            {
                if (name.Contains(':') || name.Contains('{') || name.Contains('}'))
                    throw new InvalidOperationException("Invalid XML patch path.");
                var matches = element.Elements().Where(child => child.Name.LocalName == name).Take(2).ToArray();
                if (matches.Length != 1 || matches[0].Name.NamespaceName.Length != 0) throw new InvalidOperationException("Ambiguous XML patch path.");
                element = matches[0];
            }
            if (!touched.Add(element) || element.HasElements || element.Nodes().Any(node => node is not XText)
                || !string.Equals(element.Value, edit.Expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Configuration value mismatch.");
            if (!string.Equals(edit.Expected, edit.Replacement, StringComparison.Ordinal))
            {
                element.Value = edit.Replacement;
                changed = true;
            }
        }
        if (!changed) return new CloudMaintenanceXmlPlan(beforeHash, before.ToArray(), beforeHash, before.ToArray());
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = document.Declaration is null, CloseOutput = false }))
            document.Save(writer);
        var after = output.ToArray();
        return new CloudMaintenanceXmlPlan(beforeHash, before.ToArray(), Convert.ToHexString(SHA256.HashData(after)), after);
    }
}
