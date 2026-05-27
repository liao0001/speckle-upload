# 修改日志

## 2026-05-15 14:10
- 初始化 Revit 2022 插件项目 `SpeckleUpload`
- 新增 `SpeckleUploadApp`：Revit 启动时自动启动 HTTP 监听
- 新增 HTTP 服务（默认端口 6688，环境变量 `SPECKLE_UPLOAD_HTTP_PORT`）
- 实现 `POST /upload`：打开指定 RVT、转换 Physical Objects、发送到 Speckle stream
- 完成后回调 `SPECKLE_UPLOAD_CALLBACK_URL`（默认 `http://127.0.0.1:6689/api/callback`），并关闭文档
- 使用 `ConverterRevit` + `Operations.Send` 完成 Speckle 上传
- 生成 `SpeckleUpload.addin`（ClientId: `8eee0545-1923-46bf-a7aa-30f31f4dd7bd`）

## 2026-05-15 14:23
- 新增 GitHub Actions：`.github/workflows/specleupload-install.yml`，在 `windows-latest` 上 `dotnet build` Release，并上传 `speckleupload-revit2022-install` 制品（含 `SpeckleUpload.addin` 与输出 DLL）

## 2026-05-15 14:58
- 修复 `SpeckleSendService` 与 Speckle.Core 2.23.2 API 兼容：`ServerInfo` 使用 `Speckle.Core.Api.GraphQL.Models`；`Operations.Send` 改用新签名；`CommitCreate` 改为 `VersionResource.Create`

## 2026-05-15 15:03
- Speckle.Core 2.23.2 的 `Client` 无 `VersionResource`，改回 `CommitCreate` 并抑制 CS0618 过时警告

## 2026-05-15 18:03
- 修复上传任务未执行：`ExternalEvent.Raise` 检查 Accepted/Pending/Denied；`Execute` 改用 Revit 传入的 `UIApplication`；增加 `Idling` 重试与诊断日志

## 2026-05-15 18:10
- 日志改为写入插件目录 `SpeckleUpload.log`（程序集所在目录）；`PluginLog.Step` 分阶段记录；HTTP/上传回调/文档/Speckle 转换各步骤均写日志

## 2026-05-15 18:34
- 修复 Revit API 限制：不能先 API 关闭「当前活动」文档；改为 `PrepareDocumentForUpload`（先打开目标 RVT 再关其它文档）；上传结束后关闭当前文档改为下一次 `Idling` 延迟执行

## 2026-05-15 18:43
- 新增 `Install-SpeckleUpload.ps1`：解压目录一键部署到 `%APPDATA%\...\SpeckleUpload`、Unblock、`pause`；CI 制品打包包含该脚本；`说明.md` 补充「自动部署脚本」说明

## 2026-05-15 18:51
- 新增 `Install-SpeckleUpload.cmd`（Bypass 执行策略）并纳入 CI 制品；`说明.md` 推荐双击 `.cmd`；`.ps1` 顶部补充绕过说明

## 2026-05-15 18:56
- `说明.md` 功能点 1 与实现一致：先打开目标模型再关闭其它文档（并注明 Revit API 限制）

## 2026-05-19 10:30
- 转换循环单元素 try/catch，避免 MEP 等单构件异常中断整次上传；`Operations.Send` / `CommitCreate` 失败单独写日志；Execute 结束记录 success/error
- `CloseOtherDocumentsExcept` 用路径比较识别同一文档，避免误关当前活动模型

## 2026-05-19 10:39
- 新增 `upload-rvt.sh`：按 API.md 实现创建上传任务、MinIO PUT、upload-complete 确认（依赖 curl、jq）

## 2026-05-19 10:49
- 启动日志输出程序集版本与 DLL 修改时间；CommitCreate 前记录 branchName/streamId

## 2026-05-19 13:22
- `CommitCreate` 失败时若 `Operations.Send` 已成功，回调仍返回 `objectId`；增强 GraphQL 错误日志

## 2026-05-19 15:31
- `POST /upload` 请求体固定按 UTF-8 解码，修复中文 `commitMessage` 在 Speckle 页面乱码
- 回调 `POST /api/callback` JSON 字段改为 snake_case（`request_id`、`file_path`、`stream_id` 等）

