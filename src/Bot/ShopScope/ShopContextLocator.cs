using Bot.ChromeNs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.ShopScope
{
    internal static class ShopContextLocator
    {
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);

        public static ShopContext ResolveBySellerNick(string sellerNick)
        {
            return Profiles.GetOrCreate(ResolveBySellerNickCore(sellerNick)).ToContext();
        }

        public static ShopContext ResolveRuntimeBySellerNick(string sellerNick)
        {
            return ResolveBySellerNickCore(sellerNick);
        }

        public static ShopContext ResolveCurrentForUi()
        {
            var current = QN.CurQN;
            if (current == null || current.Seller == null)
                throw new InvalidOperationException("当前没有可用的千牛卖家会话。" );
            var context = ShopIdentityResolver.Resolve(current.Seller);
            return Profiles.GetOrCreate(context).ToContext();
        }

        private static ShopContext ResolveBySellerNickCore(string sellerNick)
        {
            sellerNick = (sellerNick ?? string.Empty).Trim();
            if (sellerNick.Length == 0)
                throw new ArgumentException("卖家昵称不能为空。", nameof(sellerNick));

            var matches = SnapshotQns()
                .Where(x => x != null
                    && x.Seller != null
                    && string.Equals(
                        (x.Seller.Nick ?? string.Empty).Trim(),
                        sellerNick,
                        StringComparison.Ordinal))
                .Select(x => ShopIdentityResolver.Resolve(x.Seller))
                .GroupBy(x => x.ShopKey, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();

            if (matches.Count == 0)
            {
                var current = QN.CurQN;
                if (current != null
                    && current.Seller != null
                    && string.Equals(
                        (current.Seller.Nick ?? string.Empty).Trim(),
                        sellerNick,
                        StringComparison.Ordinal))
                {
                    matches.Add(ShopIdentityResolver.Resolve(current.Seller));
                }
            }

            if (matches.Count == 0)
                throw new InvalidOperationException("未找到卖家“" + sellerNick + "”对应的千牛登录身份。请先保持该店铺在线。" );
            if (matches.Count > 1)
                throw new InvalidOperationException("同一卖家昵称匹配到多个不同店铺身份，已阻止自动绑定。" );

            return matches[0];
        }

        private static IList<QN> SnapshotQns()
        {
            try
            {
                return QN.QNSet == null ? new List<QN>() : QN.QNSet.ToArray();
            }
            catch
            {
                return new List<QN>();
            }
        }
    }
}
