# 基于 Speckle 官方 Revit Connector 的改造实现指南

本文档描述下一代项目的实现方式：**以 Speckle 官方 Revit Connector 为基础，在官方上传链路外增加本项目已有的自动化能力**。

目标不是再写一个独立的 `ConverterRevit + Operations.Send` 小插件，而是让官方 Connector 负责：

- Converter 的加载、版本和生命周期；
- Revit 文档缓存与上下文初始化；
- 元素选择和关系处理；
- 提交对象树的构造；
- Speckle 对象上传及 Commit/Version 创建。

本项目只负责接入：

- Revit 启动后自动提供本地 HTTP 接口；
- 通过 HTTP 接收上传任务；
- 自动打开指定 RVT；
- 自动收集全模型 Physical Objects；
- 将任务排入官方 Revit UI 调度器；
- 打开阶段弹窗处理；
- 进度和最终结果回调；
- 上传完成后关闭文档；
- 日志、安装和联调。

## 0. 最重要的结论

### 0.1 “完全和官方插件一致”有严格含义

如果目标是让上传内容和官方插件一致，以下内容必须使用官方实现，不能在外围重新实现：

1. 官方 Connector 使用的 Speckle SDK/Core 版本；
2. 官方 Connector 使用的 Converter 包或官方 Kit 加载方式；
3. `RevitConverterState.Push()` 的范围；
4. `SetContextDocument(...)` 传入的官方文档缓存对象；
5. 官方的 `SetConverterSettings(...)`；
6. 官方的 `SetContextObjects(...)`；
7. 官方的元素选择逻辑；
8. 官方的 `IRevitCommitObjectBuilder`；
9. 官方当前版本的上传方式（可能是 `Operations.Send`，也可能是 `SendPipeline`）；
10. 官方当前版本的 Commit/Version 创建方式。

因此，新项目必须从官方 Connector 的**固定版本或固定 commit** fork，不能只引用本项目现在的 `Speckle.Objects.Converter.Revit2022/2024 2.23.2`，再复制几段官方代码。

### 0.2 当前项目为什么会产生不同内容

当前 `Services/SpeckleSendService.cs` 做了以下自定义行为：

- 直接 `new ConverterRevit()`；
- 自己调用 `SetContextDocument(document)`；
- 自己调用 `SetContextObjects(...)`；
- 自己调用 `converter.ConvertToSpeckle(document)`；
- 自己用 `LevelCategoryCommitBuilder` 构造 `Level → Category → Type`；
- 自己调用 `Operations.Send` 和 `CommitCreate`。

这些步骤即使类型名称与官方代码相同，也不代表结果相同。官方实现还可能依赖：

- `KitManager` 加载的 Converter；
- `RevitDocumentAggregateCache`；
- `UIDocumentProvider`；
- 官方的 converter settings；
- 官方的元素选择集合；
- 已转换对象缓存和关系排序；
- 官方提交对象构造器；
- 官方版本创建或 SendPipeline；
- `PreviousCommitId`、source application 等状态字段。

新项目不得把当前 `LevelCategoryCommitBuilder` 作为默认提交路径。它可以保留为“自定义分组模式”，但该模式的内容不应再宣称与官方上传结果相同。

### 0.3 两个目标不能同时默认成立

下面两个目标在同一个 commit 中存在冲突：

- 目标 A：严格复现官方 Connector 的对象树和对象 ID；
- 目标 B：强制把对象改造成 `Level → Category → Type` 自定义树。

只要改变根对象、Collection、对象关系或序列化路径，上传对象 ID 就可能变化。因此必须提供明确模式：

- `OfficialCompatible`：默认模式，官方上传结果优先；
- `LevelCategoryCustom`：兼容当前项目的自定义分组模式，允许结果与官方不同。

如果业务只要求“支持按标高/类别/类型查看”，优先在 Speckle 端使用官方对象属性、查询或视图实现，不要改变上传对象树。

## 1. 新项目的边界和目录

建议新仓库名称：

```text
SpeckleRevitOfficialFork/
```

建议目录：

```text
SpeckleRevitOfficialFork/
├── ConnectorRevit/                         # Speckle 官方源码，尽量少改
├── SpeckleUploadBridge/
│   ├── Http/
│   │   ├── HttpUploadServer.cs
│   │   └── LwhaleJsonResponse.cs
│   ├── Models/
│   │   ├── UploadRequest.cs
│   │   ├── UploadCallbackPayload.cs
│   │   ├── LwhaleResponse.cs
│   │   └── OpenDialogRulesConfig.cs
│   ├── Services/
│   │   ├── UploadJob.cs
│   │   ├── UploadQueue.cs
│   │   ├── UploadCallbackReporter.cs
│   │   ├── CallbackService.cs
│   │   ├── PluginLog.cs
│   │   ├── RevitDocumentService.cs
│   │   ├── RevitOpenDialogSuppression.cs
│   │   ├── OpenDialogRulesLoader.cs
│   │   └── Win32DialogClicker.cs
│   ├── OfficialSendAdapter.cs
│   ├── OfficialUploadApplication.cs
│   └── PluginSettings.cs
├── SpeckleRevitOfficialFork.sln
├── docs/
└── scripts/
```

