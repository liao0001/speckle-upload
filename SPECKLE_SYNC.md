# Speckle Sync × Revit 插件对接说明

本文档供 **Revit 插件（SpeckleUpload）** 开发使用，描述与 **speckle_sync 本机服务** 的 HTTP 约定。

---

## 1. 服务地址

| 项目 | 说明 |
|------|------|
| 默认端口 | 以 `config.yaml` 中 `http.port` 为准（示例：`8508` 或 `6689`） |
| Base URL | `http://127.0.0.1:{port}` |
| 鉴权 | **回调接口无需 Token**，可直接 POST |

查询当前回调地址（可选）：

```http
GET /api/speckle/config/callback
```

成功响应示例见下文「统一响应格式」。

插件侧默认回调（未传 `callbackUrl`、未设置环境变量时）：

`http://127.0.0.1:6689/api/callback`

环境变量覆盖：`SPECKLE_UPLOAD_CALLBACK_URL`

---

## 2. 统一响应格式（lwhale `rr`）

本服务所有 **对外 HTTP JSON 响应**（含回调接口）均使用 lwhale 标准结构，**不再**使用 `{ "success": true/false }` 扁平格式。

### 2.1 成功

HTTP 状态码：`200`

```json
{
  "ret": 0,
  "msg": { }
}
```

- `ret`：固定为 `0` 表示成功  
- `msg`：业务数据，可为 `null`、对象或数组  
- `error`：成功时不出现或为空字符串  

回调成功时 `msg` 示例：

```json
{
  "ret": 0,
  "msg": {
    "request_id": "curl-test-001"
  }
}
```

### 2.2 失败

HTTP 状态码：`500`（参数错误等也为 500，以 `ret` 区分）

```json
{
  "ret": 500,
  "error": "错误描述",
  "msg": null
}
```

常见 `ret`：

| ret | 含义 |
|-----|------|
| 0 | 成功 |
| 500 | 系统/业务错误 |
| 1002 | 参数无效（如缺少 `filePath`） |

插件侧解析（C# 模型见 `Models/LwhaleResponse.cs`）：

```csharp
public sealed class LwhaleResponse
{
    [JsonProperty("ret")]
    public int Ret { get; set; }

    [JsonProperty("error")]
    public string? Error { get; set; }

    [JsonProperty("msg")]
    public object? Msg { get; set; }

    public bool IsSuccess => Ret == 0;
}
```

---

## 3. 进度与结果回调（插件 → speckle_sync）

打开文档后、解析/上传过程中、以及任务结束时，插件均 **POST 同一地址** `/api/callback`（或请求体 `callbackUrl`）。

过程中为 **异步** 上报（失败不中断上传）；最终结果为 **同步** 等待响应。

### 3.1 进度字段（snake_case）

| 字段 | 类型 | 说明 |
|------|------|------|
| `progress` | string | 阶段文本：`打开` / `解析` / `上传` / `完成` |
| `progress_index` | int | 解析阶段 = 转换循环 `index`（1、500、1000…）；上传开始 = `1`；完成 = `object_count` |

过程中回调示例（解析中）：

```json
{
  "request_id": "curl-test-001",
  "file_path": "D:\\testrvt\\test2.rvt",
  "stream_id": "1183495a7b",
  "success": false,
  "progress": "解析",
  "progress_index": 1000
}
```

上报频率（`progress=解析`）：与日志一致，在 `1`、每 `500` 个、最后一个图元时上报。

### 3.2 最终结果回调

插件在 **Speckle 上传流程结束**（成功或失败）后再次 POST `/api/callback`，包含完整业务字段 + `progress=完成`。

### 3.3 请求

```http
POST /api/callback
Content-Type: application/json; charset=utf-8
```

路径可在服务端 `speckle.sync` → `callback.path` 配置，默认 `/api/callback`。

也可在 `POST /upload` 请求体中指定 `callbackUrl`（优先于环境变量与插件默认值）。

**无需** `Authorization` 头。

### 3.4 请求体（全部 snake_case）

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `request_id` | string | 是 | 与下发任务时一致的唯一 ID |
| `success` | bool | 是 | 整体是否成功（send + commit 均成功为 `true`） |
| `file_path` | string | 建议 | 本地 RVT 路径 |
| `stream_id` | string | 建议 | Speckle streamId |
| `object_id` | string | 否 | 上传成功后的 objectId；失败可为 `null` |
| `commit_id` | string | 否 | commit 成功后的 id；失败可为 `null` |
| `object_count` | int | 否 | 对象数量，失败可为 `0` |
| `branch_name` | string | 否 | 分支名 |
| `commit_message` | string | 否 | 提交说明（UTF-8） |
| `error` | string | 否 | 失败时的错误信息；成功可为 `null` 或 `""` |
| `progress` | string | 否 | `打开` / `解析` / `上传` / `完成` |
| `progress_index` | int | 否 | 见 3.1 |

### 3.5 请求示例

**成功：**

