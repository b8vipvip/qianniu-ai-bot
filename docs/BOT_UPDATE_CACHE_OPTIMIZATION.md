# Bot 更新检查与下载加速

## 目标

解决 Windows Bot 在“关于与更新”中检查版本时，因串行扫描多个 GitHub Release 和多个 `update.json` 而等待数分钟的问题。

## 新调用链

### 检查更新

1. Windows Bot 优先请求已配置的控制面：

   ```text
   GET /api/public/v1/bot-update/latest
   ```

2. 客户端等待上限为 6 秒。
3. 服务端通常直接返回 5 分钟内的内存/磁盘缓存。
4. 服务端内部只查询 GitHub `releases/latest`，并只读取这个版本的一份 `update.json`。
5. 服务端不可用或超时时，Windows 客户端回退到 GitHub `releases/latest`，不再扫描最近 20 个版本。

### 下载安装包

1. Windows 优先下载 GitHub Release 中的 `qianniu-bot-x64.zip`。
2. GitHub 下载连接失败、长时间无数据或校验失败时，自动切换：

   ```text
   GET /api/public/v1/bot-update/download/<bot-v版本>
   ```

3. 服务端第一次收到该版本下载请求时，从 GitHub 拉取安装包并验证 `update.json` 中的 SHA-256。
4. 验证通过后缓存到 `/data/bot-update-cache/<tag>/qianniu-bot-x64.zip`。
5. 后续客户端直接复用服务端缓存；客户端仍会再次执行 SHA-256 校验。

## 服务端缓存策略

默认配置：

```text
BOT_UPDATE_CACHE_DIR=/data/bot-update-cache
BOT_UPDATE_METADATA_CACHE_SECONDS=300
BOT_UPDATE_METADATA_STALE_SECONDS=86400
BOT_UPDATE_GITHUB_TIMEOUT_SECONDS=12
BOT_UPDATE_PACKAGE_TIMEOUT_SECONDS=600
BOT_UPDATE_KEEP_PACKAGE_VERSIONS=3
```

- 最新版本元数据每 5 分钟刷新；
- GitHub 临时不可用时，可返回 24 小时内最后一次成功缓存；
- 安装包镜像保留最近 3 个版本；
- 缓存位于现有 `/data` 持久卷中，会随控制面数据一起保留和备份；
- 版本标签必须匹配 `bot-v*`；
- 安装包必须通过 SHA-256 和已知大小验证后才会进入正式缓存。

## 超时边界

Windows 检查更新：

```text
控制面元数据：6 秒
GitHub latest：12 秒
GitHub update.json：8 秒
```

Windows 下载：

```text
建立下载连接：20 秒
连续无数据：45 秒后切换下一来源
```

因此 GitHub 网络异常时，不会再因多个 Release 的串行超时累计等待 3～7 分钟。

## 部署顺序

```text
1. 更新 API Control Plane 服务端
2. 验证 /api/public/v1/bot-update/latest
3. 合并并发布 Windows Bot 正式版
4. Windows 客户端从“关于与更新”检查新版本
```

新版 Windows 客户端在服务端尚未更新时仍会回退 GitHub `releases/latest`，不会失去更新能力。

## 安全边界

- 公共版本接口不返回任何客户端令牌、店铺数据或服务端密钥；
- 服务端只允许下载已经通过 GitHub latest Release 与 `update.json` 验证的版本；
- 任意路径标签、非 `bot-v*` 标签和无效 SHA-256 会在写盘前拒绝；
- 服务端和 Windows 客户端各自校验一次 SHA-256；
- 原有更新前备份、启动验证与失败自动回滚继续保留。
