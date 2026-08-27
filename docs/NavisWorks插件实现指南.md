# NavisWorks 插件实现指南（对照 SpeckleUpload / Revit）

本文档供 **新仓库** 按步骤实现 NavisWorks 插件。目标与当前 Revit 插件 `SpeckleUpload` 相同：启动 HTTP 服务 → 打开本地 `.nwd` → Speckle 解析/上传 → 进度回调。**不要边写边发明协议**，HTTP 契约、进度字段、回调格式必须与 Revit 版一致，speckle_sync 才能复用。

参考仓库：`SpeckleUpload`（Revit 2022/2024）。下文「对照文件」均指该仓库路径。

---

## 0. 先读：和 Revit 版的硬差异

按步骤写代码前，先接受这几条，后面不会踩坑。

| 点 | Revit 插件 | NavisWorks 插件（本指南） |
|----|------------|---------------------------|
| 自动加载入口 | `IExternalApplication` + `.addin` | `EventWatcherPlugin` + DLL 目录名与程序集同名 |
| 宿主线程调度 | `ExternalEvent` + `Idling` | `Application.Idle` 队列（没有 ExternalEvent） |
| 文档模型 | 多文档；不能先关活动文档 | **单文档**：`OpenFile` 会直接替换当前内容 |
| 完成后关文件 | 最终回调后再 Idling 关 RVT | **完成后不关**；下次 `OpenFile` 新文件时自动关掉上一份 |
| 模型对象 | `Element` / Physical Objects | `ModelItem`（有几何、未隐藏） |
| Speckle 转换器 | `ConverterRevit` | `ConverterNavisworks`，**必须**设 `_Mode=objects` |
| 默认 HTTP 端口 | 2022=`6687`，2024=`6688` | 建议 **`6690`**（避开 Revit 与 callback `6689`） |
| 弹窗抑制 | 跨版本升级弹窗较多 | 打开 NWD 通常无升级框，**第一版不做** |
| 安装位置 | `%APPDATA%\Autodesk\Revit\Addins\{年}\` | `{Navisworks}\Plugins\{与 DLL 同名的文件夹}\` |

**完成后不关文件** 在 NavisWorks 上几乎是默认行为：`Document.OpenFile(path)` 的文档说明就是 “replacing current contents”。因此：

- 任务成功/失败后 **不要** 调 `Document.Clear()`。
- 下一单若路径不同：直接 `OpenFile`，上一份会被替换。
- 下一单若路径相同（已是当前文档）：跳过打开，直接解析上传。

---

## 1. 建议的仓库与目录

建议项目名：`SpeckleUploadNavis`（避免与 Revit 版 DLL 重名）。

```
SpeckleUploadNavis/
├── SpeckleUploadNavis.sln
├── SpeckleUploadNavis.csproj
├── GlobalUsings.cs
├── PluginSettings.cs
├── SpeckleUploadNavisApp.cs          # EventWatcherPlugin，启动 HTTP
├── Models/
│   ├── UploadRequest.cs              # 从 Revit 版原样拷贝
│   ├── UploadCallbackPayload.cs      # 原样拷贝
│   └── LwhaleResponse.cs             # 原样拷贝
├── Http/
│   ├── HttpUploadServer.cs           # 几乎原样，handler 类型改名
│   └── LwhaleJsonResponse.cs         # 原样拷贝
├── Services/
│   ├── PluginLog.cs                  # 原样拷贝，日志文件名改一下
│   ├── CallbackService.cs            # 原样拷贝
│   ├── UploadCallbackReporter.cs     # 原样拷贝
│   ├── UploadWorkItem.cs             # 原样拷贝
│   ├── UploadEnqueueResult.cs        # 原样拷贝（可去掉 Denied 的 Revit 文案）
│   ├── UploadIdleHandler.cs          # 对应 UploadEventHandler，用 Idle 队列
│   ├── DocumentService.cs            # 重写：OpenFile / 收集 ModelItem
│   └── SpeckleSendService.cs         # 重写：ConverterNavisworks
├── Install-SpeckleUploadNavis.ps1
├── Install-SpeckleUploadNavis.cmd
└── docs/                             # 把本文件拷进来
```

**可原样拷贝（只改 namespace / 日志文件名）：**

- `Models/*`
- `Http/LwhaleJsonResponse.cs`
- `Http/HttpUploadServer.cs`（构造函数改接 `UploadIdleHandler`）
- `Services/CallbackService.cs`
- `Services/UploadCallbackReporter.cs`
- `Services/UploadWorkItem.cs`
- `Services/PluginLog.cs`
- `GlobalUsings.cs`

**必须重写：**

- 入口、csproj、Idle 调度、DocumentService、SpeckleSendService、PluginSettings 默认端口。

---

## 2. 步骤 1：建解决方案与 csproj

### 2.1 目标框架

NavisWorks Manage/Simulate **2024** 仍是 **.NET Framework 4.8**（与当前 Revit 插件一致）。第一版先只打一个版本。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <NavisworksVersion Condition="'$(NavisworksVersion)' == ''">2024</NavisworksVersion>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <RootNamespace>SpeckleUploadNavis</RootNamespace>
    <AssemblyName>SpeckleUploadNavis</AssemblyName>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <UseWindowsForms>true</UseWindowsForms>
    <NavisworksInstallDir Condition="'$(NavisworksInstallDir)' == ''">C:\Program Files\Autodesk\Navisworks Manage $(NavisworksVersion)\</NavisworksInstallDir>
    <BaseOutputPath>bin\$(NavisworksVersion)\</BaseOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <PackageReference Include="Speckle.Objects.Converter.Navisworks2024" Version="2.23.2" />
  </ItemGroup>

  <ItemGroup>
    <!-- 运行时由 NavisWorks 提供，禁止 Copy Local -->
    <Reference Include="Autodesk.Navisworks.Api">
      <HintPath>$(NavisworksInstallDir)Autodesk.Navisworks.Api.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="Autodesk.Navisworks.ComApi">
      <HintPath>$(NavisworksInstallDir)Autodesk.Navisworks.ComApi.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="Autodesk.Navisworks.Interop.ComApi">
      <HintPath>$(NavisworksInstallDir)Autodesk.Navisworks.Interop.ComApi.dll</HintPath>
      <Private>False</Private>
    </Reference>
  </ItemGroup>
</Project>
```

说明：

- Converter 版本与 Revit 版对齐 **2.23.2**。2025 则改包名 `Speckle.Objects.Converter.Navisworks2025`。
- 本机必须安装 **Navisworks Manage 或 Simulate**（Freedom 无 API）。
- `Private=False`：不要把 NavisWorks 官方 DLL 打进插件目录。

编译（在 Windows 上）：

```bash
dotnet build SpeckleUploadNavis.sln -c Release
```

---

## 3. 步骤 2：配置项（对照 `PluginSettings.cs`）

拷贝 `PluginSettings.cs`，只改默认端口。环境变量名建议 **复用** Revit 版，方便同一台机器用同一套 speckle_sync：

| 环境变量 | 默认 | 说明 |
|----------|------|------|
| `SPECKLE_UPLOAD_HTTP_PORT` | **6690** | 插件监听端口 |
| `SPECKLE_UPLOAD_CALLBACK_URL` | `http://127.0.0.1:6689/api/callback` | 与 Revit 相同 |
| `SPECKLE_UPLOAD_CALLBACK_TIMEOUT_SECONDS` | 1200 | 最终回调超时 |
| `SPECKLE_UPLOAD_PROGRESS_HEARTBEAT_SECONDS` | 30 | 解析/上传心跳 |

不要复用 6687/6688，否则和 Revit 插件抢端口。

```csharp
public const int DefaultHttpPort = 6690;
public const string DefaultCallbackUrl = "http://127.0.0.1:6689/api/callback";
```

弹窗相关环境变量第一版可删。

---

## 4. 步骤 3：日志

拷贝 `Services/PluginLog.cs`，把日志文件名改成 `SpeckleUploadNavis.log`（仍写在程序集目录）。全程用 `PluginLog.Step("Phase", "...")`，阶段名与 Revit 版对齐：`App` / `Http` / `UploadHandler` / `Doc` / `Speckle` / `Callback`。

---

## 5. 步骤 4：插件入口（自动启动 HTTP）

对照文件：`SpeckleUploadApp.cs`。

NavisWorks **没有** `.addin` + `IExternalApplication`。要用 **`EventWatcherPlugin`**：进程启动就会加载，且一直活着。

```csharp
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using SpeckleUploadNavis.Http;
using SpeckleUploadNavis.Services;

namespace SpeckleUploadNavis;

[Plugin("SpeckleUploadNavis", "SPKU", DisplayName = "Speckle Upload Navisworks")]
public class SpeckleUploadNavisApp : EventWatcherPlugin
{
  private HttpUploadServer? _server;
  private UploadIdleHandler? _handler;

  public override void OnLoaded()
  {
    PluginLog.EnsureInitialized();
    PluginLog.Step("App", "OnLoaded: begin");

    _handler = new UploadIdleHandler();
    Application.Idle += OnIdle;

    _server = new HttpUploadServer(_handler);
    _server.Start();

    PluginLog.Step("App", $"OnLoaded: HTTP started port={PluginSettings.HttpPort} log={PluginLog.LogFilePath}");
  }

  public override void OnUnloading()
  {
    PluginLog.Step("App", "OnUnloading: begin");
    Application.Idle -= OnIdle;
    _server?.Dispose();
    _server = null;
    PluginLog.Step("App", "OnUnloading: end");
  }

  private void OnIdle(object? sender, EventArgs e)
  {
    _handler?.OnIdle();
  }
}
```

注意：

- `[Plugin("SpeckleUploadNavis", "SPKU")]` 里第一个参数必须和 **程序集名 / 安装文件夹名** 一致。
- `SPKU` 是 4 位 DeveloperId，可改，但全仓库保持同一个。
- HTTP 在 `OnLoaded` 里启动，等价于 Revit 的 `ApplicationInitialized`。

---

## 6. 步骤 5：HTTP 服务（契约必须一致）

对照文件：`Http/HttpUploadServer.cs`、`SPECKLE_SYNC.md`。

直接拷贝 `HttpUploadServer`，把 `_handler` 类型换成 `UploadIdleHandler`。路由与校验一字不改：

| 方法 | 路径 | 行为 |
|------|------|------|
| GET | `/` 或 `/health` | `{ "status": "ok", "port": 6690 }` |
| POST | `/upload` | 校验后入队，立即 `ret:0` |

`POST /upload` 请求体（camelCase，与 speckle_sync 发出的一致）：

```json
{
  "filePath": "D:\\testnwd\\demo.nwd",
  "streamId": "1183495a7b",
  "serverUrl": "https://app.speckle.systems",
  "token": "<user-token>",
  "branchName": "main",
  "commitMessage": "nwd upload",
  "requestId": "curl-test-001",
  "callbackUrl": "http://127.0.0.1:6689/api/callback"
}
```

必填：`filePath`、`streamId`、`token`。缺则 `ret:1002`。

入队结果：

- 成功：先异步报 `接收=1`，入队后再报 `入队=5`，HTTP 返回 `{ "ret": 0, "msg": null }`。
- 忙：`ret:500`，`error: Another upload is in progress.`
- JSON 错：`ret:1002`。

**不要把最终结果放进 `/upload` 同步响应。**

`filePath` 由调用方保证已下载到本地（本插件不负责下载）。

---

## 7. 步骤 6：Idle 队列（对应 ExternalEvent）

对照文件：`Services/UploadEventHandler.cs`。

NavisWorks API **只能在 UI 线程**调用。HTTP 在后台线程，必须入队，等 `Application.Idle` 再执行。

```csharp
public sealed class UploadIdleHandler
{
  private readonly object _sync = new();
  private UploadWorkItem? _pending;
  private volatile bool _busy;

  public UploadEnqueueResult TryEnqueue(UploadWorkItem item)
  {
    lock (_sync)
    {
      if (_pending != null || _busy)
      {
        return new UploadEnqueueResult(UploadEnqueueStatus.Busy);
      }
      _pending = item;
    }
    return new UploadEnqueueResult(UploadEnqueueStatus.Accepted);
  }

  public void OnIdle()
  {
    UploadWorkItem? item;
    lock (_sync)
    {
      if (_busy || _pending == null)
      {
        return;
      }
      item = _pending;
      _pending = null;
      _busy = true;
    }

    try
    {
      Execute(item);
    }
    finally
    {
      lock (_sync)
      {
        _busy = false;
      }
    }
  }

  private void Execute(UploadWorkItem item)
  {
    var request = item.Request;
    var reporter = new UploadCallbackReporter(request);
    reporter.ReportExecute(); // progress=执行, index=6

    UploadCallbackPayload payload;
    try
    {
      var document = DocumentService.PrepareDocumentForUpload(request.FilePath);
      reporter.ReportOpened(); // 打开=9
      payload = SpeckleSendService.SendModelItems(document, request, reporter);
    }
    catch (Exception ex)
    {
      payload = new UploadCallbackPayload
      {
        RequestId = request.RequestId,
        Success = false,
        FilePath = request.FilePath,
        StreamId = request.StreamId,
        BranchName = string.IsNullOrWhiteSpace(request.BranchName) ? "main" : request.BranchName,
        CommitMessage = request.CommitMessage,
        Error = ex.Message,
      };
    }

    try
    {
      reporter.ApplyFinalProgress(payload);
      CallbackService.SendAsync(payload, request.CallbackUrl).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
      PluginLog.Step("UploadHandler", $"callback failed {ex.Message}");
    }

    // 完成后不关文件。下一单 OpenFile 会替换当前文档。
  }
}
```

和 Revit 版的差别：

- 没有 `ExternalEvent.Raise` / `Denied` / `Pending`。Idle 里直接跑。
- **没有** `_deferredCloseActive`。不要调度关闭。
- `Execute` 必须同步跑完（和 Revit 一样）：转换用 NavisWorks API，不能 `await` 切到线程池后再碰 `ModelItem`。`Operations.Send` 内部可以 `GetAwaiter().GetResult()`。

---

## 8. 步骤 7：打开 NWD

对照文件：`Services/DocumentService.cs` 的 `PrepareDocumentForUpload` / `OpenDocument`。

NavisWorks 是单文档，逻辑比 Revit 短：

```csharp
public static Document PrepareDocumentForUpload(string filePath)
{
  if (!File.Exists(filePath))
  {
    throw new FileNotFoundException($"Navisworks file not found: {filePath}");
  }

  var doc = Application.ActiveDocument
    ?? throw new InvalidOperationException("Application.ActiveDocument is null.");

  var current = NormalizePath(doc.FileName);
  var target = NormalizePath(filePath);

  if (!string.IsNullOrWhiteSpace(current)
      && string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
  {
    PluginLog.Step("Doc", "PrepareDocumentForUpload: target already open, skip OpenFile");
    return doc;
  }

  // 有上一份文件时，OpenFile 会替换并关闭它 —— 满足「下次打开时再关上一份」
  PluginLog.Step("Doc", $"PrepareDocumentForUpload: OpenFile \"{filePath}\"");
  if (!doc.TryOpenFile(filePath))
  {
    throw new InvalidOperationException($"Failed to open document: {filePath}");
  }

  return Application.ActiveDocument
    ?? throw new InvalidOperationException($"ActiveDocument is null after open: {filePath}");
}
```

要点：

- 用 `TryOpenFile`，失败返回 false；不要用 Automation API（那是进程外启动 NavisWorks 的）。
- 路径比较忽略大小写，先 `Path.GetFullPath`。
- `doc.FileName` 可能为空（空文档），此时必须打开。
- 第一版只保证 `.nwd`。`.nwc` 可同样打开；`.nwf` 依赖外部引用，先不承诺。
- **不要**在成功后 `Clear()`。

---

## 9. 步骤 8：收集要转换的对象

对照文件：`DocumentService.GetPhysicalObjects`。

Revit 用 `FilteredElementCollector` + `IsPhysicalElement()`。NavisWorks 对应 **可见几何 `ModelItem`**：

```csharp
public static List<ModelItem> GetGeometryItems(Document document)
{
  var root = document.Models.RootItem;
  if (root == null)
  {
    throw new InvalidOperationException("Document has no RootItem (empty model).");
  }

  var items = root.DescendantsAndSelf
    .Where(item => item.HasGeometry && !item.IsHidden)
    .ToList();

  PluginLog.Step("Doc", $"GetGeometryItems: count={items.Count}");
  return items;
}
```

规则（与官方 ConnectorNavisworks 一致）：

- 只要 `HasGeometry`。
- 跳过 `IsHidden`。
- 第一版 **拍平**，不保留选择树 Collection。后面若要层次，再对照 `Element.BuildNestedObjectHierarchyInParallel`。

空列表则抛错：`No geometry items found in the model.`

---

## 10. 步骤 9：Speckle 转换 + 上传

对照文件：`Services/SpeckleSendService.cs`。  
官方实现：`speckle-sharp` 的 `ConverterNavisworks` 与 `ConnectorNavisworksBindings.Send.cs`。

### 10.1 必须先设 Mode

`ConverterNavisworks.ConvertToSpeckle` 默认 `_Mode` 为空会直接 `return null`。发送几何前：

```csharp
converter.SetContextDocument(document);
converter.SetConverterSettings(new Dictionary<string, string> { { "_Mode", "objects" } });
```

### 10.2 转换循环（对齐 Revit 版结构）

```csharp
public static UploadCallbackPayload SendModelItems(
  Document document,
  UploadRequest request,
  UploadCallbackReporter reporter)
{
  var converter = new ConverterNavisworks();
  converter.SetContextDocument(document);
  converter.SetConverterSettings(new Dictionary<string, string> { { "_Mode", "objects" } });
  converter.Report.ReportObjects.Clear();

  var items = DocumentService.GetGeometryItems(document);
  if (items.Count == 0)
  {
    throw new InvalidOperationException("No geometry items found in the model.");
  }

  reporter.ReportSpeckleStart(); // 准备=10
  reporter.BeginConvert(items.Count);

  var commitObject = new Collection { collectionType = "Navisworks Model" };
  var convertedCount = 0;
  var index = 0;

  foreach (var item in items)
  {
    index++;
    reporter.ReportConvert(index);

    if (!converter.CanConvertToSpeckle(item))
    {
      continue;
    }

    try
    {
      var conversionResult = converter.ConvertToSpeckle(item);
      if (conversionResult == null)
      {
        continue;
      }

      if (string.IsNullOrWhiteSpace(conversionResult.applicationId))
      {
        conversionResult.applicationId = item.InstanceGuid.ToString();
      }

      commitObject.elements.Add(conversionResult);
      convertedCount++;
    }
    catch (Exception ex)
    {
      // 单件失败不中断整次任务（与 Revit 版一致）
      PluginLog.Step("Speckle", $"Convert failed: {ex.Message}");
    }
  }

  reporter.ReportConvertComplete(); // 解析=50

  if (convertedCount == 0)
  {
    throw new InvalidOperationException("Zero geometry items converted successfully.");
  }

  // 以下 Operations.Send + CommitCreate 与 Revit 版相同
  ...
}
```

`applicationId`：NavisWorks 没有 Revit `UniqueId`。优先 `InstanceGuid`；若全零，可用选择树路径字符串（官方 Connector 用 index path）。

### 10.3 Send + CommitCreate（与 Revit 相同）

这段几乎可从 `SpeckleSendService` 原样搬，只改 `sourceApplication`：

```csharp
var account = new Account { token = request.Token };
account.serverInfo = new ServerInfo { url = request.ServerUrl.TrimEnd('/') };

var client = new Client(account);
using var serverTransport = new ServerTransport(account, request.StreamId);

reporter.BeginUpload(convertedCount);
reporter.ReportUploadStart(); // 上传=50

string objectId = Operations.Send(
  @object: commitObject,
  cancellationToken: CancellationToken.None,
  transports: new List<ITransport> { serverTransport },
  onProgressAction: dict =>
  {
    var uploaded = dict.Sum(p => p.Value);
    reporter.ReportUpload(uploaded);
  },
  onErrorAction: null,
  disposeTransports: false
).GetAwaiter().GetResult();

reporter.FinishUpload(/* last uploaded */);
reporter.ReportUploadComplete(); // 提交=91

