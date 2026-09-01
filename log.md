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

## 2026-05-27 19:07
- 修复弹窗抑制导致 Speckle 不上传：`PrepareDocumentForUpload` 结束后 `CompleteOpenPhase()` 关闭代点，避免转换/发送阶段误点弹窗
- `SpeckleSendService.SendPhysicalObjects` 在 ExternalEvent 线程同步执行，`ConfigureAwait(true)` 保持 Revit 上下文

## 2026-05-27 19:23
- 修复 DocWarn 无正文时误点 Ok(1) 导致 `Opening was canceled`：反射 DeepText；无正文时按 `docWarnEmptyMessageSequence` 顺序代点（默认 确定→取消连接图元→关闭）
- 确定类弹窗优先尝试 code 6 再 1；移除 DocWarn 假按钮注入以免误判

## 2026-05-27 19:27
- 修复 CI 编译错误：删除重复的 `CreateDefaultDocWarnEmptyMessageSequence` 重载

## 2026-05-27 19:38
- 修复 DocWarn DialogBox 误用 code 1/6 导致 `Opening was canceled`：DialogBox 确定改用 `DialogResult.Retry(4)`（`docWarnOk`）
- `OverrideResult` 检查返回值 `accepted`；每个弹窗只代点一次；日志增加 `DialogSurface`

## 2026-05-27 19:55
- `docWarnEmptyMessageSequence` 顺序改为与实际一致：取消连接/关联图元 → 确定 → 关闭

## 2026-05-27 20:00
- 移除 Win32 代点（`Win32DialogClicker`）；仍用 `OverrideResult` + `docWarnEmptyMessageSequence` 顺序（1001→4→8）

## 2026-05-27 20:06
- DocWarn 代点优先级：JSON rules（正文）→ 正文/按钮关键词 → `docWarnEmptyMessageSequence` 顺序兜底；日志输出可读正文/按钮

## 2026-05-27 22:06
- 修复编译：`OpenDialogButtonAction` → `OpenDialogFallbackButton` 转换
- 打开阶段 `Dialog_Revit_DocWarnDialog`(DialogBox) 不再 `OverrideResult`（1001/4 等 accepted 仍会 Opening was canceled）；改由 Revit 窗口手动点完弹窗

## 2026-05-27 22:13
- 无人值守 + 可前台：DocWarn DialogBox 用 Win32 枚举可见按钮点击；弹窗出现后读 Static 正文匹配 rules，否则按 `docWarnEmptyMessageSequence`；自动 SetForegroundWindow Revit

## 2026-05-28 10:22
- Win32：仅点前台模态框；优先按可见正文智能匹配（删除图元→确定，连接→取消连接，结构分析→关闭）
- 打开阶段所有 DialogBox 走 Win32；顺序兜底改为 确定→取消连接→关闭；打开后尝试关闭右下角警告条

## 2026-08-03 22:22
- 默认 HTTP 端口：Revit 2022=6687、2024=6688（`SPECKLE_UPLOAD_HTTP_PORT` 可覆盖）
- 内置弹窗处理默认关闭（AHK）；`SPECKLE_UPLOAD_ENABLE_DIALOG_SUPPRESSION=1` 才启用
- 进度合并至 `/api/callback`：新增 `progress`（打开/解析/上传/完成）、`progress_index`（解析=convert index，上传=1，完成=object_count）
- 移除独立 `/api/progress` 与 `progressUrl`

## 2026-08-04 22:57
- 打开 RVT 默认 `DetachAndDiscardWorksets`（从中心分离）；并设 `AllowOpeningLocalByWrongUser=true` 避免他人 local 文件无法打开

## 2026-08-05 14:45
- 回调 HTTP 默认超时 30s（`SPECKLE_UPLOAD_CALLBACK_TIMEOUT_SECONDS`）；原 HttpClient 默认 100s 会在 Revit 主线程同步等待导致“卡住”
- 增强耗时日志：`elapsedMs` 覆盖打开/解析/上传/callback/关文档；Callback 记录 PostAsync 超时与响应体

## 2026-08-05 14:49
- 修复 net48 编译：`CallbackService` 响应体预览改用 `Substring`，避免 `System.Index`/`System.Range` 语法

## 2026-08-05 15:15
- `Operations.Send` 增加 onProgress 回调与 15s 心跳日志；上传进度 `progress_index` 随 Speckle 上报（每 500）
- 网络阶段 `ConfigureAwait(false)`，CommitCreate 同步加耗时日志

## 2026-08-05 15:18
- 回调 HTTP 默认超时由 30s 改为 **20 分钟**（1200s，`SPECKLE_UPLOAD_CALLBACK_TIMEOUT_SECONDS` 可覆盖）

## 2026-08-05 15:35
- 修复编译：`Operations.Send` 的 `transports` 参数改为 `List<ITransport>`