实际目录名称以官方仓库的解决方案为准。上面的 `SpeckleUploadBridge` 是新增适配层，不应复制一个完整的第二套 Connector。

### 1.1 可以从当前项目迁移的代码

迁移时可以参考或复制以下内容，但必须先改 namespace、生命周期和配置：

- `Models/UploadRequest.cs`
- `Models/UploadCallbackPayload.cs`
- `Models/LwhaleResponse.cs`
- `Models/OpenDialogRulesConfig.cs`
- `Http/LwhaleJsonResponse.cs`
- `Http/HttpUploadServer.cs`
- `Services/CallbackService.cs`
- `Services/UploadCallbackReporter.cs`
- `Services/PluginLog.cs`
- `Services/OpenDialogRulesLoader.cs`
- `Services/RevitOpenDialogSuppression.cs`
- `Services/Win32DialogClicker.cs`
- `SpeckleUpload.open-dialog-rules.json`

### 1.2 不能直接复制为默认实现的代码

以下代码必须改成调用官方 Connector，而不是继续使用当前实现：

- `Services/SpeckleSendService.cs`
- `Services/LevelCategoryCommitBuilder.cs`
- `Services/UploadEventHandler.cs`
- `Services/DocumentService.cs`
- `SpeckleUploadApp.cs`

## 2. 先固定官方基线

### 2.1 记录版本

在新仓库根目录增加 `OFFICIAL_BASELINE.md`，至少记录：

```text
official repository: specklesystems/speckle-sharp
official branch: <branch>
official commit: <full sha>
connector version: <version>
revit versions: 2022 / 2024 / ...
speckle core version: <version>
converter version: <version>
send path: Operations.Send / SendPipeline / official current path
```

不能只记录 NuGet 版本。官方 Connector 的源码、Converter、Core、DesktopUI2 和 RevitSharedResources 需要保持同一套依赖关系。

### 2.2 必须先阅读的官方代码

以固定 commit 为准，找到并阅读：

1. `ConnectorBindingsRevit` 的构造函数和静态初始化；
2. 官方 Revit 插件入口；
3. `ConnectorBindingsRevit.Send.cs`；
4. 官方 `RevitCommitObjectBuilder` 或 `IRevitCommitObjectBuilder` 实现；
5. 官方 Revit storage/cache 实现；
6. 官方 Revit UI 线程调度器；
7. 官方 `StreamState` 定义；
8. 官方 `ProgressViewModel` 和取消逻辑；
9. 官方项目的 `.csproj`、`.slnf`、构建脚本和安装方式。

新代码中的类型名必须以固定 commit 的源码为准。不能假设未来版本仍然叫 `CommitCreate`、`VersionResource.Create` 或 `Operations.Send`。

### 2.3 依赖一致性规则

禁止以下组合：

- 官方 Connector 的 DLL + 当前项目旧版 `Speckle.Core.dll`；
- 官方 Converter + 另一版本的 `Speckle.Core.dll`；
- 手工复制部分官方 DLL 覆盖 Manager 安装的 DLL；
- 在插件目录中同时放两套同名 Speckle 程序集；
- 直接引用一个独立 Converter 包，却让 `KitManager` 加载另一套 Converter。

这类混用容易导致：

- `MissingMethodException`；
- `TypeLoadException`；
- converter report 或对象缓存行为不一致；
- 上传成功但对象树与官方不同；
- Revit 进程已经加载旧 DLL，替换文件后仍执行旧代码。

## 3. 总体运行流程

新流程应保持下面的边界：

```text
Revit 启动
  → 官方 Connector 正常初始化
  → UploadBridge 注册 HTTP 与官方 UI 调度器
  → POST /upload
  → 校验请求并返回 ret=0
  → 进度回调：接收 / 入队
  → 官方 UI 调度器执行任务
  → 打开目标 RVT
  → 打开阶段处理弹窗
  → 关闭打开阶段弹窗处理
  → 创建官方文档上下文和官方 converter
  → 收集全模型 Physical Objects
  → 通过官方 SendStream 路径转换、构造提交对象并上传
  → 创建 commit/version
  → 进度回调：提交
  → 同步发送最终 callback
  → 下一次安全的 Revit UI 调度点关闭上传文档
```

关键原则：

- HTTP 线程不能调用任何 Revit API；
- 文档打开、元素收集、Converter 初始化和转换必须在官方允许的 Revit UI 上下文执行；
- `Operations.Send` 或 `SendPipeline` 的选择必须跟随官方基线；
- 最终 callback 必须先于关闭上传文档；
- 过程 callback 失败不能中断官方上传；
- 一个进程内同一时间只允许一个自动化上传任务。

## 4. 官方 Connector 的接入方式

### 4.1 不增加第二个 Revit 应用入口