## 2026-05-19 15:43
- 回调体增加 `branch_name`、`commit_message`（Speckle 成功/部分成功时为实际提交值）

## 2026-05-19 15:54
- 默认回调路径由 `/api/callback` 改为 `/api/speckle/upload/callback`（后于 16:02 按 speckle_sync 文档改回 `/api/callback`）

## 2026-05-19 16:02
- 对接 speckle_sync：`/upload` 与回调响应解析改为 lwhale `ret/msg/error`；支持请求体 `callbackUrl`；新增 `SPECKLE_SYNC.md`

## 2026-05-22 22:09
- 修复 `TryCloseDocument`：`Close()` 后勿再访问 `Title`，关闭前缓存文档标签，避免 `InvalidObjectException` 中断上传

## 2026-05-26 15:03
- 支持 Revit 2024 构建：`-p:RevitVersion=2024` + `Speckle.Objects.Converter.Revit2024`；CI 矩阵产出 `speckleupload-revit2024-install`；`Install-SpeckleUpload-2024.cmd`、`SpeckleUpload.Revit2024.addin`

## 2026-05-26 15:11
- 2024 CI 编译失败：补充 `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI`（`$(RevitVersion).*`），因 Converter.Revit2024 不暴露 Autodesk 命名空间

## 2026-05-26 15:57
- 打开 RVT 时订阅 `DialogBoxShowing`，自动关闭跨版本升级后的「图元不兼容」等弹窗；可配置抑制时长与 `SPECKLE_UPLOAD_AUTO_DISMISS_ALL_OPEN_DIALOGS`

## 2026-05-26 16:05
- 修复 2024 编译：`TaskDialogShowingEventArgs` 使用基类 `Message`；`OverrideResult` 改用整型常量替代 internal 的 `TaskDialogResult`

## 2026-05-26 16:12
- 修复 2024 编译：`Message` 改从 `TaskDialogShowingEventArgs` / `MessageBoxShowingEventArgs` 读取（Nice3point 基类无此属性）

## 2026-05-27 10:05
- CI 制品改为带版本信息的 zip：`speckle-upload-{2022|2024}-{yyyyMMddHHmmss}-{commit6}.zip`，包内附带 `BUILD_INFO.txt`；恢复 2022/2024 矩阵构建

## 2026-05-27 17:11
- 打开 RVT 弹窗改为规则文件 `SpeckleUpload.open-dialog-rules.json`：`never` 永不代点（含取消升级）；`rules` 按文案匹配 click close/ok；移除误匹配「升级」关键词

## 2026-05-27 17:25
- 弹窗规则：图1 确定(ok)、图2 取消连接图元(commandLink1)、图3 结构分析升级关闭(close)；支持 commandLink1/2 与 clickResult

## 2026-05-27 17:35
- 修复图1未关闭：`Dialog_Revit_DocWarnDialog` 专用处理；MessageBox 确定用 OverrideResult(6)；DialogId 为空时仍可按文案匹配；默认抑制 120 秒

## 2026-05-27 17:45
- 弹窗日志增强：打印全部属性、匹配结果说明、JSON 规则逐条扫描；连接错误尝试 commandLink1/1002；按文案识别「不能忽略/无法使图元保持连接」

## 2026-05-27 18:25
- 弹窗规则支持 `titleContains`（OR）+ `buttonActions`（按按钮文案选 click）；`messageContains` 明确为 OR；已按三张图更新 JSON

## 2026-05-27 18:44
- 图1/图2：`Dialog_Revit_DocWarnDialog` 专用代点（连接错误 1001→1002，警告 1→6）；`never` 优先于专用逻辑
- 规则匹配：`titleContains`/`messageContains` 在全文匹配，两组之间 OR；破折号归一化
- `OverrideResult` 沿类型继承链反射调用；JSON 为 fig1/fig2 增加 `dialogIdContains` 与 `clickResult`

## 2026-05-27 18:47
- 未命中 `rules` 时按 `unmatchedFallback.tryButtons` 顺序代点（默认：取消连接图元→确定→关闭）
- 兜底仍失败时输出「未匹配到处理措施」块（含弹窗与按钮信息），便于后续补充 JSON 规则
