using DbEntity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DbEntity.Response
{
    public class TaobaoItemSellerGetResponse
    {
        public string api { get; set; }
        public TaobaoItemSellerData data { get; set; }
    }

    public class TaobaoItemSellerData
    {
        // GitHub Actions 编译环境中仓库里的 TopSdk.dll 是空文件，无法作为元数据引用。
        // 这里仅作为接口响应 DTO 使用，不需要强依赖 Top.Api.Domain.Item。
        public object firstResult { get; set; }
        public object model { get; set; }
        public bool error { get; set; }
    }
}