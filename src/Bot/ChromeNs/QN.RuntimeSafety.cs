using System.Collections.Generic;
using System.Linq;

namespace Bot.ChromeNs
{
    public partial class QN
    {
        internal static List<QN> GetRuntimeSafetySnapshot()
        {
            lock (QNSetLock)
            {
                return QNSet == null ? new List<QN>() : QNSet.Where(x => x != null).ToList();
            }
        }

        internal void CancelActiveBuyerGeneration(string seller, string buyer, string reason)
        {
            if (_buyerMessageBurstCoordinator == null) return;
            _buyerMessageBurstCoordinator.CancelBuyer(seller, buyer, reason);
        }
    }
}