官方 Connector 已经有 `IExternalApplication` 入口。不要再用另一个独立 `.addin` 同时加载一套 `SpeckleUploadApp`，否则会出现：

- 两个 Connector 竞争 Revit 事件；
- 两套 Speckle 程序集被加载；
- 两个 HTTP 服务重复启动；
- 官方 UI 状态与自动化状态不一致；
- 文档关闭时误关用户正在编辑的文件。

优先方案是在官方入口初始化完成后创建 `OfficialUploadApplication`：

```csharp
public sealed class OfficialUploadApplication
{
  private HttpUploadServer? _server;
  private UploadQueue? _queue;

  public void Start(UIApplication uiApp, ConnectorBindingsRevit bindings)
  {
    _queue = new UploadQueue(uiApp, bindings);
    _server = new HttpUploadServer(_queue);
    _server.Start();
  }

  public void Stop()
  {
    _server?.Dispose();
    _server = null;
    _queue?.Stop();
    _queue = null;
  }
}
```

上面的 `ConnectorBindingsRevit`、官方入口事件和 UI 调度方式必须按固定官方版本调整。

### 4.2 优先复用官方 UI 调度器

当前项目的 `UploadEventHandler` 自己创建 `ExternalEvent`。改造官方插件时，先检查官方是否已有：

- `RevitTask`；
- `ExternalEvent`;
- `Idling` 队列；
- `UIApplication` 调度封装；
- 官方命令执行队列。

如果已有，HTTP 层只提交一个委托或 job，不再创建第二套调度器：

```csharp
public UploadEnqueueResult TryEnqueue(UploadJob job)
{
  lock (_sync)
  {
    if (_pending != null || _busy)
    {
      return new UploadEnqueueResult(
        UploadEnqueueStatus.Busy,
        "Another upload is in progress."
      );
    }

    _pending = job;
  }

  _officialRevitDispatcher.Post(ExecutePending);
  return new UploadEnqueueResult(UploadEnqueueStatus.Accepted);
}
```

如果官方版本没有可复用的调度器，才在官方插件内部增加一个 `ExternalEvent`，并把它作为唯一队列。必须在代码中保证：

- `_pending` 取出后立即进入 `_busy`；
- 任务执行期间第二单返回 Busy；
- `ExternalEvent.Raise()` 返回 `Denied` 时清理任务并回调失败；
- `Pending` 状态由官方 Idling 机制重试；
- Revit shutdown 时未执行任务必须回调失败或明确记录丢弃。

### 4.3 HTTP 服务不能持有官方 UI 对象到后台执行

`HttpUploadServer` 可以在后台线程运行，但只允许完成：

- 读取 HTTP body；
- UTF-8 JSON 反序列化；
- 请求校验；
- 创建 `UploadJob`；
- 提交官方 UI 调度器；
- 返回接收结果。

以下操作必须在官方 UI 线程：

- `OpenAndActivateDocument`；
- 读取 `Application.Documents`；
- `FilteredElementCollector`；
- 读取 `Element` 属性；
- 初始化带 Revit context 的 Converter；
- `ConvertToSpeckle(Element)`；
- 官方 commit builder；
- 关闭 Revit 文档。

## 5. HTTP 契约

### 5.1 监听和路由

默认端口保留当前项目约定：

- Revit 2022：`6687`
- Revit 2024：`6688`
- 环境变量：`SPECKLE_UPLOAD_HTTP_PORT`
- 默认 callback：`http://127.0.0.1:6689/api/callback`

接口：

```text
GET  /health
POST /upload
```

健康检查响应：

```json
{
  "status": "ok",
  "port": 6688
}
```

### 5.2 `/upload` 请求

请求体字段必须继续使用 camelCase：

```json
{
  "filePath": "C:\\models\\sample.rvt",
  "streamId": "1183495a7b",
  "serverUrl": "https://app.speckle.systems",
  "token": "<user-token>",
  "branchName": "main",
  "commitMessage": "automated upload",
  "requestId": "upload-001",
  "callbackUrl": "http://127.0.0.1:6689/api/callback"
}
```

必填字段：

- `filePath`
- `streamId`
- `token`

可选字段：

- `serverUrl`，默认 `https://app.speckle.systems`；
- `branchName`，默认 `main`；
- `commitMessage`；
- `requestId`，缺失时由插件生成；
- `callbackUrl`，优先级高于环境变量和默认值。

`filePath` 表示已经下载到本地的 RVT 文件。插件不负责从对象存储下载文件。

### 5.3 `/upload` 响应

成功只表示任务已经被接受：

```json
{
  "ret": 0,
  "msg": null
}
```

参数错误：

```json
{
  "ret": 1002,
  "error": "filePath is required.",
  "msg": null
}
```

系统错误或忙：

```json
{
  "ret": 500,
  "error": "Another upload is in progress.",
  "msg": null
}
```

不要在 `/upload` 的同步响应中返回最终 `objectId` 或 `commitId`。

## 6. 文档打开和关闭

