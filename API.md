# Convert Files API

## 概览

本文档描述文件转换功能当前已落地的 REST 接口、鉴权方式、请求示例、响应示例和推荐调用顺序。

- 前端页面路由：`/convert_files`
- 后端接口前缀：`/api/v1/file-conversions`
- 用户侧接口：依赖当前登录态
- 第三方接口：依赖固定请求头 `X-File-Conversion-Token`

## 术语说明

- `uploadUrl`
- 含义：MinIO 预签名上传地址，用于 `PUT` 上传文件内容
- `sourceObjectKey`
- 含义：源文件在对象存储中的路径
- `resultObjectKey`
- 含义：结果文件在对象存储中的路径
- `sourceFileUrl`
- 含义：源文件签名下载地址，由后端动态生成
- `resultFileUrl`
- 含义：结果文件签名下载地址，由后端动态生成
- `streamId`
- 含义：文件转换链路唯一标识
- `eventId`
- 含义：一次转换事件的唯一标识

## 鉴权方式

### 用户侧接口

以下接口给前端页面使用，要求用户已登录：

- `POST /api/v1/file-conversions`
- `POST /api/v1/file-conversions/:id/upload-complete`
- `GET /api/v1/file-conversions`

前端调用方式：

- 统一使用 `plugins/015-fetchAuth.ts` 提供的 `$fetch`
- 不需要手工传服务 token

### 第三方接口

以下接口给第三方转换服务使用：

- `GET /api/v1/file-conversions/pending`
- `GET /api/v1/file-conversions/:id/params`
- `POST /api/v1/file-conversions/:id/start`
- `POST /api/v1/file-conversions/:id/processing`
- `POST /api/v1/file-conversions/:id/result-upload-url`
- `POST /api/v1/file-conversions/:id/callback`

请求头格式：

```http
X-File-Conversion-Token: file-conversion-service-token
```

服务端环境变量：

```bash
FILE_CONVERSION_SERVICE_TOKEN=file-conversion-service-token
```

鉴权失败规则：

- 缺少 `X-File-Conversion-Token` 时返回 `401`
- `token` 不匹配时返回 `403`

## 状态说明

- `uploaded`
- 文件记录已创建，前端已拿到源文件上传地址
- `pending`
- 源文件已上传并确认完成，等待第三方领取
- `queued`
- 第三方已开始领取任务，服务端已创建事件
- `processing`
- 第三方已开始实际转换
- `success`
- 转换成功并已写回结果
- `failed`
- 转换失败

## 用户侧接口

### 1. 创建上传任务

- 方法：`POST`
- 路径：`/api/v1/file-conversions`
- 调用方：前端用户

请求体：

```json
{
  "fileName": "robots.txt",
  "fileSize": 26
}
```

成功响应：

```json
{
  "id": "cfbaadd933",
  "fileName": "robots.txt",
  "fileSize": 26,
  "sourceObjectKey": "file-conversion/source/cfbaadd933/robots.txt",
  "sourceFileUrl": "http://127.0.0.1:9000/...&x-id=GetObject",
  "resultObjectKey": null,
  "resultFileUrl": null,
  "streamId": "conv_2eb2f9e4b6201865396b",
  "status": "uploaded",
  "isConverted": false,
  "uploadedAt": null,
  "startedAt": null,
  "convertedAt": null,
  "errorMessage": null,
  "creator": "ee47ce6daa",
  "updater": "ee47ce6daa",
  "createdAt": "2026-05-18T12:57:52.578Z",
  "updatedAt": "2026-05-18T12:57:52.578Z",
  "uploadUrl": "http://127.0.0.1:9000/...&x-id=PutObject"
}
```

使用方式：

1. 前端先调本接口，拿到 `id` 和 `uploadUrl`
2. 前端使用 `uploadUrl` 直接把原文件 `PUT` 到 MinIO
3. 上传成功后，继续调用“上传完成确认”

`curl` 示例：

