# 千牛 AI API 控制台

这是 Bot 的统一 API 控制面和 AI 网关。上游中转站地址、ApiKey、主模型、备用模型、协议能力与定时深测全部保存在 Ubuntu 服务端；Windows Bot 只保存控制台地址和独立客户端令牌。

## 主要能力

- 管理多个上游供应商、优先级和启停状态。
- 普通测试：只调用设定的主模型或指定模型。
- 深度测试：复用 `test_api_models_v4.py` 的模型发现、双根地址、Responses、Chat Completions、Legacy Completions、Responses Vision、Chat Vision 与中文报告能力。
- 深测后自动记录每个模型实际通过的协议。
- 主模型失效时自动选择该供应商版本号最新的可用模型。
- 其它文本可用模型写入备用模型池；视觉可用模型维护独立视觉池。
- 实际请求按“供应商 → 主模型/备用模型 → 该模型已验证协议”逐层回退。
- 每个供应商可设置每隔多少小时执行一次深度测试。
- 上游 ApiKey 使用 Fernet 加密保存，不返回给 Bot。
- Bot 客户端令牌可独立创建、停用和删除。
- 对 Bot 暴露 OpenAI 兼容的 `/v1/chat/completions` 和 `/v1/responses`。
- 支持企业微信自建应用消息转人工、加密回调、工单回复和 Windows Bot 安全领取。

## Ubuntu 部署

```bash
sudo apt-get update
sudo apt-get install -y docker.io docker-compose-plugin git
sudo systemctl enable --now docker

git clone https://github.com/b8vipvip/qianniu-ai-bot.git
cd qianniu-ai-bot
# PR 验证阶段使用下行；合并后可省略。
git checkout agent/reply-dedup-knowledge-navigation
cd services/api-control-plane
cp .env.example .env
```

编辑 `.env`，至少修改域名、管理员密码、`APP_SECRET` 和 `API_KEY_ENCRYPTION_KEY`。

使用项目 Caddy：

```bash
docker compose up -d --build
docker compose ps
docker compose logs -f control-plane
```

使用宝塔 Nginx：

```bash
docker compose -f docker-compose.bt.yml up -d --build --force-recreate
docker compose -f docker-compose.bt.yml ps
curl -fsS http://127.0.0.1:18081/healthz
```

宝塔反向代理目标为 `http://127.0.0.1:18081`。不要在 Compose 中覆盖 `command`；容器必须运行 `bootstrap.py`，否则企业微信桥接路由不会注册。

浏览器访问 `https://你的域名/`。

## 首次配置

1. 登录后台并新增上游供应商。
2. 填写中转站 `BaseUrl` 和 ApiKey。
3. 打开“深度测试”，勾选需要验证的协议和视觉项。
4. 测试结束后确认主模型、备用模型和协议能力已自动更新。
5. 在“Bot 客户端”创建一枚令牌。
6. 在 Windows Bot 的“API 服务连接”中填写 `https://你的域名` 和该令牌。

## 企业微信应用消息转人工

### 1. Ubuntu `.env`

```dotenv
WECOM_APP_ENABLED=true
WECOM_CORP_ID=企业ID
WECOM_APP_SECRET=自建应用Secret
WECOM_AGENT_ID=自建应用AgentId
WECOM_TO_USERS=接收成员UserID
WECOM_CALLBACK_TOKEN=企业微信回调页面填写的Token
WECOM_CALLBACK_AES_KEY=企业微信回调页面生成的43位EncodingAESKey
WECOM_ALLOWED_REPLY_USERS=允许处理工单的成员UserID
WECOM_TICKET_HOURS=24
```

多个成员 UserID 使用 `|` 分隔。`WECOM_ALLOWED_REPLY_USERS` 留空时，仅允许 `WECOM_TO_USERS` 中的成员回复。

CorpSecret、回调 Token、EncodingAESKey 只能保存在 Ubuntu `.env`，不得放入 Windows `params.db`、GitHub、日志或截图。

### 2. 企业微信后台配置接收消息

进入自建应用的“接收消息”设置，填写：

```text
URL: https://你的域名/api/wecom/callback
Token: 与 WECOM_CALLBACK_TOKEN 完全相同
EncodingAESKey: 与 WECOM_CALLBACK_AES_KEY 完全相同
```

先部署并启动新版容器，再点击企业微信后台的保存；企业微信会对该 URL 发起签名验证。

### 3. 消息和回复格式

转人工通知会生成唯一工单：

```text
工单：QN-A1B2C3D4
回复格式：QN-A1B2C3D4 这里填写给买家的回复
```

个人微信插件或企业微信应用会话中的人工回复必须保留工单号，例如：

```text
QN-A1B2C3D4 已为您提交人工核查，请稍候。
```

必须带工单号，因为企业微信入站消息不提供可直接映射到千牛买家的业务字段。服务端使用工单绑定创建通知的 Bot 客户端、客服账号、买家和原始问题，避免多买家串单。

### 4. 双向处理流程

```text
买家消息命中转人工规则
→ Windows Bot 调用 Ubuntu 创建工单
→ Ubuntu 向企业微信自建应用发送通知
→ 人工在企业微信或个人微信插件按工单格式回复
→ 企业微信加密回调到 Ubuntu
→ Ubuntu 校验签名、解密并进入待发送队列
→ 创建工单的 Windows Bot 使用客户端令牌领取任务
→ Bot 切换并确认目标买家后发送人工回复
→ 发送成功后调用 AI 结合该买家时间线整理知识
→ 新增或用人工确认答案更新本地知识库
```

只有千牛实际发送成功后才进入知识学习队列。学习过程会脱敏手机号、订单号等内容，并按问题和内容去重。人工回复来源标记为 `人工回复-企业微信应用`。

### 5. 安全约束

- 回调使用企业微信签名校验和 AES-CBC 加密解密。
- 仅允许配置的成员 UserID 提交工单回复。
- 仅创建工单的 Bot 客户端令牌可以领取该工单。
- 每个任务使用短租约，失败会重试，最多记录五次明确发送失败。
- 工单默认 24 小时过期。
- 当前只接受文本回复。
- 不带工单号、工单过期、成员无权限或工单不属于该成员时均拒绝转发。

## 备份

必须同时备份：

```text
data/api-control-plane.db
.env 中的 API_KEY_ENCRYPTION_KEY
.env 中的企业微信应用与回调配置
```

只备份数据库但丢失加密密钥将无法恢复上游 ApiKey。

## 迁移现有测试结果

后台深测会重新验证，不直接信任旧报告中的 HTTP 200。你上传的报告可作为初始参考：

- `gpt-5.5` 已验证支持 Responses API 和 Chat Completions。
- `/v1` 根地址有效，无 `/v1` 的路径会返回网页或连接重置。
- 当前中转站存在 WAF/TLS 指纹差异，因此服务端统一使用 `curl_cffi` 的 Chrome impersonation。

## 请求容错顺序

运行时不会只依赖一种路径或协议。每次调用按以下顺序尝试：

1. 启用的供应商优先级；
2. 供应商主模型及备用模型；
3. 深度测试实际成功过的请求协议；
4. 深度测试实际成功过的完整 URL；
5. `/v1` 和无 `/v1` 两种根地址兜底。

只有全部供应商、模型、协议和地址均失败时，网关才向 Bot 返回失败。