### 6.1 打开顺序必须保留

Revit 不允许在不合适的上下文中先关闭当前活动文档。因此自动任务的顺序必须是：

1. 检查目标文件存在；
2. 判断目标是否已经是当前活动文档；
3. 如果不是，先在官方 UI 上打开并激活目标；
4. 打开成功后关闭其它非链接文档；
5. 打开阶段弹窗处理完成；
6. 关闭自动弹窗逻辑；
7. 执行官方转换。

目标已经是当前活动文档时：

- 不重复打开；
- 仍关闭其它非链接文档；
- 不触发不必要的升级弹窗流程。

### 6.2 打开选项

当前项目使用的选项需要作为可配置的自动化策略迁移：

```csharp
var openOptions = new OpenOptions
{
  DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets,
  AllowOpeningLocalByWrongUser = true,
  Audit = false,
};
```

注意：

- `DetachAndDiscardWorksets` 会改变中心模型/工作集打开语义，必须在业务上确认；
- `AllowOpeningLocalByWrongUser=true` 是为了无人值守打开他人 local 文件，可能隐藏协作模型问题；
- 不要让官方 Connector 的普通用户打开流程无条件继承自动化选项；
- 建议把这些选项只用于 HTTP 自动化任务。

### 6.3 关闭其它文档

关闭其它文档时：

- 跳过 `IsLinked` 文档；
- 用对象引用和规范化路径双重判断目标文档；
- 关闭前缓存 `Title`、`PathName` 等日志字段；
- `Close(false)` 放弃未保存修改，必须在文档中明确提示；
- 关闭失败只记录日志，并把失败状态纳入诊断。

### 6.4 最终 callback 与关闭时序

正确时序：

```text
官方上传完成
  → 构造最终 payload
  → 同步 POST callback
  → callback 返回 ret=0 或失败
  → 下一次安全的 UI 调度点关闭上传文档
```

不能在最终 callback 之前关闭 RVT，因为：

- 回调可能需要等待很长时间；
- speckle_sync 可能在 callback 返回后才认为任务结束；
- 关闭文档可能导致官方转换上下文失效；
- callback 失败时仍需要保留现场日志。

## 7. 读取全模型 Physical Objects

### 7.1 选择模式

新增自动上传必须提供两个选择模式：

```text
OfficialSelection
AllPhysicalObjects
```

默认自动化模式为 `AllPhysicalObjects`，但它必须把元素集合接入官方 `SendStream` 的选择入口，而不是绕过官方发送方法自行转换。

推荐流程：

1. 在官方 UI 线程使用当前项目的 Physical Object 收集规则；
2. 得到 `UniqueId` 集合；
3. 按官方 `StreamState` 或官方 selection model 设置选择；
4. 调用官方 `SendStream`；
5. 让官方 Send 代码完成后续 context、converter 和 commit builder 工作。

当前物理元素收集规则是：

```csharp
var elements = new FilteredElementCollector(document)
  .WhereElementIsNotElementType()
  .WhereElementIsViewIndependent()
  .ToElements()
  .Where(element => element.IsPhysicalElement())
  .ToList();
```

具体 `IsPhysicalElement()` 扩展方法、官方 selection state 的字段名称和类型必须以官方基线为准。

### 7.2 Design Option

保留当前项目的过滤语义：

- 没有次要 Design Option：保留全部 Physical Objects；
- 有激活 Design Option：保留未加入 Design Option 的元素和激活选项元素；
- 没有激活选项：保留未加入选项的元素和 Primary 选项元素。

过滤发生在“设置官方 selection state”之前。日志至少记录：

```text
physical count before design-option filter
secondary design option exists
active design option id
physical count after filter
```

### 7.3 不要使用当前项目的手工转换循环作为默认路径

以下做法会再次偏离官方结果：

```csharp
foreach (var element in physicalObjects)
{
  var result = converter.ConvertToSpeckle(element);
  customRoot.elements.Add(result);
}
```

除非这是 `LevelCategoryCustom` 模式，否则不能这样构造默认 commit。

## 8. 官方 Send 适配器

### 8.1 适配器职责

新增 `OfficialSendAdapter`，它只负责把自动化任务转换成官方 Connector 能识别的输入：

```csharp
public sealed class OfficialSendAdapter
{
  private readonly ConnectorBindingsRevit _bindings;

  public OfficialSendAdapter(ConnectorBindingsRevit bindings)
  {
    _bindings = bindings;
  }

  public Task<OfficialSendResult> SendAsync(
    UploadJob job,
    IReadOnlyList<Element> physicalObjects,
    UploadCallbackReporter reporter)
  {
    // 1. 根据官方版本创建 StreamState
    // 2. 设置 stream/server/token/branch/message
    // 3. 设置全模型元素选择
    // 4. 设置 callback 进度桥接
    // 5. 调用官方 SendStream
    // 6. 从官方结果中读取 object/version/commit 信息
    throw new NotImplementedException();
  }
}
```

