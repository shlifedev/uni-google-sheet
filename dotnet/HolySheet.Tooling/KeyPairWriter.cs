using HolySheet.Core.Config;
using HolySheet.Core.Output;

namespace HolySheet.Tooling;

/// <summary>Creates the key pair that signs manifests, and keeps the private half out of version control.</summary>
public static class KeyPairWriter
{
    public const string DefaultFileName = "holysheet.private.key";

    /// <summary>
    /// Writes a new private key to <paramref name="path"/> (readable by the owner only) and returns the public key.
    /// Refuses to replace an existing key: every client built with the old public key would stop accepting the data.
    /// </summary>
    public static string Create(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException($"'{path}' already exists. Replacing a key invalidates every client built with the old one.");
        }

        (string privateKey, string publicKey) = SigningKeys.Generate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, privateKey);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return publicKey;
    }

    /// <summary>
    /// Creates <see cref="DefaultFileName"/> next to the config and turns signing on in it: the public key and the key file.
    /// The config is changed in memory; the caller saves it.
    /// </summary>
    public static string CreateForConfig(HolySheetConfig config)
    {
        string path = Path.Combine(config.BaseDirectory, DefaultFileName);
        string publicKey = Create(path);
        config.Signing.PublicKey = publicKey;
        config.Signing.PrivateKeyFile = DefaultFileName;
        return path;
    }

    /// <summary>Whether the .gitignore in <paramref name="directory"/> already has a line for <paramref name="entry"/>.</summary>
    public static bool IsIgnored(string directory, string entry = DefaultFileName)
    {
        string path = Path.Combine(directory, ".gitignore");
        return File.Exists(path) && File.ReadAllLines(path).Any(line => line.Trim() is var text && (text == entry || text == "/" + entry || text == "*.private.key"));
    }

    /// <summary>Adds <paramref name="entry"/> to the .gitignore in <paramref name="directory"/>, creating it when there is none.</summary>
    public static void AddToGitignore(string directory, string entry = DefaultFileName)
    {
        if (IsIgnored(directory, entry))
        {
            return;
        }

        string path = Path.Combine(directory, ".gitignore");
        string existing = File.Exists(path) ? File.ReadAllText(path) : "";
        string separator = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";
        File.WriteAllText(path, existing + separator + "# HolySheet signing key: never commit it\n" + entry + "\n");
    }
}
