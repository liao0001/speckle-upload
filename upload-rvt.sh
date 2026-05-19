#!/usr/bin/env bash
# 按 API.md「用户侧接口」：创建上传任务 → PUT MinIO → 上传完成确认
set -euo pipefail

# ========== 基础变量（请按环境修改） ==========
host="http://47.100.77.97:64482"
token="0075fac4e07bf158cc7d4ca335e7b8df3f1fb8f600"
rvtPath="/Users/liaoyong/linshi/test.rvt"

# 也可通过环境变量覆盖：HOST / TOKEN / RVT_PATH
host="${HOST:-$host}"
token="${TOKEN:-$token}"
rvtPath="${RVT_PATH:-$rvtPath}"

# ========== 依赖检查 ==========
for cmd in curl jq; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "错误: 未找到命令 $cmd（请安装后重试）" >&2
    exit 1
  fi
done

host="${host%/}"
if [[ -z "$token" || "$token" == "your-bearer-token" ]]; then
  echo "错误: 请设置有效的 token" >&2
  exit 1
fi
if [[ ! -f "$rvtPath" ]]; then
  echo "错误: RVT 文件不存在: $rvtPath" >&2
  exit 1
fi

fileName="$(basename "$rvtPath")"
if [[ "$(uname -s)" == "Darwin" ]]; then
  fileSize="$(stat -f%z "$rvtPath")"
else
  fileSize="$(stat -c%s "$rvtPath")"
fi

echo "host:     $host"
echo "rvtPath:  $rvtPath"
echo "fileName: $fileName"
echo "fileSize: $fileSize"
echo ""

# ========== 2. 创建上传任务 ==========
echo ">>> [2] POST /api/v1/file-conversions（创建上传任务）"
createBody="$(jq -n --arg fn "$fileName" --argjson fs "$fileSize" '{fileName: $fn, fileSize: $fs}')"
createResp="$(curl -sS -w "\n%{http_code}" -X POST "${host}/api/v1/file-conversions" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer ${token}" \
  -d "$createBody")"

createHttpCode="$(echo "$createResp" | tail -n1)"
createJson="$(echo "$createResp" | sed '$d')"
echo "$createJson" | jq .
if [[ "$createHttpCode" -lt 200 || "$createHttpCode" -ge 300 ]]; then
  echo "错误: 创建上传任务失败 HTTP $createHttpCode" >&2
  exit 1
fi

recordId="$(echo "$createJson" | jq -r '.id')"
uploadUrl="$(echo "$createJson" | jq -r '.uploadUrl')"
if [[ -z "$recordId" || "$recordId" == "null" || -z "$uploadUrl" || "$uploadUrl" == "null" ]]; then
  echo "错误: 响应中缺少 id 或 uploadUrl" >&2
  exit 1
fi
echo "recordId: $recordId"
echo ""

# ========== 3. PUT 上传至 MinIO ==========
echo ">>> [3] PUT uploadUrl（上传 RVT 至 MinIO）"
headersFile="$(mktemp)"
trap 'rm -f "$headersFile"' EXIT

putHttpCode="$(curl -sS -o /dev/null -w "%{http_code}" -D "$headersFile" -X PUT \
  --upload-file "$rvtPath" \
  "$uploadUrl")"

echo "PUT HTTP 状态码: $putHttpCode"
if [[ "$putHttpCode" -lt 200 || "$putHttpCode" -ge 300 ]]; then
  echo "错误: MinIO PUT 失败 HTTP $putHttpCode" >&2
  exit 1
fi

# MinIO 返回的 ETag（上传完成确认需要，保持与响应头一致，含引号）
etag="$(grep -i '^[eE]tag:' "$headersFile" | tail -n1 | cut -d' ' -f2- | tr -d '\r\n')"
if [[ -z "$etag" ]]; then
  echo "警告: 未从 PUT 响应头读取到 ETag，将使用空字符串尝试确认" >&2
  etag=""
fi
echo "ETag: $etag"
echo ""

# ========== 4. 源文件上传完成确认 ==========
echo ">>> [4] POST /api/v1/file-conversions/${recordId}/upload-complete"
completeBody="$(jq -n --arg etag "$etag" '{etag: $etag}')"
completeResp="$(curl -sS -w "\n%{http_code}" -X POST \
  "${host}/api/v1/file-conversions/${recordId}/upload-complete" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer ${token}" \
  -d "$completeBody")"

completeHttpCode="$(echo "$completeResp" | tail -n1)"
completeJson="$(echo "$completeResp" | sed '$d')"
echo "$completeJson" | jq .
if [[ "$completeHttpCode" -lt 200 || "$completeHttpCode" -ge 300 ]]; then
  echo "错误: 上传完成确认失败 HTTP $completeHttpCode" >&2
  exit 1
fi
echo ""

# ========== 5. 完成 ==========
echo ">>> [5] 上传完成"
echo "文件记录 ID: $recordId"
streamId="$(echo "$completeJson" | jq -r '.streamId // empty')"
if [[ -n "$streamId" ]]; then
  echo "streamId:    $streamId"
fi
