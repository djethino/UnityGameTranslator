using System;
using System.Security.Cryptography;
using System.Text;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Provides secure token storage using AES-256 encryption with machine-derived key.
    /// Works on all platforms: Windows, Linux, macOS (Mono and IL2CPP).
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

            return TokenPrefix + AesEncrypt(plainToken);
        }

        /// <summary>
        /// Decrypt a token from config file.
        /// </summary>
        public static string DecryptToken(string storedToken)
        {
            if (string.IsNullOrEmpty(storedToken))
                return null;

            // Legacy plaintext token (from before encryption was added)
            if (storedToken.StartsWith(LegacyPrefix))
            {
                TranslatorCore.LogInfo("[TokenProtection] Legacy plaintext token detected, will be encrypted on next save");
                return storedToken;
            }

            // Not encrypted (shouldn't happen but handle gracefully)
            if (!storedToken.StartsWith(TokenPrefix))
            {
                return storedToken;
            }

            // Decrypt AES
            string encryptedPart = storedToken.Substring(TokenPrefix.Length);
            return AesDecrypt(encryptedPart);
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

        #region AES-256 Encryption

        /// <summary>
        /// AES-256-CBC encryption with machine-derived key.
        /// </summary>
        private static string AesEncrypt(string plainText)
        {
            byte[] salt = GetMachineSalt();
            byte[] key = DeriveKey(GetMachineSecret(), salt);

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var encryptor = aes.CreateEncryptor())
                {
                    byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                    byte[] encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

                    // Combine IV + encrypted data
                    byte[] result = new byte[aes.IV.Length + encryptedBytes.Length];
                    Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
                    Buffer.BlockCopy(encryptedBytes, 0, result, aes.IV.Length, encryptedBytes.Length);

                    return Convert.ToBase64String(result);
                }
            }
        }

        /// <summary>
        /// AES-256-CBC decryption with machine-derived key.
        /// </summary>
        private static string AesDecrypt(string encryptedText)
        {
            byte[] salt = GetMachineSalt();
            byte[] key = DeriveKey(GetMachineSecret(), salt);
            byte[] combined = Convert.FromBase64String(encryptedText);

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                // Extract IV (first 16 bytes)
                byte[] iv = new byte[16];
                Buffer.BlockCopy(combined, 0, iv, 0, 16);
                aes.IV = iv;

                // Extract encrypted data
                byte[] encryptedBytes = new byte[combined.Length - 16];
                Buffer.BlockCopy(combined, 16, encryptedBytes, 0, encryptedBytes.Length);

                using (var decryptor = aes.CreateDecryptor())
                {
                    byte[] plainBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
                    return Encoding.UTF8.GetString(plainBytes);
                }
            }
        }

        /// <summary>
        /// Derive a 256-bit key using PBKDF2-SHA1 (compatible with .NET Standard 2.0).
        /// 100,000 iterations provides strong protection against brute force.
        /// </summary>
        private static byte[] DeriveKey(string secret, byte[] salt)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(secret, salt, 100000))
            {
                return pbkdf2.GetBytes(32); // 256 bits
            }
        }

        /// <summary>
        /// Get machine-specific secret for key derivation.
        /// Combines multiple sources for entropy.
        /// </summary>
        private static string GetMachineSecret()
        {
            var sb = new StringBuilder();
            sb.Append(Environment.MachineName);
            sb.Append("_");
            sb.Append(Environment.UserName);
            sb.Append("_");
            sb.Append(Environment.OSVersion.Platform);
            sb.Append("_UGT_v2");

            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                {
                    sb.Append("_");
                    sb.Append(home.GetHashCode());
                }
            }
            catch { }

            return sb.ToString();
        }

        /// <summary>
        /// Get a stable salt derived from machine identity.
        /// </summary>
        private static byte[] GetMachineSalt()
        {
            string saltSource = $"{Environment.MachineName}_{Environment.UserName}_UGT_SALT";
            using (var sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(saltSource));
            }
        }

        #endregion
    }
}