```json
{
  "request_id": "curl-test-001",
  "success": true,
  "file_path": "D:\\testrvt\\test2.rvt",
  "stream_id": "1183495a7b",
  "branch_name": "main",
  "commit_message": "测试中文提交说明",
  "object_id": "9ee4510e89ddfacedecfff1ff0869f84",
  "commit_id": "a1b2c3d4e5f6789012345678abcdef01",
  "object_count": 939,
  "progress": "完成",
  "progress_index": 939,
  "error": null
}
```

**失败：**

```json
{
  "request_id": "curl-test-002",
  "success": false,
  "file_path": "D:\\testrvt\\missing.rvt",
  "stream_id": "1183495a7b",
  "object_id": null,
  "commit_id": null,
  "object_count": 0,
  "progress": "完成",
  "progress_index": 0,
  "error": "File not found: D:\\testrvt\\missing.rvt"
}
```

### 3.6 服务端行为

1. 写入 lwhale 表单 **`sync_logs`**（无论成功失败）  
2. 若 `success == false`，向企微机器人发送告警（Webhook 在服务端配置）  

### 3.7 响应示例

**成功：**

```json
{
  "ret": 0,
  "msg": {
    "request_id": "curl-test-001"
  }
}
```

**参数错误（缺少 request_id）：**

```json
{
  "ret": 1002,
  "error": "request_id 不能为空",
  "msg": null
}
```

### 3.8 curl 自测（Mac / Linux）

将端口改为实际 `http.port`：

```bash
curl -sS -X POST "http://127.0.0.1:6689/api/callback" \
  -H "Content-Type: application/json; charset=utf-8" \
  -d '{
    "request_id": "curl-test-001",
    "success": true,
    "file_path": "D:\\testrvt\\test2.rvt",
    "stream_id": "1183495a7b",
    "branch_name": "main",
    "commit_message": "测试中文提交说明",
    "object_id": "9ee4510e89ddfacedecfff1ff0869f84",
    "commit_id": "a1b2c3d4e5f6789012345678abcdef01",
    "object_count": 939,
    "progress": "完成",
    "progress_index": 939,
    "error": null
  }'
```

---

## 4. 接收上传任务（speckle_sync → 插件）

由 **speckle_sync** 主动调用插件 `HttpUploadServer`：

| Revit 版本 | 默认端口 |
|------------|----------|
| 2022 | `6687` |
| 2024 | `6688` |

可通过环境变量 `SPECKLE_UPLOAD_HTTP_PORT` 覆盖。

### 4.1 请求

```http
POST http://127.0.0.1:6688/upload
Content-Type: application/json; charset=utf-8
```

请求体字段（sync 发出为 camelCase）：

| 字段 | 说明 |
|------|------|
| `filePath` | 已下载到本地的文件路径 |
| `streamId` | streamId |
| `serverUrl` | Speckle 服务地址 |
| `token` | 用户 token |
| `branchName` | 分支 |
| `commitMessage` | 提交说明 |
| `requestId` | 请求 ID |
| `callbackUrl` | 进度与完成后 POST 的地址，如 `http://127.0.0.1:6689/api/callback` |

### 4.2 插件立即响应

使用 **lwhale `rr` 格式**（`ret: 0` 表示已接受任务，HTTP `200`）：

```json
{
  "ret": 0,
  "msg": null
}
```

接受失败（参数错误 `ret: 1002`，其它 `ret: 500`）：

```json
{
  "ret": 500,
  "error": "原因说明",
  "msg": null
}
```

插件在 **异步处理完成后** 再调用第 3 节回调，不要把最终结果放在 `/upload` 的同步响应里。

### 4.3 健康检查

```http
GET http://127.0.0.1:6688/health
```

响应（非 lwhale 格式，仅供探活）：

```json
{ "status": "ok", "port": 6688 }
```

---

## 5. 插件实现清单

- [x] 回调 URL 默认 `http://127.0.0.1:6689/api/callback`，支持 `callbackUrl` 与 `SPECKLE_UPLOAD_CALLBACK_URL`  
- [x] 回调请求体字段全部 **snake_case**，含 `progress` / `progress_index`  
- [x] 解析本服务响应时判断 **`ret == 0`**  
- [x] 失败时读取 `error` 字段  
- [x] `/upload` 同步响应改为 `ret` / `msg` / `error`  
- [x] 打开/解析/上传过程中异步 POST 回调；最终同步 POST；回调失败写入插件日志  

---

## 6. 配置参考（speckle_sync 侧）

在 lwhale **系统设置** → `speckle.sync`：

```yaml
callback:
  path: "/api/callback"

wecom:
  enabled: true
  webhook_url: "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=YOUR_KEY"
```

---

## 7. 变更记录

| 日期 | 说明 |
|------|------|
| 2026-05-19 | 回调体 snake_case；响应 lwhale `rr`；支持 `callbackUrl`；默认 `/api/callback` |
| 2026-08-03 | 默认端口 2022=6687、2024=6688；进度合并至 `/api/callback`（`progress`/`progress_index`）；弹窗默认关闭 |