```bash
curl -X POST http://localhost:3000/api/v1/file-conversions \
  -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer <user-token>' \
  -d '{
    "fileName": "robots.txt",
    "fileSize": 26
  }'
```

### 2. 源文件上传完成确认

- 方法：`POST`
- 路径：`/api/v1/file-conversions/:id/upload-complete`
- 调用方：前端用户

路径参数：

- `id`
- 创建上传任务返回的文件记录 ID

请求体：

```json
{
  "etag": "\"f02e326f800ee26f04df7961adbf7c0a\""
}
```

成功响应：

```json
{
  "id": "cfbaadd933",
  "fileName": "robots.txt",
  "fileSize": 26,
  "sourceObjectKey": "file-conversion/source/cfbaadd933/robots.txt",
  "sourceFileUrl": "http://127.0.0.1:9000/...&x-id=GetObject",
  "status": "pending",
  "isConverted": false,
  "uploadedAt": "2026-05-18T12:57:52.658Z"
}
```

使用方式：

1. 前端完成 `PUT uploadUrl`
2. 从 MinIO 上传响应头里取出 `etag`
3. 调本接口确认上传完成
4. 成功后该记录进入 `pending`

`curl` 示例：

```bash
curl -X POST http://localhost:3000/api/v1/file-conversions/cfbaadd933/upload-complete \
  -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer <user-token>' \
  -d '{
    "etag": "\"f02e326f800ee26f04df7961adbf7c0a\""
  }'
```

### 3. 查询文件列表

- 方法：`GET`
- 路径：`/api/v1/file-conversions`
- 调用方：前端用户

查询参数：

- `keyword`
- 文件名关键字，可选
- `status`
- 状态筛选，可选
- `page`
- 页码，默认 `1`
- `pageSize`
- 每页条数，默认 `20`

成功响应：

```json
{
  "items": [
    {
      "id": "cfbaadd933",
      "fileName": "robots.txt",
      "fileSize": 26,
      "sourceObjectKey": "file-conversion/source/cfbaadd933/robots.txt",
      "sourceFileUrl": "http://127.0.0.1:9000/...&x-id=GetObject",
      "resultObjectKey": "file-conversion/result/cfbaadd933/robots-converted.txt",
      "resultFileUrl": "http://127.0.0.1:9000/...&x-id=GetObject",
      "streamId": "conv_2eb2f9e4b6201865396b",
      "status": "success",
      "isConverted": true,
      "uploadedAt": "2026-05-18T12:57:52.658Z",
      "startedAt": "2026-05-18T12:58:54.418Z",
      "convertedAt": "2026-05-18T13:00:01.708Z",
      "errorMessage": null,
      "creator": "ee47ce6daa",
      "creatorName": "超级管理员"
    }
  ],
  "total": 1,
  "page": 1,
  "pageSize": 50
}
```

使用方式：

1. 页面初始化时加载
2. 上传成功后刷新
3. 用户点“刷新列表”时再次查询

`curl` 示例：

```bash
curl "http://localhost:3000/api/v1/file-conversions?page=1&pageSize=50&status=success" \
  -H 'Authorization: Bearer <user-token>'
```

## 第三方接口

### 4. 查询待转换列表

- 方法：`GET`
- 路径：`/api/v1/file-conversions/pending`
- 调用方：第三方服务

成功响应：

```json
{
  "items": [
    {
      "id": "cfbaadd933",
      "fileName": "robots.txt",
      "fileSize": 26,
      "sourceObjectKey": "file-conversion/source/cfbaadd933/robots.txt",
      "sourceFileUrl": "http://127.0.0.1:9000/...&x-id=GetObject",
      "resultObjectKey": null,
      "resultFileUrl": null,
      "streamId": "conv_2eb2f9e4b6201865396b",
      "status": "pending",
      "isConverted": false
    }
  ]
}
```

使用方式：

1. 第三方轮询本接口
2. 找到待转换任务后，取 `id`
3. 再调 `params` 或直接 `start`