## 2026-08-05 16:25
- 进度 callback 不再发 `success=false`；新增 `is_final`（进度=false，最终=true），避免 speckle_sync 误判失败

## 2026-08-05 18:36
- 最终 callback（`is_final=true`）在关文档**之前**同步发出；关文档改为 Idling 异步 `CloseUploadedDocument`
- `SPECKLE_SYNC.md` 补充 3.1 完成判定（`is_final` + `progress=完成`）及 speckle_sync 推送远端时机
- 删除 `DocumentService.CloseActiveDocumentLegacy` 重复方法

## 2026-08-05 19:03
- `progress_index` 改为 **0–100 整体百分比**：固定里程碑 1/5/6/9/10/91/100；解析 10–50、上传 50–90 按比例计算
- HTTP 层增加 `接收`(1)、`入队`(5) 进度回调；Execute 开始上报 `执行`(6)

## 2026-08-12 14:39
- 进度上报增加时间心跳（方式 D）：默认每 **30 秒**强制 callback（解析/上传），环境变量 `SPECKLE_UPLOAD_PROGRESS_HEARTBEAT_SECONDS`
- `Operations.Send` 本地 onProgress 日志同步改为每 500 或每 30 秒，避免刷屏

## 2026-08-21 17:13
- 新增 `docs/NavisWorks插件实现指南.md`：按本仓库 Revit 插件对照写出 NavisWorks 版逐步实现说明（HTTP/进度/Speckle 契约复用，Idle 调度与 OpenFile 替换文档，完成后不关文件）

## 2026-08-27 15:08
- 提交树改为对齐官方 Speckle Next：`Level → Category → Type`（`LevelCategoryCommitBuilder`）
- 取消宿主嵌套（结构柱不再挂到依附楼板下）；Category 使用本地化 `Category.Name`（如「结构柱」）
- `SpeckleSendService` 根对象改为 `ConvertToSpeckle(document)`（含 ProjectInfo），不再用默认 `ByCollection` + Host 嵌套

## 2026-08-27 22:33
- 诊断转换“卡住”：循环内 `ConvertToSpeckle` 阻塞时原进度心跳不会触发
- `SpeckleSendService` 增加转换后台心跳（约 15–30s）输出当前 `index`/构件 id/category/name；单构件 ≥3s 记 slow convert

## 2026-08-27 22:36
- 转换循环增加 `Application.DoEvents` 节流让出（约每 150ms），减轻 Revit 任务管理器「无响应」；`UseWindowsForms=true`

## 2026-08-29 19:29
- 新增 `docs/官方Revit插件改造实现指南.md`：以 Speckle 官方 Revit Connector 为唯一上传真源，说明如何接入 HTTP、自动打开/关闭文档、Physical Objects、弹窗处理、进度回调和日志等现有功能
- 明确官方兼容模式与 `Level → Category → Type` 自定义模式的边界，避免将自定义提交树误认为与官方上传结果一致
- 补充官方版本固定、依赖一致性、SendStream/Converter context、构建安装、内容对比和真实 Revit 验收要求

## 2026-09-01 06:46
- 诊断 Revit 在 `ConvertToSpeckle` 内硬阻塞导致进度停在解析阶段：`connector modifier is inaccessible` 为可跳过异常，与 UI 无响应不是同一问题
- `SpeckleSendService`：每次 `ConvertToSpeckle` 前写 `convert begin` 日志，便于定位卡死图元；心跳改为 `ConvertHeartbeatState` 只读 UI 线程写入的 index/label
- 新增 MEP 族 `HasInaccessibleConnectorModifier` 预检，提前跳过 connector modifier 不可访问的 FamilyInstance，减少 Revit 无响应风险

## 2026-09-01 06:47
- 打开与 Speckle 转换解耦：`OpenAndActivateDocument` 返回后默认再等 3 次 Revit Idling 才开始转换（`SPECKLE_UPLOAD_POST_OPEN_IDLE_TICKS`），避免 Revit 仍在加载/重生成时进入 `ConvertToSpeckle`
- 新增 `DocumentService.EnsureDocumentReadyForConversion`：同步等待 Win32 打开收尾、UI settle（默认 2s）、`Regenerate` 后再转换
- `Win32DialogClicker.WaitForOpenPhaseComplete`：打开阶段 Win32 代点改为可等待完成，避免与转换并行

## 2026-09-01 06:50
- 打开后等待策略改为代码内默认值（`DefaultPostOpenIdleTicks=3`、`DefaultPostOpenSettleSeconds=2`），部署无需配置环境变量
- 旧行为可选：`SPECKLE_UPLOAD_IMMEDIATE_CONVERT_AFTER_OPEN=1` 时打开后立即转换，跳过 Idling/settle/Regenerate
- 启动日志输出 `DescribePostOpenConvertPolicy()`；`说明.md` 补充默认行为说明