var commitInput = new CommitCreateInput
{
  streamId = request.StreamId,
  objectId = objectId,
  branchName = string.IsNullOrWhiteSpace(request.BranchName) ? "main" : request.BranchName,
  message = request.CommitMessage ?? $"Sent {convertedCount} items via SpeckleUploadNavis.",
  sourceApplication = HostApplications.Navisworks.Name, // 不要用 ConverterRevit.RevitAppName
};

#pragma warning disable CS0618
var commitId = client.CommitCreate(commitInput).GetAwaiter().GetResult();
#pragma warning restore CS0618
```

`ServerInfo` 命名空间：`Speckle.Core.Api.GraphQL.Models`（Revit 版已踩过坑）。

`CommitCreate` 失败但 `objectId` 已有时：回调 `success=false`，仍带上 `objectId`（对照 Revit 版）。

**整段 Send 必须在 Idle/`Execute` 同步上下文里 `GetAwaiter().GetResult()`**，不要把转换循环放到 `Task.Run`。

---

## 11. 步骤 10：进度与最终回调（不要改协议）

对照：`SPECKLE_SYNC.md`、`docs/进度.md`、`UploadCallbackReporter.cs`、`CallbackService.cs`。

**三个文件原样拷贝即可。** 进度表与 Revit 完全相同，speckle_sync 不用改：

| 时机 | `progress` | `progress_index` |
|------|------------|------------------|
| 收到 POST /upload | `接收` | 1 |
| /upload 返回 ret=0 | `入队` | 5 |
| Idle Execute 开始 | `执行` | 6 |
| NWD 打开完成 | `打开` | 9 |
| 开始 Speckle 转换 | `准备` | 10 |
| 遍历 ModelItem | `解析` | `10 + ⌊当前/总数×40⌋`，上限 50 |
| `Operations.Send` | `上传` | `50 + ⌊已上传/估算总数×40⌋`，上限 90 |
| Send 完成 | `提交` | 91 |
| 最终回调 `is_final=true` | `完成` | 100 |

解析/上传：第 1 个、每 500 个、阶段结束上报；另加 **30 秒心跳**。百分比单调递增。

过程回调：`CallbackService.SendFireAndForget`（失败不中断）。  
最终回调：`SendAsync` **同步等待**，`is_final=true` 且 `progress=完成`。

最终成功体（snake_case）：

```json
{
  "request_id": "curl-test-001",
  "is_final": true,
  "success": true,
  "file_path": "D:\\testnwd\\demo.nwd",
  "stream_id": "1183495a7b",
  "branch_name": "main",
  "commit_message": "nwd upload",
  "object_id": "...",
  "commit_id": "...",
  "object_count": 1234,
  "progress": "完成",
  "progress_index": 100,
  "error": null
}
```

判定任务结束（speckle_sync 侧已有逻辑，插件必须满足）：

```
is_final == true && progress == "完成"
```

---

## 12. 步骤 11：安装与加载

NavisWorks **不认** Revit 那种 `.addin`。用「文件夹名 = DLL 名」侧载。

### 12.1 安装目录

优先用户目录（无需管理员）：

```
%APPDATA%\Autodesk\Navisworks Manage 2024\Plugins\SpeckleUploadNavis\
```

若该路径不生效，改用安装目录（需管理员）：

```
C:\Program Files\Autodesk\Navisworks Manage 2024\Plugins\SpeckleUploadNavis\
```

Simulate 把路径里的 `Manage` 换成 `Simulate`。

**文件夹名必须是 `SpeckleUploadNavis`**，与 `AssemblyName`、`[Plugin("SpeckleUploadNavis", ...)]` 一致。

### 12.2 放入内容

Release 输出目录里 **除** `Autodesk.Navisworks.*.dll` 外的全部文件（含 Speckle、Newtonsoft 等依赖）。

Windows 解锁（与 Revit 版相同）：

```powershell
$dir = "$env:APPDATA\Autodesk\Navisworks Manage 2024\Plugins\SpeckleUploadNavis"
Get-ChildItem -Path $dir -Recurse -File | Unblock-File
```

完全退出 NavisWorks 后重新打开。

### 12.3 安装脚本（对照 `Install-SpeckleUpload.ps1`）

脚本职责：

1. 以脚本所在目录为源。
2. 清空并重建 `Plugins\SpeckleUploadNavis`。
3. 拷贝全部文件。
4. `Unblock-File`。
5. `pause`。

### 12.4 探活

```bash
curl -sS http://localhost:6690/health
# {"status":"ok","port":6690}
```

无响应：看插件目录名、日志是否生成、NavisWorks 是否加载失败。

---

## 13. 步骤 12：联调顺序

1. 启动 NavisWorks Manage 2024（空文档即可）。
2. `GET http://127.0.0.1:6690/health`。
3. 确认本地已有 `.nwd`。
4. `POST /upload`（body 见第 6 节）。
5. 立刻应返回 `ret:0`；NavisWorks 开始打开模型。
6. speckle_sync（或自己 mock）在 `6689/api/callback` 收到进度：`接收→入队→执行→打开→准备→解析→上传→提交→完成`。
7. 最终一条 `is_final=true`。**模型应仍开着**。
8. 再 POST 另一个 `.nwd`：应关掉上一份并打开新文件（`OpenFile` 替换）。
9. 再 POST 同一路径：应跳过打开，直接转换。

