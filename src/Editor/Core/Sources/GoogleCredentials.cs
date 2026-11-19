#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HolySheet.Core.Config;

namespace HolySheet.Core.Sources
{
    /// <summary>
    /// Supplies the credentials for Google sources whose auth is <c>{ "type": "custom", "provider": "&lt;name&gt;" }</c>,
    /// for projects that keep them somewhere HolySheet does not read on its own: a secret manager, the OS keychain, a company token service.
    /// <para>
    /// In the Unity editor a provider needs no registration: any non-abstract class with a public parameterless constructor
    /// that implements this interface is found in the editor's assemblies. Its name is the class name, or the one given
    /// with <see cref="HolySheetCredentialProviderAttribute"/>. Elsewhere, hand the instances to <see cref="TableBuildOptions.CredentialProviders"/>.
    /// </para>
    /// <para>
    /// A new instance is made for every build, and it is called on a background thread, once per source that names it
    /// (a Drive folder asks once for all of its files). Do not use Unity's API from it.
    /// </para>
    /// </summary>
    public interface IGoogleCredentialProvider
    {
        Task<GoogleCredential> GetCredentialAsync(GoogleCredentialRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Names a credential provider in the config. Without it the provider's name is its class name.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class HolySheetCredentialProviderAttribute : Attribute
    {
        public HolySheetCredentialProviderAttribute(string? name = null)
        {
            Name = name;
        }

        public string? Name { get; }
    }

    /// <summary>What a credential provider is asked for.</summary>
    public sealed class GoogleCredentialRequest
    {
        internal GoogleCredentialRequest(SourceConfig source, string baseDirectory, string scopes, HttpClient http)
        {
            Source = source;
            BaseDirectory = baseDirectory;
            Scopes = scopes;
            Http = http;
        }

        /// <summary>The source that needs the credentials: its spreadsheet or folder id, its name and its auth settings.</summary>
        public SourceConfig Source { get; }

        /// <summary>The folder of the config file, which relative paths in the config are resolved against.</summary>
        public string BaseDirectory { get; }

        /// <summary>The OAuth scopes the token must carry, separated by spaces (read-only Drive and Sheets).</summary>
        public string Scopes { get; }

        /// <summary>The build's HTTP client, for providers that exchange something for a token.</summary>
        public HttpClient Http { get; }
    }

    /// <summary>The credentials a provider returns. Create one with the static methods.</summary>
    public sealed class GoogleCredential
    {
        private GoogleCredential(string? apiKey, string? accessToken, string? serviceAccountKeyJson, string? account)
        {
            ApiKey = apiKey;
            AccessToken = accessToken;
            ServiceAccountKeyJson = serviceAccountKeyJson;
            Account = account;
        }

        /// <summary>No credentials: the spreadsheet is read as a public link.</summary>
        public static GoogleCredential None { get; } = new GoogleCredential(null, null, null, null);

        /// <summary>A Google API key. It reads only link-shared files.</summary>
        public static GoogleCredential FromApiKey(string apiKey) => new GoogleCredential(Require(apiKey, nameof(apiKey)), null, null, null);

        /// <summary>
        /// An OAuth access token with the scopes in <see cref="GoogleCredentialRequest.Scopes"/>.
        /// <paramref name="account"/> is the e-mail address it belongs to, shown in messages that say whom to share a file with.
        /// </summary>
        public static GoogleCredential FromAccessToken(string accessToken, string? account = null) => new GoogleCredential(null, Require(accessToken, nameof(accessToken)), null, account);

        /// <summary>The contents of a service account's JSON key, kept somewhere other than a file. HolySheet signs and exchanges it for a token.</summary>
        public static GoogleCredential FromServiceAccountKey(string keyJson) => new GoogleCredential(null, null, Require(keyJson, nameof(keyJson)), null);

        public string? ApiKey { get; }

        public string? AccessToken { get; }

        public string? ServiceAccountKeyJson { get; }

        public string? Account { get; }

        private static string Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("The value is empty.", name);
            }

            return value.Trim();
        }
    }

