# Bot 更新检查与下载加速

## 目标

解决 Windows Bot 在“关于与更新”中检查版本时，因串行扫描多个 GitHub Release 和多个 `update.json` 而等待数分钟的问题，同时让安装包默认从腾讯云 API Control Plane 镜像下载，GitHub 作为备用源。

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

1. Windows **优先从腾讯云 API Control Plane 镜像**下载：

   ```text
   GET /api/public/v1/bot-update/download/<bot-v版本>
   ```

2. 腾讯云控制台服务器连接失败、长时间无数据或 SHA-256 校验失败时，自动回退 GitHub Release/CDN 中的 `qianniu-bot-x64.zip`。
3. 服务端镜像若尚未缓存该正式版本，会从 GitHub 拉取安装包并验证 `update.json` 中的 SHA-256 与文件大小。
4. 验证通过后缓存到 `/data/bot-update-cache/<tag>/qianniu-bot-x64.zip`。
5. 后续客户端直接复用腾讯云服务端缓存；客户端仍会再次执行 SHA-256 校验。

下载优先级固定为：

```text
腾讯云 API Control Plane → GitHub Release/CDN
```

## GitHub 编译成功后的自动镜像预热

当前仓库已经具备完整自动链路，不需要 Windows 客户端第一次点击下载才触发腾讯云服务器拉包：

```text
master 提交
  ↓
Windows x64 release build 成功
  ↓
Publish Bot auto-update release 自动运行
  ↓
生成 bot-v* GitHub Release
  ↓
发布 qianniu-bot-x64.zip + update.json
  ↓
腾讯云 API Control Plane 后台 prefetch 自动发现新 Release
  ↓
服务端主动从 GitHub 拉取安装包
  ↓
SHA-256 / 文件大小校验
  ↓
写入 /data/bot-update-cache/<tag>/
  ↓
Windows 客户端优先从腾讯云下载
```

需要注意：这套机制是**服务端定时主动拉取**，不是 GitHub webhook 直接推送。默认 `BOT_UPDATE_PREFETCH_POLL_SECONDS=60`，但最新 Release 元数据默认缓存 300 秒，所以正常情况下新正式版会在数分钟内被腾讯云服务器自动发现并预热。只要 API Control Plane 持续运行、可以访问 GitHub，并且 `BOT_UPDATE_PREFETCH_ENABLED=true`，整个过程无需人工操作。

GitHub 普通 Actions artifact 本身不会直接成为客户端更新源。只有成功的 `master` Windows Release 构建被自动发布成 `bot-v*` 正式 GitHub Release 后，控制面才会把它当成可分发版本。这可以避免把 PR/临时构建误推给在线客户端。

## 服务端缓存策略

默认配置：

```text
BOT_UPDATE_CACHE_DIR=/data/bot-update-cache
BOT_UPDATE_METADATA_CACHE_SECONDS=300
BOT_UPDATE_METADATA_STALE_SECONDS=86400
BOT_UPDATE_GITHUB_TIMEOUT_SECONDS=12
BOT_UPDATE_PACKAGE_TIMEOUT_SECONDS=600
BOT_UPDATE_KEEP_PACKAGE_VERSIONS=3
BOT_UPDATE_PREFETCH_ENABLED=true
BOT_UPDATE_PREFETCH_POLL_SECONDS=60
```

- 最新版本元数据每 5 分钟刷新；
- 服务端 prefetch 每分钟检查一次，并在发现新正式 Release 后主动准备安装包；
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

因此腾讯云镜像异常时会自动回退 GitHub；GitHub 网络异常时，正常情况下客户端已经可以直接使用腾讯云预热好的安装包。

## 部署顺序

```text
1. 更新 API Control Plane 服务端
2. 验证 /api/public/v1/bot-update/latest
3. 合并 master；等待 Windows x64 release build 成功
4. Publish Bot auto-update release 自动发布正式 bot-v* Release
5. 等待腾讯云服务端自动预热该安装包
6. Windows 客户端从“关于与更新”检查新版本
```

新版 Windows 客户端在服务端尚未更新时仍会回退 GitHub `releases/latest`，不会失去更新能力。

## 安全边界

- 公共版本接口不返回任何客户端令牌、店铺数据或服务端密钥；
- 服务端只允许下载已经通过 GitHub latest Release 与 `update.json` 验证的版本；
- 任意路径标签、非 `bot-v*` 标签和无效 SHA-256 会在写盘前拒绝；
- 服务端和 Windows 客户端各自校验一次 SHA-256；
- 原有更新前备份、启动验证与失败自动回滚继续保留。
