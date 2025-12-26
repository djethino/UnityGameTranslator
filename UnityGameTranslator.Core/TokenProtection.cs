using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Provides secure token storage using platform-appropriate encryption.
    /// On Windows: Uses DPAPI (Data Protection API) for current user scope.
    /// On other platforms: Uses a machine-specific key derived from hardware ID.
    /// </summary>
    public static class TokenProtection
    {
        private const string TokenPrefix = "ENCRYPTED:";
        private const string LegacyPrefix = "ugt_"; // Plain tokens from older versions

        /// <summary>
        /// Encrypt a token for secure storage in config file.
        /// </summary>
        public static string EncryptToken(string plainToken)
        {
            if (string.IsNullOrEmpty(plainToken))
                return null;

            try
            {
#if NETSTANDARD2_0 || NET472 || NET48
                // Use DPAPI on Windows (available in .NET Framework and .NET Standard on Windows)
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    byte[] plainBytes = Encoding.UTF8.GetBytes(plainToken);
                    byte[] encryptedBytes = ProtectedData.Protect(
                        plainBytes,
                        GetEntropyBytes(),
                        DataProtectionScope.CurrentUser
                    );
                    return TokenPrefix + Convert.ToBase64String(encryptedBytes);
                }
#endif
                // Fallback: Simple XOR obfuscation with machine-specific key
                // Not as secure as DPAPI but better than plaintext
                return TokenPrefix + SimpleObfuscate(plainToken);
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TokenProtection] Encryption failed, storing as-is: {ex.Message}");
                return plainToken;
            }
        }

        /// <summary>
        /// Decrypt a token from config file.
        /// Handles both encrypted and legacy plaintext tokens.
        /// </summary>
        public static string DecryptToken(string storedToken)
        {
            if (string.IsNullOrEmpty(storedToken))
                return null;

            // Handle legacy plaintext tokens (from before encryption was added)
            if (storedToken.StartsWith(LegacyPrefix))
            {
                TranslatorCore.LogInfo("[TokenProtection] Legacy plaintext token detected, will be encrypted on next save");
                return storedToken;
            }

            // Not encrypted
            if (!storedToken.StartsWith(TokenPrefix))
            {
                return storedToken;
            }

            try
            {
                string encryptedPart = storedToken.Substring(TokenPrefix.Length);

#if NETSTANDARD2_0 || NET472 || NET48
                // Use DPAPI on Windows
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    byte[] encryptedBytes = Convert.FromBase64String(encryptedPart);
                    byte[] plainBytes = ProtectedData.Unprotect(
                        encryptedBytes,
                        GetEntropyBytes(),
                        DataProtectionScope.CurrentUser
                    );
                    return Encoding.UTF8.GetString(plainBytes);
                }
#endif
                // Fallback: Simple XOR deobfuscation
                return SimpleDeobfuscate(encryptedPart);
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TokenProtection] Decryption failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Check if a stored token needs to be re-encrypted (e.g., legacy plaintext)
        /// </summary>
        public static bool NeedsReEncryption(string storedToken)
        {
            if (string.IsNullOrEmpty(storedToken))
                return false;

            // Legacy plaintext tokens need to be encrypted
            if (storedToken.StartsWith(LegacyPrefix))
                return true;

            // Not encrypted at all
            if (!storedToken.StartsWith(TokenPrefix))
                return true;

            return false;
        }

        /// <summary>
        /// Additional entropy for DPAPI (makes it harder to decrypt even with same user context)
        /// </summary>
        private static byte[] GetEntropyBytes()
        {
            // Use a fixed entropy specific to this application
            return Encoding.UTF8.GetBytes("UnityGameTranslator_v1_TokenEntropy");
        }

        /// <summary>
        /// Simple XOR obfuscation for non-Windows platforms.
        /// Not cryptographically secure, but better than plaintext.
        /// </summary>
        private static string SimpleObfuscate(string plainText)
        {
            byte[] key = GetMachineKey();
            byte[] data = Encoding.UTF8.GetBytes(plainText);
            byte[] result = new byte[data.Length];

            for (int i = 0; i < data.Length; i++)
            {
                result[i] = (byte)(data[i] ^ key[i % key.Length]);
            }

            return Convert.ToBase64String(result);
        }

        /// <summary>
        /// Simple XOR deobfuscation for non-Windows platforms.
        /// </summary>
        private static string SimpleDeobfuscate(string obfuscated)
        {
            byte[] key = GetMachineKey();
            byte[] data = Convert.FromBase64String(obfuscated);
            byte[] result = new byte[data.Length];

            for (int i = 0; i < data.Length; i++)
            {
                result[i] = (byte)(data[i] ^ key[i % key.Length]);
            }

            return Encoding.UTF8.GetString(result);
        }

        /// <summary>
        /// Get a machine-specific key for obfuscation.
        /// Uses machine name + username as seed.
        /// </summary>
        private static byte[] GetMachineKey()
        {
            string seed = Environment.MachineName + "_" + Environment.UserName + "_UGT";
            using (var sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(seed));
            }
        }
    }
}