## 2026-09-01 20:04
- 新增 Revit **2026** 构建：`-p:RevitVersion=2026`，目标框架 `net8.0-windows`，默认 HTTP 端口 **6691**
- NuGet 尚无 `Speckle.Objects.Converter.Revit2026`，暂引用 `Speckle.Objects.Converter.Revit2025` 2.23.2
- 新增 `SpeckleUpload.Revit2026.addin`、`Install-SpeckleUpload-2026.cmd`；CI 矩阵增加 2026；`ElementId` 判断改为 `REVIT2022` vs 其它版本

## 2026-09-01 21:13
- 修复 Revit 2026 运行时 `ElementId.IntegerValue` 缺失：本地实现 `IsPhysicalElement`（`Revit/RevitElementExtensions.cs`），不再调用 NuGet `RevitSharedResources` 扩展
- 新增 `Revit/ElementIdCompat.cs` 统一 `ElementId` 读写（2022 用 `IntegerValue`，2024+ 用 `Value`）
- 新增构建后工具 `tools/PatchElementIdForRevit2026`：对输出目录内 Speckle Converter 等 DLL 做 IL 补丁（`get_IntegerValue` → `get_Value` + `conv.i4`，`ElementId(int)` → `ElementId(long)`）
- `SpeckleUpload.csproj` 2026 构建后自动执行补丁；CI 2026 矩阵增加显式补丁步骤

## 2026-09-01 21:49
- 修复 CI 三版本编译失败：`tools/PatchElementIdForRevit2026/Program.cs` 被 SDK 默认 glob 编入主项目，现 `Compile Remove="tools/**"` 排除
- 恢复误删的 `Newtonsoft.Json` 包引用
- 新增 `Directory.Build.props` 提前设置 `BaseOutputPath`/`BaseIntermediateOutputPath`，消除 MSB3539 警告

## 2026-09-01 21:56
- 修复 2026 CI 仍编译 `tools/PatchElementIdForRevit2026/Program.cs` 入主项目：改用 `Directory.Build.props` 的 `DefaultItemExcludes=tools\**`；移除 `SpeckleUpload.csproj` 内 PostBuild `dotnet run`（与 `--no-restore` 冲突）
- 补丁工具加入 `SpeckleUpload.sln`，`dotnet restore` 会拉取 `Mono.Cecil`；CI 2026 单独 `dotnet build` + `dotnet run --no-build` 执行补丁
- 修复 `SpeckleUpload.csproj` 因误编辑导致的 XML 结构损坏

## 2026-09-01 22:02
- 修复补丁工具编译错误：Mono.Cecil 0.11 无 `MethodBody.OptimizeMacros()`，已移除该调用
- 补丁工具移出 `SpeckleUpload.sln`，避免 2022/2024 矩阵误编；CI 仅在 2026 时单独 restore/build 补丁项目

## 2026-09-01 22:10
- 修复补丁工具运行时 `Failed to resolve assembly: RevitAPI`：补丁项目引用 `Nice3point.Revit.Api.RevitAPI` 2026，自定义 `RevitApiAssemblyResolver` 在 Cecil 读写时解析 RevitAPI；`get_Value`/`.ctor(long)` 改用 `ImportReference`

## 2026-09-01 22:23
- 修复补丁工具 restore 失败：`PatchElementIdForRevit2026` 目标框架由 `net8.0` 改为 `net8.0-windows`（与 Nice3point Revit API 2026 包一致）

## 2026-09-01 22:30
- 修复补丁工具编译：`Mono.Cecil` 0.11 的 `WriterParameters` 无 `AssemblyResolver`，改为 `assembly.Write(path)`
- CI 拆为两个 job：`build-2022-2024`（仅编插件）与 `build-2026`（插件 + 补丁工具）；2026 直接执行补丁 exe，避免 `dotnet run --no-build` 找不到文件

## 2026-09-01 22:35
- 补丁工具改为纯 Cecil 实现，运行时不再加载 `RevitAPI`（避免 `FileNotFoundException`）；CI 从 NuGet 缓存定位 `RevitAPI.dll` 并作为第 2 参数传入
- 补丁工具目标框架改回 `net8.0`（仅依赖 Mono.Cecil，无需 Revit API 包）
- CI 暂时停用 2022/2024 构建，仅保留 `build-2026` job

## 2026-09-01 22:39
- 修复 CI 找不到 `RevitAPI.dll`：Nice3point 包将 DLL 放在 `Content/` 而非 `lib/`；`SpeckleUpload.csproj` 为 Revit API 包启用 `GeneratePathProperty`，CI 用 `dotnet msbuild -getProperty:PkgNice3point_Revit_Api_RevitAPI` 定位包目录后递归查找