本地 mock 回调（无 speckle_sync 时）：

```bash
# 另开终端：python -m http.server 不够，需要能 POST 的简易服务
# 或临时把 callbackUrl 指到能打日志的地址
```

最小自测：

```bash
curl -sS -X POST "http://127.0.0.1:6690/upload" \
  -H "Content-Type: application/json; charset=utf-8" \
  -d '{
    "filePath": "D:\\testnwd\\demo.nwd",
    "streamId": "YOUR_STREAM",
    "serverUrl": "https://app.speckle.systems",
    "token": "YOUR_TOKEN",
    "branchName": "main",
    "commitMessage": "nwd test",
    "requestId": "nwd-001",
    "callbackUrl": "http://127.0.0.1:6689/api/callback"
  }'
```

并发第二单应得到 `Another upload is in progress.`

---

## 14. 推荐实现顺序（按这个勾）

按下面顺序提交/编码，每步都能单独验证。

| # | 任务 | 验证 |
|---|------|------|
| 1 | 空插件 `EventWatcherPlugin` + 日志，启动 NavisWorks 能看到 log | 插件目录下有 `.log` |
| 2 | `HttpUploadServer` + `/health` | 浏览器打开 health |
| 3 | `POST /upload` 校验 + 入队 Busy | curl 参数错误 / 并发 |
| 4 | Idle 调度：Execute 里只 `PluginLog` | 日志出现 `Execute: start` |
| 5 | `TryOpenFile` + 路径相同跳过 | NavisWorks 窗口打开 nwd；再发同一路径不重开 |
| 6 | 收集 `ModelItem` 并打 count | 日志有 count |
| 7 | `ConverterNavisworks` 循环 + 进度 `解析` | callback 百分比 10–50 |
| 8 | `Operations.Send` + `CommitCreate` + 进度 `上传/提交` | Speckle 网页能看到 commit |
| 9 | 最终同步 callback `is_final=true` | speckle_sync 判定完成 |
| 10 | 确认完成后文档仍打开；第二文件替换第一份 | 人工看窗口 |
| 11 | 安装脚本 + Unblock | 换一台机可部署 |

