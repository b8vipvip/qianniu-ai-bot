using System;
using System.Security.Cryptography;
using System.Text;

namespace Bot.ShopScope
{
    internal static class ShopKeyGenerator
    {
        private const int DigestCharacters = 12;

        public static string Create(string platform, string sellerIdentity)
        {
            platform = NormalizeRequired(platform, nameof(platform)).ToLowerInvariant();
            sellerIdentity = NormalizeRequired(sellerIdentity, nameof(sellerIdentity));
            var canonical = platform + ":" + sellerIdentity;
            string digest;
            using (var sha = SHA256.Create())
            {
                digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
            return Prefix(platform) + "_" + digest.Substring(0, DigestCharacters);
        }

        public static string NormalizeFallbackNickname(string nickname)
        {
            return NormalizeRequired(nickname, nameof(nickname))
                .Normalize(NormalizationForm.FormKC)
                .ToLowerInvariant();
        }

        private static string Prefix(string platform)
        {
            if (string.Equals(platform, ShopContext.QianniuPlatform, StringComparison.Ordinal))
                return "qn";

            var builder = new StringBuilder();
            foreach (var ch in platform)
            {
                var lower = char.ToLowerInvariant(ch);
                if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9'))
                    builder.Append(lower);
                if (builder.Length >= 8) break;
            }
            return builder.Length >= 2 ? builder.ToString() : "shop";
        }

        private static string NormalizeRequired(string value, string name)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) throw new ArgumentException("Value is required.", name);
            return value;
        }
    }
}
