using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using HolySheet.Core.Sources;
using HolySheet.Editor;

namespace HolySheet.Dev.Tests
{
    public class CredentialProviderEditorTests
    {
        [HolySheetCredentialProvider("holysheet-test-provider")]
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
            var names = HolySheetProject.CredentialProviderNames();
            CollectionAssert.Contains(names, "holysheet-test-provider");
            CollectionAssert.Contains(names, nameof(UnnamedTestProvider));
            CollectionAssert.DoesNotContain(names, nameof(NamedTestProvider));
            CollectionAssert.DoesNotContain(names, nameof(AbstractTestProvider));
        }
    }
}