不要先写 Speckle 再写 HTTP：没有 Idle 调度时，后台线程一碰 API 就会崩。

---

## 15. 明确不要做的事

1. **不要**做 Revit 那套「先开空白文档再关活动文档」。NavisWorks 不需要。
2. **不要**在最终回调后 `Clear()` / 关文件。
3. **不要**改 callback 字段名或进度公式。
4. **不要**在 HTTP 线程调 `OpenFile` / 遍历 `ModelItem`。
5. **不要**忘了 `SetConverterSettings(_Mode=objects)`。
6. **不要**把 `Autodesk.Navisworks.Api.dll` Copy Local。
7. **不要**占用 6687/6688/6689。
8. 第一版 **不要**做弹窗抑制、选择集、Saved Viewpoint、层次 Collection。
9. 第一版 **不要**承诺 `.nwf`。

---

## 16. speckle_sync 侧改什么

协议不变，只改 **插件端口**：

- Revit 2024：`127.0.0.1:6688/upload`
- NavisWorks：`127.0.0.1:6690/upload`

按文件后缀分流：`.rvt` → Revit 插件；`.nwd` / `.nwc` → NavisWorks 插件。`callbackUrl` 仍指向 `6689/api/callback`。

---

## 17. 对照清单（写完用这个验收）

