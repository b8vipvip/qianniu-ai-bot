#!/usr/bin/env bash
set -Eeuo pipefail

REPO_URL="${REPO_URL:-git@github.com:b8vipvip/qianniu-ai-bot.git}"
REPO_DIR="${REPO_DIR:-/opt/qianniu-ai-bot}"
LEGACY_DIR="${LEGACY_DIR:-/opt/qianniu-api-control-plane}"
BRANCH="${BRANCH:-master}"
BACKUP_ROOT="${BACKUP_ROOT:-/opt/qianniu-ai-bot-backups}"
CONTAINER_NAME="${CONTAINER_NAME:-qianniu-api-control-plane}"
VERIFY_URL="${VERIFY_URL:-}"

SERVICE_REL="services/api-control-plane"
SERVICE_DIR="$REPO_DIR/$SERVICE_REL"
COMPOSE_FILE="$SERVICE_DIR/docker-compose.bt.yml"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP_DIR="$BACKUP_ROOT/$TIMESTAMP"
OLD_COMMIT=""
OLD_DEPLOY_KIND="none"
OLD_SOURCE_DIR=""
BACKUP_READY=0

log() {
  printf '\n[%s] %s\n' "$(date '+%F %T')" "$*"
}

warn() {
  printf '\n[WARN] %s\n' "$*" >&2
}

die() {
  printf '\n[ERROR] %s\n' "$*" >&2
  exit 1
}

need() {
  command -v "$1" >/dev/null 2>&1 || die "缺少命令: $1"
}

