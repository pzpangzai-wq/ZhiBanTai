# GitHub 上传步骤

本目录已经整理为适合公开上传的源码仓库。

## 建议仓库信息

- 仓库名：`ZhiBanTai`
- 简介：`面向 Win7/Windows 局域网环境的查询检测工具，用卡片显示电脑和网络打印机，支持扫描、记忆、离线保留和共享状态提示。`
- 可见性：公开或私有都可以；如果包含真实运行数据，请选择私有。
- 许可证：MIT

## 建议上传到源码仓库

- `.gitignore`
- `README.md`
- `LICENSE`
- `LanMonitor.cs`
- `ZhiBanTai.ico`
- `使用说明.txt`
- `电脑配置.example.txt`
- `程序设置.example.txt`
- `build.ps1`
- `发布说明.md`

## 不建议上传到源码仓库

- 真实 `电脑配置.txt`
- 真实 `电脑清单.txt`
- 真实 `程序设置.txt`
- `错误日志.txt`
- 本地生成的 `.exe`
- 本地生成的 `.zip`

如果要公开提供可直接运行的压缩包，建议在 GitHub Releases 页面上传 `值班台-通用版.zip`，不要直接放进源码仓库。

## 有 Git 时的命令行上传方式

```powershell
git init
git add .
git commit -m "Initial release"
git branch -M main
git remote add origin https://github.com/YOUR_NAME/ZhiBanTai.git
git push -u origin main
```