`curl` 示例：

```bash
curl http://localhost:3000/api/v1/file-conversions/pending \
  -H 'X-File-Conversion-Token: file-conversion-service-token'
```

### 5. 按 ID 获取转换参数

- 方法：`GET`
- 路径：`/api/v1/file-conversions/:id/params`
- 调用方：第三方服务

成功响应：

```json
{
  "id": "cfbaadd933",
  "sourceFileUrl": "http://127.0.0.1:9000/...&x-id=GetObject",
  "streamId": "conv_2eb2f9e4b6201865396b",
  "status": "pending"
}
```

使用方式：

1. 第三方根据 `id` 读取当前任务的源文件下载地址
2. 直接用 `sourceFileUrl` 拉原始文件
3. 之后调用 `start`

`curl` 示例：

```bash
curl http://localhost:3000/api/v1/file-conversions/cfbaadd933/params \
  -H 'X-File-Conversion-Token: file-conversion-service-token'
```

### 6. 开始转换

- 方法：`POST`
- 路径：`/api/v1/file-conversions/:id/start`
- 调用方：第三方服务

请求体：

```json
{
  "operator": "converter-service"
}
```

成功响应：

```json
{
  "fileId": "cfbaadd933",
  "eventId": "6526d33053",
  "streamId": "conv_2eb2f9e4b6201865396b",
  "status": "queued"
}
```

使用方式：

1. 第三方决定领取任务后调用本接口
2. 后端会校验源文件存在
3. 成功后生成 `eventId`
4. 后续 `processing` 和 `callback` 都依赖这个 `eventId`

幂等说明：

- 如果重复调用，且当前任务已是 `queued` 或 `processing`
- 服务端会返回已有事件，而不是重新创建

`curl` 示例：

```bash
curl -X POST http://localhost:3000/api/v1/file-conversions/cfbaadd933/start \
  -H 'Content-Type: application/json' \
  -H 'X-File-Conversion-Token: file-conversion-service-token' \
  -d '{
    "operator": "converter-service"
  }'
```

### 7. 标记转换中

- 方法：`POST`
- 路径：`/api/v1/file-conversions/:id/processing`
- 调用方：第三方服务

请求体：

```json
{
  "eventId": "6526d33053",
  "operator": "converter-service"
}
```

成功响应：

```json
{
  "fileId": "cfbaadd933",
  "eventId": "6526d33053",
  "status": "processing"
}
```

使用方式：

1. 第三方真正启动转换引擎后调用
2. 用于把状态从 `queued` 推进到 `processing`

幂等说明：

- 重复调用会直接返回当前 `processing`

`curl` 示例：

```bash
curl -X POST http://localhost:3000/api/v1/file-conversions/cfbaadd933/processing \
  -H 'Content-Type: application/json' \
  -H 'X-File-Conversion-Token: file-conversion-service-token' \
  -d '{
    "eventId": "6526d33053",
    "operator": "converter-service"
  }'
```

### 8. 获取结果文件上传地址

- 方法：`POST`
- 路径：`/api/v1/file-conversions/:id/result-upload-url`
- 调用方：第三方服务

请求体：

```json
{
  "fileName": "robots-converted.txt",
  "fileSize": 26,
  "contentType": "text/plain"
}
```

成功响应：

```json
{
  "fileId": "cfbaadd933",
  "uploadUrl": "http://127.0.0.1:9000/...&x-id=PutObject",
  "resultObjectKey": "file-conversion/result/cfbaadd933/robots-converted.txt",
  "resultFileUrl": "http://127.0.0.1:9000/...&x-id=GetObject"
}
```

使用方式：

1. 第三方转换完成后，先申请结果文件上传地址
2. 用返回的 `uploadUrl` 把结果文件 `PUT` 到 MinIO
3. 上传成功后，再调 `callback`

`curl` 示例：