这里的 `NotImplementedException` 只是接口示意。实现时不能复制当前 `SpeckleSendService` 的内部上传代码。

### 8.2 `RevitConverterState` 和 converter context

如果固定官方版本的 `SendStream` 中已经有以下代码，必须让官方方法执行，而不是在适配器重复实现：

```csharp
using var ctx = RevitConverterState.Push();

var converter = (ISpeckleConverter)Activator.CreateInstance(Converter.GetType());
converter.SetContextDocument(CurrentDoc.Document);
converter.Report.ReportObjects.Clear();
```

尤其要确认 `SetContextDocument` 传入的是：

- `Document`；
- `RevitDocumentAggregateCache`；
- 还是官方版本要求的其它 context 对象。

不同版本这里可能不同。当前独立项目直接传 `Document`，不能作为新项目的通用实现。

### 8.3 Converter settings

官方 `SendStream` 会从 `StreamState` 或 UI 设置构造 converter settings。自动化任务必须：

- 使用官方默认 settings；
- 只覆盖官方明确支持的设置；
- 保留官方传入的所有 key；
- 不要只传一个空 `Dictionary`；
- 不要把 NavisWorks 的 `_Mode` 等非 Revit 设置带入。

如果需要增加“全模型”开关，增加到自动化任务配置，再映射到官方 selection state，而不是修改 converter 的内部行为。

### 8.4 官方 commit builder

官方 Revit Converter 可能要求：

```csharp
IRevitCommitObjectBuilder
```

或通过 exposer/adapter 取得 builder。新项目必须调用官方 builder：

```csharp
var officialRoot = converter.ConvertToSpeckle(currentDocument);
var officialBuilder = GetOfficialCommitObjectBuilder(converter);
officialBuilder.IncludeObject(conversionResult, nativeElement);
```

实际方法名以固定版本为准。禁止把 `LevelCategoryCommitBuilder` 替换到 `OfficialCompatible` 路径。

官方 builder 的意义包括：

- 保持官方 Collection 层级；
- 维护 Revit 对象关系；
- 处理 Host、Type、Level、Material、MEP 等关系；
- 维护 applicationId；
- 处理转换缓存；
- 保持官方对象序列化结构。

### 8.5 上传路径必须跟随官方版本

如果官方固定版本使用：

```text
Operations.Send
```

就调用官方的同一签名、同一 transport、同一 progress/error handler。

如果官方固定版本使用：

```text
SendPipeline / SendViaPackfile / ingestion
```

就必须完整使用官方 pipeline：

- 不将 pipeline 改回 `Operations.Send`；
- 不比较两种路径产生的 object ID；
- 不在 pipeline 后重复执行另一套 `Complete`；
- 使用官方 progress 和 cancellation；
- 使用官方资源释放方式。

当前独立项目中的 `Operations.Send + ServerTransport + CommitCreate` 只能作为旧版本兼容参考，不能覆盖官方实现。

### 8.6 token 和 server

自动化请求中的 token 只在任务内存中使用：

```csharp
var state = CreateOfficialStreamState(
  streamId: job.Request.StreamId,
  serverUrl: job.Request.ServerUrl,
  token: job.Request.Token,
  branchName: job.Request.BranchName,
  commitMessage: job.Request.CommitMessage
);
```

要求：

- 不把 token 写入日志；
- 不写入配置文件；
- 不放入 URL；
- 不放入异常文本；
- HTTP 请求完成后不保留原始 JSON；
- `serverUrl` 只允许 `http`/`https`，生产环境建议限制 host；
- 自动化模式下不使用当前用户 UI state 覆盖请求参数。

## 9. 自定义 Level → Category → Type 模式

该功能可以保留，但必须显式区分：

```text
SPECKLE_UPLOAD_COMMIT_MODE=official
SPECKLE_UPLOAD_COMMIT_MODE=level-category
```

默认值必须是：

```text
official
```

### 9.1 Official 模式

Official 模式：

- 使用官方根对象；
- 使用官方 builder；
- 使用官方关系；
- 使用官方发送 pipeline；
- 目标是与同版本官方 Connector 结果一致。

### 9.2 LevelCategory 模式

LevelCategory 模式才允许使用当前项目的：

- `LevelCategoryCommitBuilder`；
- `Level → Category → Type` Collection；
- 不把 Host 构件挂在 Host 下；
- 使用本地化 `Category.Name`；
- LevelId、`FAMILY_BASE_LEVEL_PARAM`、`SCHEDULE_LEVEL_PARAM` 回退逻辑。

该模式的最终 callback 必须带上：

```text
commit_mode=level-category
```

如果现有回调协议不能增加字段，则至少写入日志，并在服务端任务配置中记录模式。

### 9.3 不能隐式切换

不能因为某个官方元素转换失败，就偷偷从 Official 模式切换到 LevelCategory 模式。两种模式必须：

- 任务开始时确定；
- 日志明确记录；
- callback 可识别；
- 测试分别验收；
- 结果不可混用。

## 10. 进度和 callback