    /// <summary>Finds and names credential providers.</summary>
    public static class GoogleCredentialProviders
    {
        /// <summary>The name a provider class is chosen by in the config.</summary>
        public static string NameOf(Type type)
        {
            Guard.NotNull(type, nameof(type));
            string? name = type.GetCustomAttribute<HolySheetCredentialProviderAttribute>(inherit: false)?.Name;
            return string.IsNullOrWhiteSpace(name) ? type.Name : name!.Trim();
        }

        /// <summary>True for a class that can be made into a provider: not abstract, implements the interface, has a public parameterless constructor.</summary>
        public static bool IsProvider(Type type)
        {
            Guard.NotNull(type, nameof(type));
            return type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters && typeof(IGoogleCredentialProvider).IsAssignableFrom(type)
                && type.GetConstructor(Type.EmptyTypes) != null;
        }

        /// <summary>
        /// Makes one provider from each provider class among <paramref name="types"/>. Classes marked with
        /// <see cref="HolySheetCredentialProviderAttribute"/> that cannot be used, and constructors that throw, are reported
        /// in <paramref name="problems"/> and skipped.
        /// </summary>
        public static IReadOnlyList<IGoogleCredentialProvider> Create(IEnumerable<Type> types, ICollection<string>? problems = null)
        {
            Guard.NotNull(types, nameof(types));
            var providers = new List<IGoogleCredentialProvider>();
            foreach (Type type in types.Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (!IsProvider(type))
                {
                    if (type.IsDefined(typeof(HolySheetCredentialProviderAttribute), inherit: false))
                    {
                        problems?.Add($"{type.FullName} is marked as a credential provider but is not used: it must be a non-abstract class that implements {nameof(IGoogleCredentialProvider)} and has a public parameterless constructor.");
                    }

                    continue;
                }

                try
                {
                    providers.Add((IGoogleCredentialProvider)Activator.CreateInstance(type)!);
                }
                catch (TargetInvocationException exception)
                {
                    problems?.Add($"The credential provider {type.FullName} could not be created: {(exception.InnerException ?? exception).Message}");
                }
            }

            return providers;
        }

        /// <summary>The provider called <paramref name="name"/> (case does not matter), or an error that lists the ones there are.</summary>
        internal static IGoogleCredentialProvider Find(IReadOnlyList<IGoogleCredentialProvider>? providers, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new GoogleSourceException("No credential provider is named.", "Set auth.provider to the name of a class that implements IGoogleCredentialProvider.");
            }

            List<IGoogleCredentialProvider> matches = (providers ?? Array.Empty<IGoogleCredentialProvider>())
                .Where(provider => string.Equals(NameOf(provider.GetType()), name!.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new GoogleSourceException($"{matches.Count} credential providers are named '{name}': {string.Join(", ", matches.Select(provider => provider.GetType().FullName))}.",
                    "Give each a different name with [HolySheetCredentialProvider(\"name\")].");
            }

            List<string> known = (providers ?? Array.Empty<IGoogleCredentialProvider>()).Select(provider => NameOf(provider.GetType())).OrderBy(known => known, StringComparer.Ordinal).ToList();
            throw new GoogleSourceException($"There is no credential provider named '{name}'" + (known.Count == 0 ? "." : $"; there are: {string.Join(", ", known)}."),
                providers == null
                    ? "Custom providers are C# classes, so only the Unity editor can use them. Here, use auth type \"command\" with a command that prints the token, or build with --offline from the snapshot."
                    : "Add a class that implements IGoogleCredentialProvider to an editor assembly, or fix auth.provider. The name is the class name, or the one in [HolySheetCredentialProvider(\"name\")].");
        }
    }

    /// <summary>
    /// Runs the command of auth type <c>command</c> and reads the credentials it prints: an access token, a JSON object with
    /// <c>access_token</c>, or a service account's JSON key. Works the same in the editor, the CLI and CI, the way git's
    /// credential helpers do.
    /// </summary>
    internal static class CredentialCommand
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        public static async Task<GoogleCredential> RunAsync(string? command, string workingDirectory, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new GoogleSourceException("No credential command is configured.", "Set auth.command to a command that prints an access token, for example \"gcloud auth print-access-token\".");
            }

