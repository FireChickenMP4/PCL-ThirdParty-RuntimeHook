# 获取编译依赖

```powershell
powershell -ExecutionPolicy Bypass -File fetch-deps.ps1
```

会从 NuGet 下载并解压到 `lib/`：

| 文件 | 来源 | 许可证 |
| --- | --- | --- |
| `dnlib.dll` | dnlib 4.5.0 (`lib/net45`) | MIT |
| `0Harmony.dll` | Lib.Harmony 2.4.3 (`lib/net48`) | MIT |

`lib/` 下的 dll 不纳入版本库，需要本地自行获取。