### 10.1 callback 字段

最终和过程 callback 继续使用 snake_case：

```json
{
  "request_id": "upload-001",
  "is_final": false,
  "success": null,
  "file_path": "C:\\models\\sample.rvt",
  "stream_id": "1183495a7b",
  "branch_name": "main",
  "commit_message": "automated upload",
  "object_id": null,
  "commit_id": null,
  "object_count": 0,
  "error": null,
  "progress": "解析",
  "progress_index": 35
}
```

最终成功：

```json
{
  "request_id": "upload-001",
  "is_final": true,
  "success": true,
  "file_path": "C:\\models\\sample.rvt",
  "stream_id": "1183495a7b",
  "branch_name": "main",
  "commit_message": "automated upload",
  "object_id": "<official-object-id>",
  "commit_id": "<official-commit-or-version-id>",
  "object_count": 939,
  "error": null,
  "progress": "完成",
  "progress_index": 100
}
```

### 10.2 固定进度阶段

沿用当前 speckle_sync 契约：

```text
接收 = 1
入队 = 5
执行 = 6
打开 = 9
准备 = 10
解析 = 10–50
上传 = 50–90
提交 = 91
完成 = 100
```

解析阶段：

```text
10 + floor(current / total * 40)
```

上传阶段：

```text
50 + floor(uploaded / estimatedTotal * 40)
```

进度要求：

- 首个对象上报；
- 每 500 个对象上报；
- 阶段结束上报；
- 默认 30 秒心跳；
- 百分比单调递增；
- 过程 callback 为 `is_final=false`；
- 过程 callback 不发送 `success=false`。

### 10.3 官方进度桥接

不要在官方 `ProgressViewModel` 外再统计一套互相矛盾的上传计数。采用进度桥接：

```csharp
public sealed class OfficialProgressBridge
{
  private readonly UploadCallbackReporter _reporter;

  public void OnOfficialProgress(OfficialProgressSnapshot snapshot)
  {
    _reporter.ReportOfficialProgress(
      phase: snapshot.Phase,
      current: snapshot.Current,
      total: snapshot.Total,
      uploaded: snapshot.Uploaded
    );
  }
}
```

如果官方 pipeline 只有 ingestion 阶段进度，则：

- 转换阶段使用 Physical Objects 遍历进度；
- 官方上传阶段使用官方提供的 `IProgress` 或 progress callback；
- 不用旧版 `Operations.Send` 的字典回调去猜测 pipeline 进度。

### 10.4 最终结果

最终任务完成的唯一判定：

```text
is_final == true && progress == "完成"
```

callback HTTP 响应仍要求解析 lwhale 格式并判断：

```text
ret == 0
```

最终 callback 失败时：

- 记录 callback 错误；
- 不再发送第二条“成功最终结果”；
- 关闭文档动作仍在安全 UI 调度点执行；
- 本地日志保留官方 object/version 信息；
- 是否把 callback 失败改成任务失败，必须和 speckle_sync 约定一致。

## 11. 弹窗自动化

### 11.1 只在打开阶段启用

弹窗处理必须严格限定为：

```text
ArmForOpen
  → OpenAndActivateDocument
  → CloseOtherDocuments
  → CompleteOpenPhase
```

进入官方 Converter 和 Send 前必须关闭自动弹窗处理，避免把 Speckle/官方 UI 的提示误判成 Revit 打开警告。

默认值继续保持：

```text
SPECKLE_UPLOAD_ENABLE_DIALOG_SUPPRESSION=false
```

默认由外部 AHK 或人工处理，只有明确设置为 `1` 或 `true` 才启用内置逻辑。

### 11.2 规则优先级

继续沿用：

1. `never`；
2. `Dialog_Revit_DocWarnDialog` 专用处理；
3. `AUTO_DISMISS_ALL`；
4. JSON `rules`；
5. `unmatchedFallback`；
6. 只记录日志，不自动点击。

必须保留的安全规则：

- “取消升级”永远不自动点击；
- “正在升级”永远不自动点击；
- 连接错误才尝试 `commandLink1/commandLink2`；
- `Dialog_Revit_DocWarnDialog` 的确定结果码使用该版本验证过的值；
- 不用普通 TaskDialog 的 `1/6` 结果码替代 DocWarn 的结果码；
- Win32 点击只针对前台 Revit 模态框。

### 11.3 官方入口事件共存

如果官方 Connector 已注册 `DialogBoxShowing`：

- 不要重复注册两个处理器；
- 在官方已有处理链中增加 UploadBridge handler；
- 只在自动打开任务的时间窗口内处理；
- 非自动任务不改变官方弹窗行为；
- 处理异常不能冒泡破坏官方入口。

## 12. 配置

建议配置项：