            (int exitCode, string output, string error) = await Task.Run(() => Run(command!, workingDirectory, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                string detail = FirstLine(error) ?? FirstLine(output) ?? "";
                throw new GoogleSourceException($"The credential command '{command}' failed with exit code {exitCode}" + (detail.Length > 0 ? ": " + detail : "."),
                    exitCode == 127 || exitCode == 9009
                        ? "The program was not found. The Unity editor does not see the PATH of your terminal when it was not started from one: use the program's full path."
                        : "Run the command in a terminal to see what it needs (a login, a permission).");
            }

            return Parse(output, command!);
        }

        internal static GoogleCredential Parse(string output, string command)
        {
            string text = output.Trim();
            if (text.StartsWith("{", StringComparison.Ordinal))
            {
                JsonObject? json;
                try
                {
                    json = JsonValue.Parse(text).AsObject;
                }
                catch (JsonFormatException exception)
                {
                    throw new GoogleSourceException($"The credential command '{command}' printed JSON that could not be read: {exception.Message}", "Print an access token, a JSON object with access_token, or a service account's JSON key.");
                }

                if (json?.GetString("private_key") != null)
                {
                    return GoogleCredential.FromServiceAccountKey(text);
                }

                if (json?.GetString("access_token") is { Length: > 0 } token)
                {
                    return GoogleCredential.FromAccessToken(token, json.GetString("account"));
                }

                throw new GoogleSourceException($"The credential command '{command}' printed JSON without access_token or private_key.", "Print an access token, a JSON object with access_token, or a service account's JSON key.");
            }

            if (text.Length == 0 || text.Any(char.IsWhiteSpace))
            {
                throw new GoogleSourceException($"The credential command '{command}' printed " + (text.Length == 0 ? "nothing." : "more than one token."),
                    "The command must print only the access token (or a JSON object). Send anything else to stderr.");
            }

            return GoogleCredential.FromAccessToken(text);
        }

        private static (int ExitCode, string Output, string Error) Run(string command, string workingDirectory, CancellationToken cancellationToken)
        {
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var start = new ProcessStartInfo(windows ? "cmd.exe" : "/bin/sh", windows ? "/c " + command : "-c " + QuoteArgument(command))
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            Process process;
            try
            {
                process = Process.Start(start) ?? throw new Win32Exception("the process did not start");
            }
            catch (Win32Exception exception)
            {
                throw new GoogleSourceException($"The credential command '{command}' could not be started: {exception.Message}", "Check auth.command.");
            }

            using (process)
            {
                // Nothing is typed into the command: a prompt for input ends instead of waiting until the timeout.
                process.StandardInput.Close();
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                var watch = Stopwatch.StartNew();
                while (!process.WaitForExit(100))
                {
                    if (cancellationToken.IsCancellationRequested || watch.Elapsed > Timeout)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                            // It ended on its own in the meantime.
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        throw new GoogleSourceException($"The credential command '{command}' did not finish within {Timeout.TotalSeconds:F0} seconds.",
                            "Run it in a terminal: it may be waiting for a login in a browser. Log in there first.");
                    }
                }

                // The process has exited, so both streams end and the reads complete.
                return (process.ExitCode, output.Result, error.Result);
            }
        }

        // Quotes one argument the way the process starter splits Arguments (the Windows command-line convention, which
        // .NET and Mono also use on other systems), so the shell receives the command exactly as written.
        private static string QuoteArgument(string value)
        {
            var builder = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                builder.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                builder.Append(character);
            }

            builder.Append('\\', backslashes * 2);
            return builder.Append('"').ToString();
        }

        private static string? FirstLine(string text)
        {
            string? line = text.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0);
            return line == null ? null : line.Length > 300 ? line.Substring(0, 300) + "…" : line;
        }
    }
}
