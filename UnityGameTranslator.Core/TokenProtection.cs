using System;
using Newtonsoft.Json;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// At-rest obfuscation of secrets in config.json, binding them to the machine.
    ///
    /// ⚠ The scheme itself now lives in UnityGameTranslator.Common.Secrets, with its threat model
    /// attached to it — read that before assuming anything is protected here. It was written twice,
    /// and every constant in it is part of the derived key: two copies drifting apart means a
    /// config file this mod can no longer read, reported to the player as being signed out.
    ///
    /// What stays here is what belongs to the mod and not to a shared library: the shape of OUR
    /// token ("ugt_"), which is how a token saved in the clear by an old version is recognised, and
    /// the logging, which goes through the mod loader.
    /// </summary>
    public static class TokenProtection
    {
        /// <summary>Plain tokens from versions that predate encryption.</summary>
        private const string LegacyPrefix = "ugt_";

        /// <summary>
        /// Encrypt a token for storage in the config file. Null for nothing to store — callers
        /// have always relied on that rather than on an empty string.
        /// </summary>
        public static string EncryptToken(string plainToken)
        {
            if (string.IsNullOrEmpty(plainToken))
                return null;

            return Secrets.Protect(plainToken);
        }

        /// <summary>
        /// Read a token back from the config file.
        ///
        /// Null means it was ours and could not be decrypted, which happens legitimately when the
        /// file came from another machine. The caller treats that as "no token" and asks the player
        /// to sign in again — the one thing it must not do is present it as corruption.
        /// </summary>
        public static string DecryptToken(string storedToken)
        {
            if (string.IsNullOrEmpty(storedToken))
                return null;

            // A token written before encryption existed. Said out loud because the next save
            // rewrites it, and a line in the log is what makes that traceable afterwards.
            if (storedToken.StartsWith(LegacyPrefix))
            {
                TranslatorCore.LogInfo("[TokenProtection] Legacy plaintext token detected, will be encrypted on next save");
                return storedToken;
            }

            if (Secrets.TryUnprotect(storedToken, out string plain, out string failure))
                return plain;

            TranslatorCore.LogWarning($"[TokenProtection] Decryption failed: {failure}");
            return null;
        }

        /// <summary>
        /// True when a stored value is a real secret sitting there unprotected — a legacy plaintext
        /// token, or anything else that arrived without our marker. The next save rewrites it.
        /// </summary>
        public static bool NeedsReEncryption(string storedToken) => Secrets.NeedsProtecting(storedToken);
    }

    /// <summary>
    /// JsonConverter that transparently encrypts string properties on serialization
    /// and decrypts on deserialization. Apply via [JsonConverter(typeof(EncryptedTokenConverter))]
    /// on a string property in a serialized class.
    ///
    /// In-memory the property holds the plaintext value (so callers can use it directly
    /// for HTTP requests etc.). On disk (config.json) the value is always AES-encrypted
    /// with the machine-derived key. Returns null on decryption failure — the caller is
    /// expected to detect this (raw JSON had a value, in-memory is null) to log and clear.
    ///
    /// ⚠ Stays in the mod rather than moving to the shared library: it is built on Newtonsoft.Json,
    /// and that library takes no packages at all. The rule it applies is shared; the plumbing that
    /// applies it is not.
    /// </summary>
    public class EncryptedTokenConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(string);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            string ciphertext = reader.Value as string;
            if (string.IsNullOrEmpty(ciphertext)) return null;
            // DecryptToken returns:
            //   - the plaintext if input is encrypted (ENCRYPTED: prefix) and key matches
            //   - the input unchanged if it's a legacy plaintext token (ugt_ prefix) or
            //     unprefixed (will be re-encrypted on next save)
            //   - null on cryptographic failure (machine identity changed, corrupted data)
            return TokenProtection.DecryptToken(ciphertext);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            string plaintext = value as string;
            if (string.IsNullOrEmpty(plaintext))
            {
                writer.WriteNull();
                return;
            }
            // EncryptToken always wraps with ENCRYPTED: prefix; round-trip via ReadJson is symmetric.
            writer.WriteValue(TokenProtection.EncryptToken(plaintext));
        }
    }
}
