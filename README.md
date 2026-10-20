# PCL 第三方运行期补丁（PCL Third-Party Runtime Hook）

> 原作者：龙腾猫跃（PCL）
> 赞助 PCL：https://meloong.com/afd/a/LTCat
> 本项目是第三方基于 PCL 独立进行的二次创作，与 PCL 官方无关。
> 参考与致谢见 [CREDITS.md](CREDITS.md)，PCL 的许可与合理使用指南见 [PCL-LICENCE.md](PCL-LICENCE.md)。
> 本仓库不包含任何 PCL 二进制或 PCL 源代码，也不提供预编译产物。

不修改 PCL 的 exe，通过 `AppDomainManager` + `Harmony` 在进程内补全被编译期删掉的功能。
目标方法由 dnlib 读取 IL、按语义锚点定位，再按 MetadataToken 映射到运行中的方法。

## 学习声明

本项目用于研究 .NET 程序集结构、`AppDomainManager` 注入、Harmony 补丁与 IL 静态分析。
不提供、不鼓励分发任何修改后的 PCL；请支持 PCL 原作者，并自行确认使用场景的合规性。
本项目不构成对任何下载行为、游戏内容或第三方服务的授权。

## 目录

```
PCL-Hook-源码/
├─ build.cmd                 用系统自带 csc 编译
├─ fetch-deps.ps1            从 NuGet 下载 dnlib / Harmony（带 SHA256 校验）
├─ CREDITS.md                参考与致谢
├─ PCL-LICENCE.md            PCL 的许可与合理使用指南（原文）
├─ LICENSE
├─ lib/                      编译依赖（dll 不入库）
├─ src/
│  ├─ AssemblyInfo.cs        版本与安全透明级别
│  ├─ DomainManager.cs       注入入口
│  ├─ HookPlan.cs            语义定位：读 IL，按锚点生成 token plan
│  ├─ Refl.cs                反射访问 PCL 内部成员
│  ├─ Patches.cs             按 plan 解析 MethodInfo/FieldInfo 并安装补丁
│  ├─ PatchMethods.cs        Prefix/Postfix 实现、原生跳转的替换方法
│  ├─ ModpackInstaller.cs    重建整合包类型判定与分发
│  ├─ NativeDetour.cs        JIT 入口跳转
│  └─ SelfCheck.cs           自检（仅调试构建）
└─ check/
   └─ CheckProgram.cs        离线兼容检查工具
```

## 依赖与编译

```powershell
powershell -ExecutionPolicy Bypass -File fetch-deps.ps1
build.cmd
```

产物：

- `bin\PclHook.dll`
- `bin\pclhook-check.exe`
- `bin\dnlib.dll`、`bin\0Harmony.dll`（复制自 lib，方便直接部署）

需要 Windows 自带的 .NET Framework 4.8 `csc.exe`，不需要安装 SDK。

自检代码默认不编译。需要时：

```
build.cmd selftest
```

它会覆盖 `bin\PclHook.dll`，发布前请重新执行一次 `build.cmd` 生成 release 版本。

## 部署

把以下文件放到 **PCL 所在目录**（必须与 `Plain Craft Launcher 2.exe` 同级，不能放在 `lib\` 子目录）：

```
PclHook.dll
0Harmony.dll
dnlib.dll
Plain Craft Launcher 2.exe.config
```

配置内容：

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <runtime>
    <appDomainManagerAssembly value="PclHook" />
    <appDomainManagerType value="PclHook.DomainManager" />
  </runtime>
</configuration>
```

注意：

- `PclHook.dll` 不能改名，配置里的程序集名要和它一致
- 如果目录里已有 `Plain Craft Launcher 2.exe.config`，请把 `<runtime>` 节点合并进去，
  直接覆盖可能丢掉原有配置
- 需要 .NET Framework 4.8
- 卸载时删除上面 4 个文件即可
- DLL 未签名且会注入进程，杀软 / SmartScreen 可能拦截，需要自行加白名单

之后直接双击 PCL 的 exe，不需要启动器。

## 工作方式

1. `exe.config` 让 CLR 在 PCL 的 `Main` 之前加载 `PclHook.dll`
2. PCL 主程序集加载后，用 dnlib 读取 exe，按锚点生成 plan
3. 通过 `Module.ResolveMethod(token)` / `ResolveField(token)` 映射到运行中的成员
4. 用 Harmony / 原生跳转应用补丁
5. PCL 更新只替换 exe，hook 文件不受影响；版本不是 2.99 时补丁会自动禁用

## 语义锚点

| 目标 | 依据 |
| --- | --- |
| `LoaderInit` | 字符串 `"关闭下载"` + 其后的 `newobj LoaderTask<,>` |
| `DlClientListLoader` | 全模块 `LoaderTask<string, *DlClientListResult>` 静态字段中引用次数最多的一个 |
| 可见性 | `get_CardMinecraft` ... `set_Visibility` |
| `ModpackInstall(3)` | 3 个 string 参数 + 返回 `LoaderCombo` + 含 `qwq` 异常 |
| `ModpackInstall()` | 0 参数 + 含 `qwq`，且 qwq 后紧跟 3 参数 `Hint` 调用 |
| `ControlInit` | 构造 LoaderTask 之前、同类型、0 参数 void 的调用 |
| `LoadMinecraft` / `PanLoad` / `PanAllBack` | 构造前返回类型为 `MyLoading` / `MyCard` / `Grid` 的 getter |
| 完成回调 | 构造 LoaderTask 之后的 `ldftn` |
| `PageLoaderInit` | 构造之后、8 参数、泛型方法 |
| `McFolderSelected` | `ModpackInstall(3)` 里 `Format("{0}versions\{1}\", ...)` 的 getter |
| `Hint` | `ModpackInstall()` 里 `qwq` 提示对应的 3 参数调用 |

