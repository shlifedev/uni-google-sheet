namespace HolySheet.Tooling;

/// <summary>What checking a data folder against its manifest found.</summary>
/// <param name="Signature">"valid", "INVALID", "MISSING", or a sentence saying why it was not checked.</param>
/// <param name="Tables">How many tables the manifest lists; 0 when it could not be read.</param>
/// <param name="Content">The manifest's content hash, or null when it could not be read.</param>
public sealed record VerifyReport(string Directory, string Signature, int Tables, string? Content, IReadOnlyList<string> Problems)
{
    public bool Success => Problems.Count == 0;
}

/// <summary>
/// Checks that a data folder is what a build wrote: every file the manifest lists is there with the right size and hash,
/// and, given a public key, that the manifest is signed by it. This is the check a game does on load, run ahead of time.
/// </summary>
public static class DataVerifier
{
    public static VerifyReport Verify(string directory, string? publicKey, string manifestName = TableManifest.DefaultFileName)
    {
        directory = Path.GetFullPath(directory);
        var problems = new List<string>();
        TableManifest? manifest = null;
        try
        {
            manifest = TableManifest.Parse(File.ReadAllText(Path.Combine(directory, manifestName)));
        }
        catch (Exception exception) when (exception is TableLoadException or IOException or UnauthorizedAccessException)
        {
            problems.Add("manifest: " + exception.Message);
        }

        string signature = "not checked (no public key given)";
        if (manifest == null)
        {
            return new VerifyReport(directory, signature, 0, null, problems);
        }

        if (!string.IsNullOrEmpty(publicKey))
        {
            bool valid = manifest.IsSigned && TableSignature.Verify(manifest, publicKey);
            signature = valid ? "valid" : manifest.IsSigned ? "INVALID" : "MISSING";
            if (!valid)
            {
                problems.Add(manifest.IsSigned ? "signature: does not match the public key" : "signature: the manifest is not signed");
            }
        }
        else if (manifest.IsSigned)
        {
            signature = "present, not checked (no public key given)";
        }

        foreach (TableManifestEntry entry in manifest.Tables)
        {
            string path = Path.Combine(directory, entry.File);
            if (!File.Exists(path))
            {
                problems.Add($"{entry.File}: missing");
                continue;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length != entry.Bytes || TableHash.Compute(bytes) != entry.Hash)
            {
                problems.Add($"{entry.File}: does not match the manifest (modified, truncated, or from another build)");
            }
        }

        return new VerifyReport(directory, signature, manifest.Tables.Count, manifest.Content, problems);
    }
}
