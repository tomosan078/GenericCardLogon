using System;
using System.Security.Cryptography;
using System.Text;

namespace GenericCardLogon.Core
{
    public static class CardHash
    {
        public static string ComputeHash(string idm)
        {
            if (string.IsNullOrWhiteSpace(idm))
                throw new ArgumentException("IDm is required.", nameof(idm));

            var normalized = idm.Trim().ToUpperInvariant();
            if (normalized.Length != 16)
                throw new ArgumentException("FeliCa IDm must be 16 hexadecimal characters.", nameof(idm));

            for (var i = 0; i < normalized.Length; i++)
            {
                var c = normalized[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
                    throw new ArgumentException("FeliCa IDm must contain only hexadecimal characters.", nameof(idm));
            }

            var input = Encoding.UTF8.GetBytes("FeliCa:" + normalized);
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(input))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }
}