```text
SPECKLE_UPLOAD_HTTP_PORT
SPECKLE_UPLOAD_CALLBACK_URL
SPECKLE_UPLOAD_CALLBACK_TIMEOUT_SECONDS=1200
SPECKLE_UPLOAD_PROGRESS_HEARTBEAT_SECONDS=30
SPECKLE_UPLOAD_ENABLE_DIALOG_SUPPRESSION=false
SPECKLE_UPLOAD_OPEN_DIALOG_SUPPRESS_SECONDS=120
SPECKLE_UPLOAD_AUTO_DISMISS_ALL_OPEN_DIALOGS=false
SPECKLE_UPLOAD_COMMIT_MODE=official
```

增加配置时遵守：

- 插件启动时记录非敏感配置；
- 不记录 token；
- 配置读取失败使用安全默认值；
- `commit_mode` 非法时拒绝任务，不自动降级；
- 端口被占用时在 Revit 日志和插件日志中明确记录；
- callback URL 和 server URL 做格式校验。

## 13. 日志要求

至少保留以下阶段：

```text
App
Http
UploadQueue
Doc
OfficialSend
Callback
Dialog
```

每个任务都记录：

- `requestId`；
- 文件路径；
- streamId；
- Revit 版本；
- 官方基线 commit；
- commit mode；
- 打开耗时；
- Physical Objects 数量；
- Design Option 过滤前后数量；
- 官方转换数量；
- 官方上传 object/version/commit 标识；
- 最终 callback 结果；
- 关闭文档结果。

禁止记录：

- token；
- Authorization header；
- 完整带敏感参数的 URL；
- 完整上传请求 JSON。

## 14. 安装和发布

### 14.1 官方插件安装方式优先

新项目应优先沿用官方 Connector 的安装和依赖布局：

- 官方 `.addin`；
- 官方插件目录；
- 官方 Speckle Kit/依赖目录；
- 官方 Revit API 引用；
- 官方版本矩阵。

UploadBridge 应作为官方 Connector 的程序集或其官方允许的附属程序集部署，不能在另一个目录放第二套 Speckle Core。

### 14.2 `.addin` 规则

如果官方入口类为 `ConnectorRevit`，`.addin` 的 `FullClassName` 必须指向官方入口，而不是新增的第二个 `SpeckleUploadApp`。

如果通过 partial class 或官方入口扩展启动 UploadBridge，`.addin` 不需要新增入口。

只有在官方明确支持多 application entry 的情况下，才考虑单独 `.addin`，并且必须确认不会加载重复 Connector。

### 14.3 构建矩阵

每个 Revit 版本单独构建和验证：

```text
Release + Revit 2022
Release + Revit 2024
Release + 其它官方支持版本
```

构建包必须包含：

- 官方 Connector 需要的文件；
- UploadBridge 程序集；
- `SpeckleUpload.open-dialog-rules.json`；
- 版本信息；
- 固定官方 baseline 信息；
- 安装说明。

不能只把当前独立项目的 `bin/2024` 复制进官方安装目录。

## 15. 测试和验收

### 15.1 版本一致性测试

在干净 Windows 虚拟机中验证：

- 官方 Connector 正常加载；
- Revit 只加载一套 Speckle Core；
- 启动日志显示正确官方 baseline；
- `/health` 返回成功；
- 官方 UI 手动上传仍然正常；
- 自动 HTTP 上传和手动官方上传都能完成。

### 15.2 内容一致性测试

使用同一个：

- Revit 版本；
- RVT 文件；
- stream；
- branch；
- token；
- converter settings；
- selection 集合；
- 官方 baseline commit。

分别执行：

1. 官方 UI 上传；
2. HTTP 自动化上传 `OfficialCompatible`。

比较：

- 根对象类型；
- Collection 层级；
- 对象数量；
- 每个 applicationId；
- 关键对象属性；
- relation/host/type 信息；
- object/version/commit 结果；
- Speckle 页面显示树。

不能只比较 `object_count` 或页面上“看起来差不多”。

### 15.3 功能测试

必须覆盖：

- 空文件路径；
- 不存在的 RVT；
- 缺少 streamId；
- 缺少 token；
- requestId 缺失时自动生成；
- 中文 commit message；
- 同时提交第二个任务；
- Revit 正在弹窗时提交任务；
- 打开旧版本 RVT；
- 打开他人 local RVT；
- 有多个打开文档；
- 当前活动文档就是目标文档；
- 有链接文档；
- 有 Design Option；
- 没有 Physical Objects；
- 单个元素转换失败；
- 官方上传失败；
- commit/version 创建失败；
- callback 服务不可用；
- callback 返回非 JSON；
- callback 返回 `ret != 0`；
- 最终 callback 后关闭文档；
- 关闭文档失败。

### 15.4 回调序列测试

成功任务应看到：

```text
接收(1)
→ 入队(5)
→ 执行(6)
→ 打开(9)
→ 准备(10)
→ 解析(10–50)
→ 上传(50–90)
→ 提交(91)
→ 完成(100, is_final=true)
```

服务端只在以下条件同时满足时判定结束：

```text
is_final=true && progress="完成"
```

### 15.5 自定义模式测试

`LevelCategoryCustom` 单独测试：