- [ ] NavisWorks 启动即监听 `6690`
- [ ] `GET /health` 返回 ok
- [ ] `POST /upload` 立即 `ret:0`，忙时 `500`
- [ ] 所有 NavisWorks/Speckle 转换在 Idle/`Execute` 内
- [ ] 打开本地 `.nwd` 成功；相同路径不重复打开
- [ ] 进度：接收 1 → … → 解析 10–50 → 上传 50–90 → 提交 91 → 完成 100
- [ ] 过程 callback 异步；最终 callback 同步且 `is_final=true`
- [ ] Speckle 上能看到 commit，`object_count` > 0
- [ ] 完成后模型仍打开
- [ ] 下一单不同文件时替换上一份
- [ ] 日志在插件目录，能按 `[Http]` `[Doc]` `[Speckle]` `[Callback]` 检索

---

## 18. 关键对照文件速查

| 新项目文件 | 从 Revit 仓库怎么处理 |
|------------|------------------------|
| `PluginSettings.cs` | 拷贝，默认端口改 6690，删弹窗配置 |
| `SpeckleUploadNavisApp.cs` | 对照 `SpeckleUploadApp.cs` 重写为 EventWatcherPlugin |
| `Http/*` | 几乎原样 |
| `Models/*` | 原样 |
| `CallbackService.cs` / `UploadCallbackReporter.cs` | 原样 |
| `UploadIdleHandler.cs` | 对照 `UploadEventHandler.cs`，去掉 ExternalEvent 与关文档 |
| `DocumentService.cs` | 重写 OpenFile + GetGeometryItems |
| `SpeckleSendService.cs` | 对照原文件，换 ConverterNavisworks，补 `_Mode` |
| 安装脚本 | 对照 `Install-SpeckleUpload.ps1`，改目标 Plugins 目录 |
| `.addin` | **不要** |

官方 Speckle 参考（转换细节卡住时再看）：

- https://github.com/specklesystems/speckle-sharp/tree/main/Objects/Converters/ConverterNavisworks
- https://github.com/specklesystems/speckle-sharp/tree/main/ConnectorNavisworks