整合包安装方法按方法体里的格式标记识别，标记缺失的按参数个数兜底：

| 标记 | 方法 |
| --- | --- |
| `mcbbs.packmeta` | MCBBS |
| `mmc-pack.json` | MMC |
| `modrinth.index.json` | Modrinth |
| `modpack.json` | HMCL |
| `manifest.json` | CurseForge |
| `modpack.zip` / `modpack.mrpack` | 带启动器的压缩包 |
| 3 参数兜底 | 带启动器的压缩包 |
| 2 参数兜底 | 普通压缩包 |

## 兼容检查

```
pclhook-check.exe "Plain Craft Launcher 2.exe"
```

只读 exe，不修改文件。输出版本、每个锚点和 token；`MISS:` 行表示该目标没找到。
退出码 0 = compatible，1 = 需要适配。

`--dump-installers` 列出 `ModModpack` 里所有返回 `LoaderCombo` 的方法及其格式字符串。
`--dump-type <type>` 打印指定类型的字段和方法，便于排查结构变化。

## 版本闸门

启动时读取 PCL 程序集版本；不是 `2.99.x` 时直接跳过全部补丁并写日志：

```
unsupported PCL version, patches disabled
```

## 跨版本情况

`RestrictMcDownload` 是 2.99 才引入的。公开仓库 2.14 及更早的 `ModSecret.vb` 里没有这个常量，
对应的 exe 里也没有 `"关闭下载"` 和 `qwq` 异常，因此不存在“给旧版本打同样的补丁”这回事。

在 2.13.1.1 的官方 exe 上实测：

- 除 `DlClientListLoader` 字段外，其余锚点都依赖 2.99 才有的结构，全部 MISS
- 运行时版本闸门直接禁用补丁，PCL 正常启动
- `pclhook-check.exe` 会输出 `NOTE: 非 2.99，运行时补丁会被版本闸门禁用`

也就是说，旧版本不是“补丁失败”，而是本来就没有需要恢复的东西；新于 2.99 的版本才会遇到
锚点变化，需要按 `MISS:` 列表更新。

## 已验证

分三档：

**锚点解析**

- 官方 2.99：13 个锚点全部命中，7 个整合包安装方法全部识别
- 2.13.1.1（没有限制功能）：除 loader 字段外全部 MISS，版本闸门禁用补丁
- 方法全部改名、加 nop、常量编码变化、局部变量整体移位的变体：同样命中

**补丁安装**

- 4 处补丁安装成功，日志可见 `patchX OK`、`installers resolved: 7`
- 第 2 处运行到 `LoaderInit reimplemented`，绑定的是真实 loader

**补丁行为**

- 第 2 处：页面初始化时输出 `LoaderInit reimplemented`，绑定到真实 loader
- 第 3 处：修改页的 `CardMinecraft` 可见性被设为 `Visible`
- 第 4 处：整合包类型判定与 `InstallPack*` 分发方法均解析成功
- 第 5 处：文件选择入口的 Prefix 安装成功

## 限制

- 只适配 2.99；方法被重构、逻辑被改写时需要人工更新锚点
- 补丁失败只写日志并回退官方行为；原生跳转失败可能导致进程异常（写入前会校验签名）
- 仍按名字做兜底的只有 plan 失败时的降级路径

## 日志与开关

```
%TEMP%\pclhook.log
```

同目录下的控制文件：

| 文件 | 作用 |
| --- | --- |
| `nopatch.flag` | 只注入不补丁 |
| `skip2.flag` ... `skip5.flag` | 跳过指定补丁 |
| `selftest.flag` | 仅 SELFTEST 构建有效 |

日志包含用户路径，贴到公开 issue 前请自行检查。

## 许可

MIT。`dnlib` 与 `Lib.Harmony` 也是 MIT，构建前通过 `fetch-deps.ps1` 获取，不随仓库分发。

## 项目定位

本项目是第三方基于 PCL 的独立实现，与 PCL 官方及龙腾猫跃无隶属关系。
PCL 2.99 未提供 MC 下载与整合包安装功能，本项目通过运行期补丁补全相关流程。
本仓库只包含独立编写的源代码，不包含任何 PCL 二进制、游戏文件或预编译产物。

## 免责声明与下架

- 本项目按“现状”提供，不提供任何明示或暗示的担保；使用风险与后果由使用者自行承担。
- 使用前请自行确认所在地法律法规，以及 PCL 的许可与合理使用指南。
- 本项目不提供游戏本体、账号或任何受版权保护的内容。

## 权利人通知

如 PCL 作者或权利人认为本项目不妥，请通过 Issue 告知；本项目会在收到通知后立即删除仓库或相关内容，不进行争辩。