```bash
curl -X POST http://localhost:3000/api/v1/file-conversions/cfbaadd933/result-upload-url \
  -H 'Content-Type: application/json' \
  -H 'X-File-Conversion-Token: file-conversion-service-token' \
  -d '{
    "fileName": "robots-converted.txt",
    "fileSize": 26,
    "contentType": "text/plain"
  }'
```

结果文件上传示例：

```bash
curl -X PUT --upload-file ./robots-converted.txt "<uploadUrl>"
```

### 9. 转换结果回调

- 方法：`POST`
- 路径：`/api/v1/file-conversions/:id/callback`
- 调用方：第三方服务

成功回调请求体：

```json
{
  "eventId": "6526d33053",
  "status": "success",
  "resultObjectKey": "file-conversion/result/cfbaadd933/robots-converted.txt",
  "resultFileUrl": "http://127.0.0.1:9000/...&x-id=GetObject"
}
```

失败回调请求体：

```json
{
  "eventId": "6526d33053",
  "status": "failed",
  "message": "convert failed"
}
```

成功响应：

```json
{
  "fileId": "cfbaadd933",
  "eventId": "6526d33053",
  "status": "success"
}
```

使用方式：

1. 第三方结果文件上传完成后调用
2. 成功时传 `resultObjectKey`
3. 失败时传 `message`
4. 服务端更新文件状态和事件状态

`curl` 成功示例：

```bash
curl -X POST http://localhost:3000/api/v1/file-conversions/cfbaadd933/callback \
  -H 'Content-Type: application/json' \
  -H 'X-File-Conversion-Token: file-conversion-service-token' \
  -d '{
    "eventId": "6526d33053",
    "status": "success",
    "resultObjectKey": "file-conversion/result/cfbaadd933/robots-converted.txt",
    "resultFileUrl": "http://127.0.0.1:9000/...&x-id=GetObject"
  }'
```

`curl` 失败示例：

```bash
curl -X POST http://localhost:3000/api/v1/file-conversions/cfbaadd933/callback \
  -H 'Content-Type: application/json' \
  -H 'X-File-Conversion-Token: file-conversion-service-token' \
  -d '{
    "eventId": "6526d33053",
    "status": "failed",
    "message": "convert failed"
  }'
```

## 推荐调用顺序

### 前端用户流程

1. `POST /api/v1/file-conversions`
2. `PUT uploadUrl`
3. `POST /api/v1/file-conversions/:id/upload-complete`
4. `GET /api/v1/file-conversions`

### 第三方服务流程

1. `GET /api/v1/file-conversions/pending`
2. `GET /api/v1/file-conversions/:id/params`
3. `POST /api/v1/file-conversions/:id/start`
4. `POST /api/v1/file-conversions/:id/processing`
5. `POST /api/v1/file-conversions/:id/result-upload-url`
6. `PUT uploadUrl`
7. `POST /api/v1/file-conversions/:id/callback`

## 常见错误

### `401 Missing x-file-conversion-token`

原因：

- 第三方接口缺少固定 token 请求头

处理：

- 增加 `X-File-Conversion-Token`

### `403 Invalid service token`

原因：

- 第三方 token 配置错误

处理：

- 检查后端 `.env` 中的 `FILE_CONVERSION_SERVICE_TOKEN`
- 检查请求头值是否一致

### `404 File conversion record not found`

原因：

- `id` 不存在
- 记录已被删除

处理：

- 检查路径参数是否正确

### `409 Current status ... does not allow ...`

原因：

- 当前状态不允许继续执行对应动作

处理：

- 按状态机顺序调用接口

### `400 ETag mismatch`

原因：

- 上传完成确认时传的 `etag` 与 MinIO 实际对象不一致

处理：

- 使用 MinIO `PUT` 响应头返回的原始 `etag`

## 当前实现说明

- 当前下载地址统一走签名 URL
- 不依赖 MinIO 公共读权限
- 当前第三方鉴权方案是固定 token，后续可升级为签名机制
- 当前功能已完成真实联调验证
