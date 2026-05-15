# 修改日志

## 2026-05-15 14:10
- 初始化 Revit 2022 插件项目 `SpeckleUpload`
- 新增 `SpeckleUploadApp`：Revit 启动时自动启动 HTTP 监听
- 新增 HTTP 服务（默认端口 6688，环境变量 `SPECKLE_UPLOAD_HTTP_PORT`）
- 实现 `POST /upload`：打开指定 RVT、转换 Physical Objects、发送到 Speckle stream
- 完成后回调 `SPECKLE_UPLOAD_CALLBACK_URL`（默认 `http://localhost:6689/api/callback`），并关闭文档
- 使用 `ConverterRevit` + `Operations.Send` 完成 Speckle 上传
- 生成 `SpeckleUpload.addin`（ClientId: `8eee0545-1923-46bf-a7aa-30f31f4dd7bd`）

## 2026-05-15 14:23
- 新增 GitHub Actions：`.github/workflows/specleupload-install.yml`，在 `windows-latest` 上 `dotnet build` Release，并上传 `speckleupload-revit2022-install` 制品（含 `SpeckleUpload.addin` 与输出 DLL）
