#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace UGS
{
    /// <summary>
    /// Verifies that a manifest was produced by whoever holds the private key.
    /// The data files are covered indirectly: the manifest lists their hashes and the signature covers the manifest.
    /// The public key is safe to ship in the client; only the build machine needs the private key.
    /// </summary>
    public static class TableSignature
    {
        /// <summary>Prefix of the text form of a public key: <c>rsa:{base64 modulus}:{base64 exponent}</c>.</summary>
        public const string PublicKeyPrefix = "rsa:";

        public static bool Verify(TableManifest manifest, string publicKey)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            if (manifest.Signature == null)
            {
                return false;
            }

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(manifest.Signature);
            }
            catch (FormatException)
            {
                return false;
            }

            RSAParameters parameters = ParsePublicKey(publicKey);
            using (RSA rsa = RSA.Create())
            {
                rsa.ImportParameters(parameters);
                return rsa.VerifyData(Encoding.UTF8.GetBytes(manifest.SignedText), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }

        public static RSAParameters ParsePublicKey(string publicKey)
        {
            if (publicKey == null)
            {
                throw new ArgumentNullException(nameof(publicKey));
            }

            string[] parts = publicKey.Trim().Split(':');
            if (parts.Length != 3 || parts[0] + ":" != PublicKeyPrefix)
            {
                throw new FormatException("The public key must look like 'rsa:<modulus>:<exponent>'. Run 'ugs keygen' to create one.");
            }

            return new RSAParameters
            {
                Modulus = Convert.FromBase64String(parts[1]),
                Exponent = Convert.FromBase64String(parts[2])
            };
        }

        public static string FormatPublicKey(RSAParameters parameters)
        {
            if (parameters.Modulus == null || parameters.Exponent == null)
            {
                throw new ArgumentException("The key has no modulus or exponent.", nameof(parameters));
            }

            return PublicKeyPrefix + Convert.ToBase64String(parameters.Modulus) + ":" + Convert.ToBase64String(parameters.Exponent);
        }
    }
}