read_env_value() {
  local key="$1"
  local file="$2"
  [[ -f "$file" ]] || return 0
  awk -F= -v key="$key" '
    $0 !~ /^[[:space:]]*#/ && $1 == key {
      value=substr($0, index($0, "=")+1)
      gsub(/^[[:space:]\047\"]+|[[:space:]\047\"]+$/, "", value)
      found=value
    }
    END { if (found != "") print found }
  ' "$file"
}

compose() {
  docker compose -f "$COMPOSE_FILE" "$@"
}

container_exists() {
  docker ps -a --format '{{.Names}}' | grep -Fxq "$CONTAINER_NAME"
}

container_running() {
  docker ps --format '{{.Names}}' | grep -Fxq "$CONTAINER_NAME"
}

restore_backup_to() {
  local target="$1"
  [[ "$BACKUP_READY" -eq 1 ]] || return 0
  mkdir -p "$target"
  if [[ -f "$BACKUP_DIR/.env" ]]; then
    cp -a "$BACKUP_DIR/.env" "$target/.env"
  fi
  if [[ -f "$BACKUP_DIR/data.tar.gz" ]]; then
    rm -rf "$target/data"
    mkdir -p "$target/data"
    tar -xzf "$BACKUP_DIR/data.tar.gz" -C "$target/data"
  fi
}

rollback() {
  warn "新版本启动或健康检查失败，开始自动回滚。"
  set +e
  docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true

  if [[ "$OLD_DEPLOY_KIND" == "repo" && -n "$OLD_COMMIT" ]]; then
    git -C "$REPO_DIR" reset --hard "$OLD_COMMIT"
    restore_backup_to "$REPO_DIR/$SERVICE_REL"
    if [[ -f "$REPO_DIR/$SERVICE_REL/docker-compose.bt.yml" ]]; then
      (
        cd "$REPO_DIR/$SERVICE_REL" || exit 1
        docker compose -f docker-compose.bt.yml up -d --build --force-recreate
      )
    fi
  elif [[ "$OLD_DEPLOY_KIND" == "legacy" && -n "$OLD_SOURCE_DIR" ]]; then
    restore_backup_to "$OLD_SOURCE_DIR"
    if [[ -f "$OLD_SOURCE_DIR/docker-compose.bt.yml" ]]; then
      (
        cd "$OLD_SOURCE_DIR" || exit 1
        docker compose -f docker-compose.bt.yml up -d --build --force-recreate
      )
    elif [[ -f "$OLD_SOURCE_DIR/docker-compose.yml" ]]; then
      (
        cd "$OLD_SOURCE_DIR" || exit 1
        docker compose up -d --build --force-recreate
      )
    fi
  fi

  warn "已执行回滚尝试。备份目录: $BACKUP_DIR"
  exit 1
}

need git
need docker
need curl
need tar

docker compose version >/dev/null 2>&1 || die "未检测到 Docker Compose v2（docker compose）"

if [[ ! -d "$REPO_DIR/.git" ]]; then
  log "未发现 Git 仓库，使用服务器现有 GitHub SSH 配置克隆到 $REPO_DIR"
  mkdir -p "$(dirname "$REPO_DIR")"
  git clone --branch "$BRANCH" "$REPO_URL" "$REPO_DIR"
fi

[[ -d "$REPO_DIR/.git" ]] || die "$REPO_DIR 不是有效 Git 仓库"

TRACKED_DIRTY="$(git -C "$REPO_DIR" status --porcelain --untracked-files=no)"
if [[ -n "$TRACKED_DIRTY" ]]; then
  printf '%s\n' "$TRACKED_DIRTY" >&2
  die "仓库存在未提交的已跟踪文件修改。为避免覆盖服务器手工改动，已停止更新。"
fi

OLD_COMMIT="$(git -C "$REPO_DIR" rev-parse HEAD)"
if [[ -f "$SERVICE_DIR/.env" || -d "$SERVICE_DIR/data" ]]; then
  OLD_DEPLOY_KIND="repo"
  OLD_SOURCE_DIR="$SERVICE_DIR"
elif [[ -f "$LEGACY_DIR/.env" || -d "$LEGACY_DIR/data" ]]; then
  OLD_DEPLOY_KIND="legacy"
  OLD_SOURCE_DIR="$LEGACY_DIR"
fi

log "拉取 GitHub 最新 $BRANCH 分支"
git -C "$REPO_DIR" fetch --prune origin "$BRANCH"
if git -C "$REPO_DIR" show-ref --verify --quiet "refs/heads/$BRANCH"; then
  git -C "$REPO_DIR" checkout "$BRANCH"
else
  git -C "$REPO_DIR" checkout -b "$BRANCH" "origin/$BRANCH"
fi
git -C "$REPO_DIR" merge --ff-only "origin/$BRANCH"
NEW_COMMIT="$(git -C "$REPO_DIR" rev-parse HEAD)"
log "代码版本: $OLD_COMMIT -> $NEW_COMMIT"

[[ -f "$SERVICE_DIR/Dockerfile" ]] || die "缺少 $SERVICE_DIR/Dockerfile"
[[ -f "$SERVICE_DIR/runtime_streaming_guard.py" ]] || die "缺少 runtime_streaming_guard.py，拒绝部署不完整版本"
[[ -f "$COMPOSE_FILE" ]] || die "缺少宝塔 Compose 文件: $COMPOSE_FILE"

# 老的 ZIP 部署通常位于 /opt/qianniu-api-control-plane。第一次切换到 Git 仓库部署时复用原配置。
if [[ ! -f "$SERVICE_DIR/.env" && -f "$LEGACY_DIR/.env" ]]; then
  log "检测到旧部署配置，复制 .env 到 Git 仓库服务目录"
  cp -a "$LEGACY_DIR/.env" "$SERVICE_DIR/.env"
fi

[[ -f "$SERVICE_DIR/.env" ]] || die "未找到 $SERVICE_DIR/.env。请先把现有 .env 放到该目录后重试。"

if grep -Eq 'replace-with-|change-me-in-production' "$SERVICE_DIR/.env"; then
  die ".env 仍包含示例占位密钥/密码，拒绝启动生产服务。"
fi

log "校验宝塔 Compose 配置"
(
  cd "$SERVICE_DIR"
  docker compose -f docker-compose.bt.yml config >/dev/null
)

log "先构建新镜像；此阶段旧服务继续运行"
if ! (
  cd "$SERVICE_DIR"
  docker compose -f docker-compose.bt.yml build --pull
); then
  die "新镜像构建失败，旧服务未被停止。"
fi

log "停止旧控制面并创建冷备份"
if container_running; then
  docker stop "$CONTAINER_NAME" >/dev/null
fi

mkdir -p "$BACKUP_DIR"
ENV_SOURCE="$SERVICE_DIR/.env"
DATA_SOURCE="$SERVICE_DIR/data"
if [[ "$OLD_DEPLOY_KIND" == "legacy" ]]; then
  [[ -f "$LEGACY_DIR/.env" ]] && ENV_SOURCE="$LEGACY_DIR/.env"
  [[ -d "$LEGACY_DIR/data" ]] && DATA_SOURCE="$LEGACY_DIR/data"
fi

if [[ -f "$ENV_SOURCE" ]]; then
  cp -a "$ENV_SOURCE" "$BACKUP_DIR/.env"
fi
if [[ -d "$DATA_SOURCE" ]]; then
  tar -czf "$BACKUP_DIR/data.tar.gz" -C "$DATA_SOURCE" .
fi
printf '%s\n' "$OLD_COMMIT" > "$BACKUP_DIR/old-git-commit.txt"
printf '%s\n' "$NEW_COMMIT" > "$BACKUP_DIR/new-git-commit.txt"
BACKUP_READY=1
log "备份完成: $BACKUP_DIR"

# 首次从旧 ZIP 部署迁移到 Git 仓库部署时，冷复制原 data。
if [[ "$OLD_DEPLOY_KIND" == "legacy" ]]; then
  log "迁移旧部署的 .env 和 data 到 $SERVICE_DIR"
  cp -a "$LEGACY_DIR/.env" "$SERVICE_DIR/.env"
  rm -rf "$SERVICE_DIR/data"
  mkdir -p "$SERVICE_DIR/data"
  if [[ -d "$LEGACY_DIR/data" ]]; then
    cp -a "$LEGACY_DIR/data/." "$SERVICE_DIR/data/"
  fi
fi
mkdir -p "$SERVICE_DIR/data"

if container_exists; then
  docker rm -f "$CONTAINER_NAME" >/dev/null
fi

log "启动最新控制面"
if ! (
  cd "$SERVICE_DIR"
  docker compose -f docker-compose.bt.yml up -d --build --force-recreate
); then
  rollback
fi

BIND_PORT="$(read_env_value CONTROL_PLANE_BIND_PORT "$SERVICE_DIR/.env")"
BIND_PORT="${BIND_PORT:-18081}"
LOCAL_HEALTH="http://127.0.0.1:${BIND_PORT}/healthz"

log "等待本机健康检查: $LOCAL_HEALTH"
HEALTH_OK=0
for _ in $(seq 1 60); do
  if curl -fsS --max-time 5 "$LOCAL_HEALTH" >/tmp/qianniu-control-plane-health.json 2>/dev/null; then
    HEALTH_OK=1
    break
  fi
  sleep 2
done

if [[ "$HEALTH_OK" -ne 1 ]]; then
  docker logs --tail 200 "$CONTAINER_NAME" || true
  rollback
fi
cat /tmp/qianniu-control-plane-health.json

CMD_JSON="$(docker inspect -f '{{json .Config.Cmd}}' "$CONTAINER_NAME" 2>/dev/null || true)"
if [[ "$CMD_JSON" != *"bootstrap.py"* ]]; then
  warn "容器启动命令不是 bootstrap.py: $CMD_JSON"
  rollback
fi

if [[ -z "$VERIFY_URL" ]]; then
  PUBLIC_BASE_URL="$(read_env_value PUBLIC_BASE_URL "$SERVICE_DIR/.env")"
  CONTROL_PLANE_DOMAIN="$(read_env_value CONTROL_PLANE_DOMAIN "$SERVICE_DIR/.env")"
  if [[ -n "$PUBLIC_BASE_URL" ]]; then
    VERIFY_URL="${PUBLIC_BASE_URL%/}/healthz"
  elif [[ -n "$CONTROL_PLANE_DOMAIN" ]]; then
    VERIFY_URL="https://${CONTROL_PLANE_DOMAIN}/healthz"
  else
    VERIFY_URL="https://aboter.mv3.cn/healthz"
  fi
fi

log "验证宝塔现有反代和 SSL: $VERIFY_URL"
if ! curl -fsS --max-time 15 "$VERIFY_URL" >/tmp/qianniu-control-plane-public-health.json; then
  warn "本机服务已健康，但公网域名验证失败。未回滚服务，因为这通常属于宝塔反代、DNS 或 SSL 层问题。"
  warn "请检查宝塔反向代理目标是否仍为 http://127.0.0.1:${BIND_PORT}"
  compose ps
  exit 2
fi
cat /tmp/qianniu-control-plane-public-health.json

log "更新成功"
printf 'Git commit: %s\n' "$NEW_COMMIT"
printf 'Service dir: %s\n' "$SERVICE_DIR"
printf 'Backup: %s\n' "$BACKUP_DIR"
printf 'Local health: %s\n' "$LOCAL_HEALTH"
printf 'Public health: %s\n' "$VERIFY_URL"
compose ps
