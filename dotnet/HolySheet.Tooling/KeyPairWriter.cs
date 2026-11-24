using HolySheet.Core.Config;
using HolySheet.Core.Output;

namespace HolySheet.Tooling;

public static class KeyPairWriter
{
    public const string DefaultFileName = "holysheet.private.key";

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

    public static string CreateForConfig(HolySheetConfig config)
    {
        string path = Path.Combine(config.BaseDirectory, DefaultFileName);
        string publicKey = Create(path);
        config.Signing.PublicKey = publicKey;
        config.Signing.PrivateKeyFile = DefaultFileName;
        return path;
    }

    public static bool IsIgnored(string directory, string entry = DefaultFileName)
    {
        string path = Path.Combine(directory, ".gitignore");
        return File.Exists(path) && File.ReadAllLines(path).Any(line => line.Trim() is var text && (text == entry || text == "/" + entry || text == "*.private.key"));
    }

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
