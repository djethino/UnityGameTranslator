using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Provides secure token storage using platform-appropriate encryption.
    /// - Windows: Uses DPAPI (Data Protection API) for current user scope.
    /// - Linux: Uses secret-tool (libsecret) for GNOME Keyring / KDE Wallet.
    /// - macOS: Uses security CLI for Keychain access.
    /// - Fallback: AES-256 encryption with machine-derived key.
    /// </summary>
    public static class TokenProtection
    {
        private const string TokenPrefix = "ENCRYPTED:";
        private const string KeyringPrefix = "KEYRING:";
        private const string LegacyPrefix = "ugt_"; // Plain tokens from older versions

        // Keyring/Keychain identifiers
        private const string ServiceName = "UnityGameTranslator";
        private const string AccountName = "api_token";

        /// <summary>
        /// Encrypt a token for secure storage in config file.
        /// </summary>
        public static string EncryptToken(string plainToken)
        {
            if (string.IsNullOrEmpty(plainToken))
                return null;

            try
            {
                // Try platform-native secure storage first
                if (TryStoreInKeyring(plainToken))
                {
                    // Return a marker indicating token is in keyring
                    return KeyringPrefix + "stored";
                }

#if NETSTANDARD2_0 || NET472 || NET48
                // Use DPAPI on Windows
                if (IsWindows())
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
                // Fallback: AES-256 encryption with machine-derived key
                return TokenPrefix + AesEncrypt(plainToken);
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TokenProtection] Encryption failed, using fallback: {ex.Message}");
                // Last resort fallback
                return TokenPrefix + AesEncrypt(plainToken);
            }
        }

        /// <summary>
        /// Decrypt a token from config file.
        /// Handles keyring storage, encrypted, and legacy plaintext tokens.
        /// </summary>
        public static string DecryptToken(string storedToken)
        {
            if (string.IsNullOrEmpty(storedToken))
                return null;

            // Handle keyring storage marker
            if (storedToken.StartsWith(KeyringPrefix))
            {
                string keyringToken = TryRetrieveFromKeyring();
                if (!string.IsNullOrEmpty(keyringToken))
                {
                    return keyringToken;
                }
                TranslatorCore.LogWarning("[TokenProtection] Token marked as keyring but retrieval failed");
                return null;
            }

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
                if (IsWindows())
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
                // Fallback: AES decryption
                return AesDecrypt(encryptedPart);
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
            if (!storedToken.StartsWith(TokenPrefix) && !storedToken.StartsWith(KeyringPrefix))
                return true;

            return false;
        }

        /// <summary>
        /// Clear token from keyring when logging out
        /// </summary>
        public static void ClearKeyringToken()
        {
            try
            {
                if (IsLinux())
                {
                    RunProcess("secret-tool", $"clear service {ServiceName} account {AccountName}");
                }
                else if (IsMacOS())
                {
                    RunProcess("security", $"delete-generic-password -s \"{ServiceName}\" -a \"{AccountName}\"");
                }
            }
            catch
            {
                // Ignore errors on cleanup
            }
        }

        #region Platform-Specific Keyring

        private static bool TryStoreInKeyring(string token)
        {
            try
            {
                if (IsLinux())
                {
                    return StoreInLinuxKeyring(token);
                }
                else if (IsMacOS())
                {
                    return StoreInMacKeychain(token);
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TokenProtection] Keyring storage failed: {ex.Message}");
            }
            return false;
        }

        private static string TryRetrieveFromKeyring()
        {
            try
            {
                if (IsLinux())
                {
                    return RetrieveFromLinuxKeyring();
                }
                else if (IsMacOS())
                {
                    return RetrieveFromMacKeychain();
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TokenProtection] Keyring retrieval failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Store token in Linux Secret Service (GNOME Keyring / KDE Wallet)
        /// Requires: libsecret-tools package (secret-tool command)
        /// </summary>
        private static bool StoreInLinuxKeyring(string token)
        {
            // Check if secret-tool is available
            if (!IsCommandAvailable("secret-tool"))
            {
                TranslatorCore.LogInfo("[TokenProtection] secret-tool not available, using fallback encryption");
                return false;
            }

            // secret-tool reads the secret from stdin
            var result = RunProcessWithInput(
                "secret-tool",
                $"store --label=\"{ServiceName} API Token\" service {ServiceName} account {AccountName}",
                token
            );

            return result.ExitCode == 0;
        }

        private static string RetrieveFromLinuxKeyring()
        {
            if (!IsCommandAvailable("secret-tool"))
                return null;

            var result = RunProcess("secret-tool", $"lookup service {ServiceName} account {AccountName}");

            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
            {
                return result.Output.Trim();
            }
            return null;
        }

        /// <summary>
        /// Store token in macOS Keychain
        /// Uses built-in 'security' command
        /// </summary>
        private static bool StoreInMacKeychain(string token)
        {
            // Delete existing entry first (update doesn't work well)
            RunProcess("security", $"delete-generic-password -s \"{ServiceName}\" -a \"{AccountName}\" 2>/dev/null");

            // Add new entry
            var result = RunProcess(
                "security",
                $"add-generic-password -s \"{ServiceName}\" -a \"{AccountName}\" -w \"{EscapeForShell(token)}\" -U"
            );

            return result.ExitCode == 0;
        }

        private static string RetrieveFromMacKeychain()
        {
            var result = RunProcess(
                "security",
                $"find-generic-password -s \"{ServiceName}\" -a \"{AccountName}\" -w"
            );

            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
            {
                return result.Output.Trim();
            }
            return null;
        }

        #endregion

        #region AES Fallback Encryption

        /// <summary>
        /// AES-256 encryption for platforms without native secure storage.
        /// Uses PBKDF2 key derivation with machine-specific salt.
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
        /// Derive a 256-bit key using PBKDF2-SHA1 (compatible with .NET Standard 2.0)
        /// Note: Uses SHA1 as HMAC (still secure for key derivation) with high iteration count
        /// </summary>
        private static byte[] DeriveKey(string secret, byte[] salt)
        {
            // .NET Standard 2.0 only supports SHA1 for PBKDF2
            // This is still secure for key derivation when using sufficient iterations
            using (var pbkdf2 = new Rfc2898DeriveBytes(secret, salt, 100000))
            {
                return pbkdf2.GetBytes(32); // 256 bits
            }
        }

        /// <summary>
        /// Get machine-specific secret for key derivation.
        /// Combines multiple sources for better entropy.
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

            // Try to add more entropy from environment
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
        /// Get a stable salt derived from machine identity
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

        #region DPAPI Support

        /// <summary>
        /// Additional entropy for DPAPI (makes it harder to decrypt even with same user context)
        /// </summary>
        private static byte[] GetEntropyBytes()
        {
            // Generate entropy from multiple sources
            string entropySource = $"UnityGameTranslator_v2_{Environment.UserName}_{Environment.MachineName}";
            using (var sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(entropySource));
            }
        }

        #endregion

        #region Process Helpers

        private static bool IsCommandAvailable(string command)
        {
            try
            {
                var result = RunProcess("which", command);
                return result.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000); // 5 second timeout

                    return (process.ExitCode, output);
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TokenProtection] Process execution failed: {ex.Message}");
                return (-1, null);
            }
        }

        private static (int ExitCode, string Output) RunProcessWithInput(string fileName, string arguments, string input)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    process.Start();
                    process.StandardInput.Write(input);
                    process.StandardInput.Close();

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);

                    return (process.ExitCode, output);
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TokenProtection] Process execution failed: {ex.Message}");
                return (-1, null);
            }
        }

        private static string EscapeForShell(string input)
        {
            // Escape special characters for shell
            return input
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("$", "\\$")
                .Replace("`", "\\`");
        }

        #endregion

        #region Platform Detection

        /// <summary>
        /// Check if running on Windows (no external dependencies needed)
        /// </summary>
        private static bool IsWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }

        /// <summary>
        /// Check if running on Linux
        /// </summary>
        private static bool IsLinux()
        {
            return Environment.OSVersion.Platform == PlatformID.Unix && !IsMacOS();
        }

        /// <summary>
        /// Check if running on macOS
        /// </summary>
        private static bool IsMacOS()
        {
            // On .NET Standard 2.0, macOS reports as Unix
            // We detect macOS by checking for macOS-specific paths
            if (Environment.OSVersion.Platform != PlatformID.Unix)
                return false;

            try
            {
                // macOS has /System/Library, Linux doesn't
                return System.IO.Directory.Exists("/System/Library");
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}

