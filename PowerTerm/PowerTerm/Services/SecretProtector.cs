using System;
using System.Security.Cryptography;
using System.Text;

namespace PowerTerm.Services
{
    /// <summary>
    /// Wraps DPAPI so stored passwords and key passphrases are readable only by the
    /// Windows user that saved them, on the machine that saved them.
    /// </summary>
    internal static class SecretProtector
    {
        // Bound into the DPAPI blob; a blob from another app cannot be unwrapped as ours.
        //
        // Deliberately still the old name. This string is part of the key: change it and every
        // password already saved stops opening. Settings carried over from the earlier build have to
        // keep working, so this name is frozen where the product name is not.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("XCENA.Terminal.Profile.v1");

        public static string? Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
            {
                return null;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
            try
            {
                byte[] blob = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(blob);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        /// <summary>Returns null when the blob is missing, malformed, or was written by another user.</summary>
        public static string? Unprotect(string? protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64))
            {
                return null;
            }

            try
            {
                byte[] blob = Convert.FromBase64String(protectedBase64);
                byte[] bytes = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    return Encoding.UTF8.GetString(bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            catch (FormatException)
            {
                return null;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }
}
