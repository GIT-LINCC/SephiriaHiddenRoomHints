# Sephiria 隐藏房间提示 Mod

为 Steam 版《Sephiria（赛菲莉娅）》制作的 BepInEx 5 插件。它读取游戏实际生成的隐藏墙触发器，因此不会只凭墙体纹理猜测隐藏房。

## 功能

- 进入含隐藏房的楼层后，显示“本层存在隐藏房间”的提示。
- 在完整地图对应房间图标的右上角显示红色标记。
- 在实际隐藏墙砖处绘制闪烁的黄色框。
- 墙被打通后，标记会保留至离开当前楼层。
- 可选测试模式：房主/单机端可用 F8 秒杀非玩家单位，并让首层至少生成一个隐藏房。默认关闭。

## 安装

1. 下载本仓库 [Releases](../../releases) 页中最新的 `SephiriaHiddenRoomHints-v*.zip`。
2. 安装 **BepInEx 5 x64** 到 Sephiria 游戏根目录。可从 [BepInEx Releases](https://github.com/BepInEx/BepInEx/releases) 下载；本插件在 BepInEx `5.4.23.5` 上测试通过。
3. 解压下载的 ZIP 到游戏根目录。解压后应存在：

   ```text
   Sephiria/
   └─ BepInEx/
      └─ plugins/
         └─ SephiriaHiddenRoomHints.dll
   ```

4. 启动游戏即可。首次启动会生成配置文件。

## 配置

配置文件路径：

```text
Sephiria/BepInEx/config/codex.sephiria.hidden-room-hints.cfg
```

常用项目：

```ini
[Display]
ShowSystemMessage = true
ShowMapMarker = true
ShowWallMarker = true
ShowScreenNotice = true

[Testing]
EnableTestMode = false
ForceHiddenRoomOnFirstFloor = false
InstantKillKey = F8
```

正常游玩请保持 `EnableTestMode = false`。若要测试隐藏房提示，可改为 `true`，并将 `ForceHiddenRoomOnFirstFloor = true`；在单机或房主端按 F8 可清除当前场景的非玩家单位。修改配置后重启游戏。

## 联机说明

隐藏房提示为客户端显示功能。测试模式会改变房主端的楼层生成/单位状态，只建议单机或房主自行测试使用。

## 从源码构建

需要 .NET 8 SDK、已安装 BepInEx 5 的 Sephiria 游戏目录。示例：

```powershell
dotnet run --project tests\SephiriaHiddenRoomHints.Tests.csproj
dotnet build src\SephiriaHiddenRoomHints.csproj /p:SephiriaDir="D:\SteamLibrary\steamapps\common\Sephiria"
```

DLL 输出至 `build/netstandard2.0/SephiriaHiddenRoomHints.dll`。

## 许可证

本项目采用 [MIT License](LICENSE)。