- LevelId 有效；
- LevelId 无效但参数有效；
- 没有 Level；
- Category 为空；
- TypeId 无效；
- Host 关系存在；
- 同一 Level/Category/Type 重复对象；
- 结果与 OfficialCompatible 明确不同且日志有记录。

## 16. 推荐实现顺序

按以下顺序实施，每一步都先在固定官方版本上验证：

1. Fork 官方 Connector，锁定 commit 和依赖；
2. 不改上传逻辑，确认官方插件能编译、加载、手动上传；
3. 增加 `PluginLog`，只记录启动和官方版本；
4. 增加 `/health`，确认不影响官方 UI；
5. 增加 `/upload` 参数校验和单槽位队列；
6. 接入官方 UI 调度器，先只记录 Execute；
7. 接入目标 RVT 打开和多文档关闭；
8. 接入 Physical Objects 和 Design Option 过滤；
9. 将选择结果映射到官方 `StreamState`；
10. 调用官方 `SendStream`，先不修改官方 commit builder；
11. 接入过程 callback；
12. 接入最终同步 callback；
13. 接入最终 callback 后的文档关闭；
14. 接入弹窗规则，并确认只作用于打开阶段；
15. 增加 `LevelCategoryCustom` 可选模式；
16. 完成两种模式的回归和内容对比；
17. 打包、安装和干净机器验证。

不要先重写 `SpeckleSendService`。新方案的第一条成功标准是：HTTP 自动任务能够调用官方已有的 SendStream，并且手动官方上传不被破坏。

## 17. 明确禁止的实现

1. 不要在官方 Connector 旁边再引用本项目旧版 `Speckle.Core`；
2. 不要同时加载两套同名 Speckle DLL；
3. 不要默认 `new ConverterRevit()` 代替官方 converter 创建方式；
4. 不要绕过官方 `IRevitCommitObjectBuilder`；
5. 不要默认使用 `LevelCategoryCommitBuilder`；
6. 不要把 `Operations.Send` 作为所有官方版本的固定实现；
7. 不要在 HTTP 线程调用 Revit API；
8. 不要把 token 写到日志、脚本或配置；
9. 不要在最终 callback 前关闭 RVT；
10. 不要在非打开阶段自动点击所有 Revit 弹窗；
11. 不要用 object_count 相同证明结果一致；
12. 不要把 OfficialCompatible 和 LevelCategoryCustom 的结果混在同一个模式中；
13. 不要迁移 `upload-rvt.sh` 中的硬编码 token；
14. 不要在没有官方版本和 commit 记录的情况下发布安装包。

## 18. 现有仓库文件到新项目的对应关系

```text
当前文件                                      新项目处理
──────────────────────────────────────────    ──────────────────────────────
SpeckleUploadApp.cs                            合并到官方入口生命周期
Http/HttpUploadServer.cs                       保留协议，改接官方队列
Models/*                                       保留字段和 JSON 命名
CallbackService.cs                             基本保留，补 URL 校验
UploadCallbackReporter.cs                      保留进度契约，桥接官方进度
PluginSettings.cs                              保留环境变量，增加 commit mode
DocumentService.cs                             保留打开/关闭策略，适配官方调度器
RevitOpenDialogSuppression.cs                  合并官方 DialogBoxShowing 处理链
Win32DialogClicker.cs                          仅用于打开阶段
OpenDialogRulesLoader.cs                       基本保留
SpeckleSendService.cs                          改为 OfficialSendAdapter
LevelCategoryCommitBuilder.cs                  仅用于显式 custom 模式
UploadEventHandler.cs                          改为官方调度器适配层
SPECKLE_SYNC.md                                作为 HTTP/callback 协议基准
API.md                                         作为外部文件转换服务说明，不属于插件上传链路
upload-rvt.sh                                  不直接迁移，先删除硬编码 token
```

## 19. 完成定义

新项目只有在以下条件全部满足后，才能称为“官方插件改造完成”：

- 官方 Connector 的手动上传行为未被破坏；
- 自动 HTTP 上传使用官方 SendStream；
- 官方 converter、context、settings、builder 和上传路径均与 baseline 一致；
- `OfficialCompatible` 与官方 UI 上传经过同文件、同选择集的内容对比；
- `/upload`、`/health`、callback 和进度协议与 speckle_sync 兼容；
- 自动打开、关闭文档和弹窗逻辑通过真实 Revit 测试；
- 最终 callback 先于关闭文档；
- 第二个任务在第一个任务执行期间被拒绝；
- 失败场景都有最终 callback 或明确的本地失败日志；
- 安装包不会复制或覆盖错误版本的 Speckle DLL；
- `LevelCategoryCustom` 被明确标记为非官方兼容模式；
- 发布包记录官方源码 commit、Revit 版本和 Speckle 依赖版本。

这套边界可以同时保留本项目的自动化能力和官方 Connector 的上传语义，但前提是默认路径始终调用官方上传链路，而不是在外围重新实现一套“看起来相同”的转换流程。
