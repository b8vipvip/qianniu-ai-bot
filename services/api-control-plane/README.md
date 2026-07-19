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

## Ubuntu 部署

```bash
sudo apt-get update
sudo apt-get install -y docker.io docker-compose-plugin git
sudo systemctl enable --now docker

git clone https://github.com/b8vipvip/qianniu-ai-bot.git
cd qianniu-ai-bot
# PR 验证阶段使用下行；合并后可省略。
git checkout feat/api-control-plane
cd services/api-control-plane
cp .env.example .env
```

编辑 `.env`，至少修改域名、管理员密码、`APP_SECRET` 和 `API_KEY_ENCRYPTION_KEY`，然后启动：

```bash
docker compose up -d --build
docker compose ps
docker compose logs -f control-plane
```

浏览器访问 `https://你的域名/`。

## 首次配置

1. 登录后台并新增上游供应商。
2. 填写中转站 `BaseUrl` 和 ApiKey。
3. 打开“深度测试”，勾选需要验证的协议和视觉项。
4. 测试结束后确认主模型、备用模型和协议能力已自动更新。
5. 在“Bot 客户端”创建一枚令牌。
6. 在 Windows Bot 的“API 服务连接”中填写 `https://你的域名` 和该令牌。

## 备份

必须同时备份：

```text
data/api-control-plane.db
.env 中的 API_KEY_ENCRYPTION_KEY
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
