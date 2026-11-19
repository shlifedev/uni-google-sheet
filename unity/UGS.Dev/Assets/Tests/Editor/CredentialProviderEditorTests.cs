using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UGS.Core.Sources;
using UGS.Editor;

namespace UGS.Dev.Tests
{
    /// <summary>Credential providers are found in the editor's assemblies by themselves, which only a real editor can show.</summary>
    public class CredentialProviderEditorTests
    {
        [UgsCredentialProvider("ugs-test-provider")]
        public sealed class NamedTestProvider : IGoogleCredentialProvider
        {
            public Task<GoogleCredential> GetCredentialAsync(GoogleCredentialRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(GoogleCredential.FromAccessToken("token"));
        }

        public sealed class UnnamedTestProvider : IGoogleCredentialProvider
        {
            public Task<GoogleCredential> GetCredentialAsync(GoogleCredentialRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(GoogleCredential.None);
        }

        public abstract class AbstractTestProvider : IGoogleCredentialProvider
        {
            public abstract Task<GoogleCredential> GetCredentialAsync(GoogleCredentialRequest request, CancellationToken cancellationToken);
        }

        [Test]
        public void Provider_classes_are_listed_by_their_name_without_registration()
        {
            var names = UgsProject.CredentialProviderNames();
            CollectionAssert.Contains(names, "ugs-test-provider");
            CollectionAssert.Contains(names, nameof(UnnamedTestProvider));
            CollectionAssert.DoesNotContain(names, nameof(NamedTestProvider));
            CollectionAssert.DoesNotContain(names, nameof(AbstractTestProvider));
        }
    }
}
