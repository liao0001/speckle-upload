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

过程中为 **异步** 上报（失败不中断上传）；**最终结果为同步等待响应，且一定在关闭 RVT 之前发出**。

插件执行顺序：

```text
打开 → 解析 → 上传(Operations.Send) → CommitCreate
  → POST /api/callback（is_final=true，同步）  ← speckle_sync 在此判定任务完成
  → 下一次 Idling 异步关闭 RVT（不关也不影响已完成回调）
```

### 3.1 如何判断「任务完成」（speckle_sync 必读）

**仅当同时满足以下条件时，才视为任务结束，可推送远端成功/失败：**

| 条件 | 进度回调 | 最终回调 |
|------|----------|----------|
| `is_final` | `false` | **`true`** |
| `progress` | `打开` / `解析` / `上传` | **`完成`** |
| `success` | 不传或 `null` | **`true` / `false`** |

判定逻辑（推荐）：

```text
if body.is_final == true && body.progress == "完成":
    # 任务完成 → 更新任务状态、推送远端（success/error）
    if body.success == true:
        推送成功（带 object_id、commit_id、object_count）
    else:
        推送失败（error 必填，转发为 errorMessage）
else:
    # 进行中 → 只更新 progress / progress_index，不推送最终结果
```

兼容旧插件：`is_final` 缺失时，可用 `progress == "完成"` 且 `success` 字段存在 判断为最终回调。

### 3.2 进度字段（snake_case）

| 字段 | 类型 | 说明 |
|------|------|------|
| `is_final` | bool | **进度=false，最终=true** |
| `progress` | string | 阶段：`接收`/`入队`/`执行`/`打开`/`准备`/`解析`/`上传`/`提交`/`完成` |
| `progress_index` | int | **整体进度百分比 0–100**，插件已算好，speckle_sync 可直接用于进度条 |

**`progress_index` 里程碑（固定值）**

| 时机 | `progress` | `progress_index` |
|------|------------|------------------|
| 收到 POST /upload | `接收` | **1** |
| /upload 返回 ret=0 | `入队` | **5** |
| Revit Execute 开始 | `执行` | **6** |
| RVT 打开完成 | `打开` | **9** |
| 开始 Speckle 转换 | `准备` | **10** |
| Operations.Send 完成 | `提交` | **91** |
| 最终回调 is_final=true | `完成` | **100** |

**按比例计算（40% 额度）**

- **解析**（`progress=解析`）：`10 + floor(当前图元序号 / 图元总数 × 40)`，上限 **50**  
  例：500/10000 → `10 + 2 = 12`
- **上传**（`progress=上传`）：`50 + floor(已上传对象数 / 估算总数 × 40)`，上限 **90**  
  估算总数 = `max(已转换图元数 × 10, 当前已上传数)`（Speckle 序列化对象数通常远大于图元数）

过程中回调示例（解析中）：

```json
{
  "request_id": "curl-test-001",
  "file_path": "D:\\testrvt\\test2.rvt",
  "stream_id": "1183495a7b",
  "is_final": false,
  "progress": "解析",
  "progress_index": 12
}
```

上报频率（`progress=解析`/`上传`）：

- 在 `1`、每 `500` 个、阶段结束时上报
- **时间心跳**：默认每 **30 秒**强制上报一次（`SPECKLE_UPLOAD_PROGRESS_HEARTBEAT_SECONDS` 可改），慢模型也不会长时间无更新
- 进度百分比单调递增，不会回退

### 3.3 最终结果回调

插件在 **Speckle Send + CommitCreate 成功后**（或整流程异常后）**同步** POST `/api/callback`，字段含 `is_final=true`、`progress=完成`。  
**此请求返回后插件才在后台 Idling 关闭 RVT**，关闭失败不影响已完成状态。

### 3.4 请求

```http
POST /api/callback
Content-Type: application/json; charset=utf-8
```

路径可在服务端 `speckle.sync` → `callback.path` 配置，默认 `/api/callback`。

也可在 `POST /upload` 请求体中指定 `callbackUrl`（优先于环境变量与插件默认值）。

**无需** `Authorization` 头。

### 3.5 请求体（全部 snake_case）

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `request_id` | string | 是 | 与下发任务时一致的唯一 ID |
| `is_final` | bool | 是 | **false=进度，true=任务完成** |
| `success` | bool | 最终必填 | 仅 `is_final=true` 时有效 |
| `file_path` | string | 建议 | 本地 RVT 路径 |
| `stream_id` | string | 建议 | Speckle streamId |
| `object_id` | string | 否 | 上传成功后的 objectId；失败可为 `null` |
| `commit_id` | string | 否 | commit 成功后的 id；失败可为 `null` |
| `object_count` | int | 否 | 对象数量，失败可为 `0` |
| `branch_name` | string | 否 | 分支名 |
| `commit_message` | string | 否 | 提交说明（UTF-8） |
| `error` | string | 否 | 失败时的错误信息；成功可为 `null` 或 `""` |
| `progress` | string | 否 | 见 3.2 |
| `progress_index` | int | 否 | **0–100 百分比** |

### 3.6 请求示例

**成功（最终，推送远端用这一条）：**

```json
{
  "request_id": "curl-test-001",
  "is_final": true,
  "success": true,
  "file_path": "D:\\testrvt\\test2.rvt",
  "stream_id": "1183495a7b",
  "branch_name": "main",
  "commit_message": "测试中文提交说明",
  "object_id": "9ee4510e89ddfacedecfff1ff0869f84",
  "commit_id": "a1b2c3d4e5f6789012345678abcdef01",
  "object_count": 939,
  "progress": "完成",
  "progress_index": 100,
  "error": null
}
```

**失败：**

```json
{
  "request_id": "curl-test-002",
  "is_final": true,
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

### 3.7 服务端行为

1. **`is_final=false`**：只更新 `sync_logs` 的 `progress` / `progress_index`，**不要**改任务为失败，**不要**调 Speckle rvt result  
2. **`is_final=true`**：写入完整 `sync_logs`，按 `success` 更新任务并**推送远端**；失败时 `error` 非空  

### 3.8 响应示例

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
    "progress_index": 100,
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
