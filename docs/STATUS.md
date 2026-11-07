# 进度

## 当前状态

v0.1.0 可用：文章列表、编辑器、配图工作区、本地预览、构建、发布（三条通道 + 发布前同步）。
2026-11-07 建仓，自检三项全部通过，界面已实机截图确认。

## 本轮确认的事实

- 站点目录：`C:\Neko7ina\Syncthing\Hexo\Hexo`（Syncthing 同步），分支 `main`，
  远端 `git@github.com:huaxianyan/Hexo.git`。
- 53 篇文章，50 篇能对上配图目录（`source/images/<日期>[/.NN]`）。
- `hexo.cmd` 实际执行 `node node_modules/hexo/bin/hexo`，所以程序直接调 node，
  不经过 cmd.exe 包装。
- `connect.exe` 在本机 Git 的 `ucrt64/bin`（scoop 安装的 git 2.56.0.2），
  由 `git --exec-path` 往上两级推出，不写死路径。
- 三条推送通道当前都能连通：22 端口 5.5s、443+代理 6.8s、HTTPS+代理 4.4s。
- 本地仓库曾落后远端 8 个主题同步提交（CI 推的），发布前必须同步，否则推送被拒。

## 踩过的坑

- **UI 线程同步等异步会死锁**：`--push-test` 最初在 `App.OnStartup` 里
  `GetAwaiter().GetResult()` 等异步方法，续体被派发回已阻塞的 UI 线程，表现为一直卡在
  第一步。改为 `Task.Run` 在无上下文线程上跑。
- **`-c key=value with spaces` 必须加引号**：`-c core.sshCommand=ssh -o BatchMode=yes`
  会被拆成多个参数，git 直接报用法错误（退出码 129）。
- **没有终端时必须禁交互**：`GIT_TERMINAL_PROMPT=0`、`ssh -o BatchMode=yes`，
  否则凭据或口令提问会把进程挂死，界面上永远是「发布中…」。
- **截图脚本要开 DPI 感知**：PowerShell 不开 DPI 感知时 `GetWindowRect` 返回缩放后的
  坐标，截出来的窗口只有一部分，容易被误判成布局溢出。
- 构建时 exe 被运行中的进程占用 → 先 `Get-Process HexoBlogStudio | Stop-Process`。


## 第二轮改动（2026-11-07）

- **界面换成 WPF-UI 4.3.0**（Fluent、Mica 背景、FluentWindow + TitleBar）。本机原先
  一个 NuGet 源都没配，`dotnet nuget add source https://api.nuget.org/v3/index.json`
  加上代理环境变量（`http_proxy`/`https_proxy` 指向 `127.0.0.1:37777`）才能还原。
- **预览按钮合成一个**：未运行时「启动本地预览」，运行后变「停止本地预览」，图标与
  主次色一起换；服务自己退出（崩溃或被外部杀掉）也会把按钮复原，靠 `HexoServer.Exited`。
- **配图异步加载**：`LoadPostAsync` 只读正文（`File.ReadAllTextAsync`），配图交给
  `LoadImagesAsync` 在后台跑，`SemaphoreSlim(3)` 限并发，每次切换取消上一批
  （`CancellationTokenSource`）。缩略图解码 300px 宽，冻结点后回 UI 线程赋值。
- **列表项无障碍名修正**：`Post.ToString()` 返回列表显示文本。原来 ListBoxItem 的
  自动化名是类型名 `HexoBlogStudio.Models.Post`，读屏器和 UIA 都读不出来。

### 验收数字（UIAutomation 实测）

- 选中 22 张图的《九寨沟之旅》：`SelectionItemPattern.Select()` 12 ms 返回，
  之后立刻能读到界面内容（没有卡住），日志稍后出现「配图 22 张」。
- 切换耗时只在超过 150 ms 时写日志，这次没有出现该行，说明正文切换在阈值内。
- 纯逻辑自检 19 项全过（发布版 exe 也跑过一遍）。

## 下一步（还没做）

- 新建文章（调 `hexo new`，带模板选择）
- 草稿箱与 `source/_drafts`
- 图片工作区支持拖入文件、自动生成 `proc/` 占位图
- 打包成安装包或加自启动（目前是绿色 exe）
