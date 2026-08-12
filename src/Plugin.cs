using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mirror;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SephiriaHiddenRoomHints;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class HiddenRoomHintPlugin : BaseUnityPlugin
{
    private const string PluginGuid = "codex.sephiria.hidden-room-hints";
    private const string PluginName = "Sephiria Hidden Room Hints";
    private const string PluginVersion = "0.4.2";

    private static readonly FieldInfo HiddenRoomConnectRoomsField =
        typeof(EnhancedProceduralFloorGenerator).GetField(
            "hiddenRoomConnectRoomInstances",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo HiddenRoomConnectPassagesField =
        typeof(EnhancedProceduralFloorGenerator).GetField(
            "hiddenRoomConnectPassageIndexs",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly List<HiddenRoomHint> hints = new();
    private readonly Dictionary<int, EnhancedProceduralFloorGenerator?> triggerOwners = new();
    private readonly Dictionary<int, HiddenRoomTriggerCollider> pendingTriggers = new();
    private readonly HashSet<int> seenTriggerIds = new();
    private readonly HashSet<int> announcedTriggerIds = new();
    private readonly Dictionary<int, GameObject> rawWallMarkers = new();
    private readonly Dictionary<UI_Map, GameObject> mapMarkers = new();

    private ConfigEntry<bool> enableTestMode = null!;
    private ConfigEntry<bool> forceHiddenRoomOnFirstFloor = null!;
    private ConfigEntry<KeyCode> instantKillKey = null!;
    private ConfigEntry<bool> showSystemMessage = null!;
    private ConfigEntry<bool> showMapMarker = null!;
    private ConfigEntry<bool> showWallMarker = null!;
    private ConfigEntry<bool> showScreenNotice = null!;
    private ConfigEntry<float> scanInterval = null!;
    private float nextScanTime;
    private float nextDiagnosticsTime;
    private string screenNotice = string.Empty;
    private float screenNoticeUntil;

    private void Awake()
    {
        enableTestMode = Config.Bind("Testing", "EnableTestMode", false, "启用测试功能。关闭时不会响应秒杀快捷键，也不会修改楼层生成数据。默认关闭。重启游戏后生效。\nEnableTestMode=false");
        forceHiddenRoomOnFirstFloor = Config.Bind("Testing", "ForceHiddenRoomOnFirstFloor", true, "测试模式下让每个场景的第一层至少生成一个隐藏房间。\nForceHiddenRoomOnFirstFloor=true");
        instantKillKey = Config.Bind("Testing", "InstantKillKey", KeyCode.F8, "测试模式下秒杀当前场景所有非玩家单位的快捷键。设为 None 可禁用。\nInstantKillKey=F8");
        showSystemMessage = Config.Bind("Display", "ShowSystemMessage", true, "在游戏系统消息中显示本层有隐藏房间。");
        showMapMarker = Config.Bind("Display", "ShowMapMarker", true, "在完整地图对应区域上显示标记。");
        showWallMarker = Config.Bind("Display", "ShowWallMarker", true, "在隐藏墙的实际位置显示闪烁框。");
        showScreenNotice = Config.Bind("Display", "ShowScreenNotice", true, "在屏幕左上角保留短暂的文字提示。");
        scanInterval = Config.Bind("Performance", "ScanIntervalSeconds", 0.25f, "扫描隐藏墙触发器的间隔秒数。");

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
    }

    private void Update()
    {
        if (enableTestMode.Value)
        {
            ForceFirstFloorHiddenRoom();

            if (WasInstantKillPressed())
            {
                InstantKillAllEnemies();
            }
        }

        if (Time.unscaledTime < nextScanTime)
        {
            return;
        }

        nextScanTime = Time.unscaledTime + Mathf.Clamp(scanInterval.Value, 0.1f, 2f);
        ScanForHiddenRooms();
        UpdateMapMarkers();
        PruneDestroyedFloors();
        WriteDiagnosticsIfDue();
    }

    private bool WasInstantKillPressed()
    {
        if (instantKillKey.Value == KeyCode.None)
        {
            return false;
        }

        var key = MapInputSystemKey(instantKillKey.Value);
        return key.HasValue
            && Keyboard.current != null
            && Keyboard.current[key.Value].wasPressedThisFrame;
    }

    private static Key? MapInputSystemKey(KeyCode keyCode)
    {
        return keyCode switch
        {
            KeyCode.F1 => Key.F1,
            KeyCode.F2 => Key.F2,
            KeyCode.F3 => Key.F3,
            KeyCode.F4 => Key.F4,
            KeyCode.F5 => Key.F5,
            KeyCode.F6 => Key.F6,
            KeyCode.F7 => Key.F7,
            KeyCode.F8 => Key.F8,
            KeyCode.F9 => Key.F9,
            KeyCode.F10 => Key.F10,
            KeyCode.F11 => Key.F11,
            KeyCode.F12 => Key.F12,
            _ => null
        };
    }

    private void ForceFirstFloorHiddenRoom()
    {
        if (!forceHiddenRoomOnFirstFloor.Value || !NetworkServer.active || DungeonManager.Instance == null)
        {
            return;
        }

        foreach (var generator in FindObjectsByType<EnhancedProceduralFloorGenerator>(FindObjectsSortMode.None))
        {
            if (!generator || generator.GenerateSuccess)
            {
                continue;
            }

            var data = generator.DataOnServer;
            if (data == null || !IsFirstFloor(data))
            {
                continue;
            }

            if (!DungeonManager.Instance.generatedFloors.TryGetValue(generator.guid, out var generatedData))
            {
                continue;
            }

            var hiddenRoomCount = TestModeRules.GetHiddenRoomCount(true, true, generatedData.hiddenRoomCount);
            if (generatedData.hiddenRoomCount >= hiddenRoomCount)
            {
                continue;
            }

            generatedData.hiddenRoomCount = hiddenRoomCount;
            if (!ReferenceEquals(data, generatedData))
            {
                data.hiddenRoomCount = hiddenRoomCount;
            }

            Logger.LogInfo($"Test mode: forced a hidden room on first floor '{data.name}' ({data.guid}).");
        }
    }

    private static bool IsFirstFloor(FloorData data)
    {
        var stage = DungeonManager.Instance.FindStage(data.stageName);
        return stage != null
            && stage.firstFloor != null
            && data.globalX == 0
            && string.Equals(data.name, stage.firstFloor.name, StringComparison.Ordinal);
    }

    private void InstantKillAllEnemies()
    {
        if (!NetworkServer.active)
        {
            ShowTestNotice("秒杀功能需要在单机或房主端使用。", Color.orange);
            return;
        }

        var killed = 0;
        foreach (var avatar in FindObjectsByType<UnitAvatar>(FindObjectsSortMode.None))
        {
            if (!avatar || !TestModeRules.ShouldInstantKill(true, avatar is PlayerAvatar, avatar.IsDead))
            {
                continue;
            }

            avatar.ForceDie();
            killed++;
        }

        ShowTestNotice($"测试秒杀：已处理 {killed} 个非玩家单位。", Color.cyan);
    }

    private void ShowTestNotice(string notice, Color color)
    {
        if (showSystemMessage.Value && UIManager.Instance != null)
        {
            var message = UIManager.Instance.GetElement<UI_SystemMessage>();
            if (message != null)
            {
                message.Open(notice, 5f);
            }
        }

        if (GameLogWriter.Instance != null)
        {
            GameLogWriter.Instance.WriteLog(notice, color);
        }

        screenNotice = notice;
        screenNoticeUntil = Time.unscaledTime + 7f;
        Logger.LogInfo(notice);
    }

    private void ScanForHiddenRooms()
    {
        var added = 0;
        var activeTriggerIds = new HashSet<int>();
        foreach (var trigger in FindObjectsByType<HiddenRoomTriggerCollider>(FindObjectsSortMode.None))
        {
            if (!trigger)
            {
                continue;
            }

            var triggerId = trigger.GetInstanceID();
            activeTriggerIds.Add(triggerId);
            if (!triggerOwners.ContainsKey(triggerId))
            {
                triggerOwners[triggerId] = null;
                HiddenRoomHintTracker.TryAcceptTrigger(seenTriggerIds, triggerId);
            }

            if (announcedTriggerIds.Add(triggerId))
            {
                ShowLayerNotice(activeTriggerIds.Count);
            }

            if (FindHint(triggerId) != null)
            {
                continue;
            }

            if (TryCreateHint(trigger, out var hint))
            {
                hints.Add(hint);
                pendingTriggers.Remove(triggerId);
                added++;
            }
            else
            {
                pendingTriggers[triggerId] = trigger;
                if (showWallMarker.Value && !rawWallMarkers.ContainsKey(triggerId))
                {
                    rawWallMarkers[triggerId] = CreateWallMarker(trigger, null);
                }
            }
        }

        foreach (var pending in pendingTriggers.Values.ToArray())
        {
            if (!pending)
            {
                continue;
            }

            if (TryCreateHint(pending, out var hint))
            {
                hints.Add(hint);
                pendingTriggers.Remove(pending.GetInstanceID());
                added++;
            }
        }

        if (added > 0)
        {
            ShowLayerNotice(hints.Count);
        }

        // Keep the acceptance set bounded while allowing Unity to reuse instance ids on a later floor.
        foreach (var oldId in triggerOwners.Keys.Where(id => !activeTriggerIds.Contains(id)).ToArray())
        {
            if (FindHint(oldId) == null)
            {
                triggerOwners.Remove(oldId);
                seenTriggerIds.Remove(oldId);
                announcedTriggerIds.Remove(oldId);
                pendingTriggers.Remove(oldId);
                if (rawWallMarkers.Remove(oldId, out var rawWallMarker) && rawWallMarker != null)
                {
                    Destroy(rawWallMarker);
                }
            }
        }
    }

    private bool TryCreateHint(HiddenRoomTriggerCollider trigger, out HiddenRoomHint hint)
    {
        foreach (var generator in FindObjectsByType<EnhancedProceduralFloorGenerator>(FindObjectsSortMode.None))
        {
            if (!generator)
            {
                continue;
            }

            if (!TryFindAssociatedRoom(trigger, generator, out var room))
            {
                continue;
            }

            var roomCoordinate = new RoomCoordinate(room.pos.x, room.pos.y, room.size.x, room.size.y);
            triggerOwners[trigger.GetInstanceID()] = generator;
            var triggerId = trigger.GetInstanceID();
            GameObject? wallMarker = null;
            if (rawWallMarkers.Remove(triggerId, out var existingWallMarker))
            {
                        wallMarker = existingWallMarker;
                        UpdateWallMarker(wallMarker, trigger, generator);
                    }
                    else if (showWallMarker.Value)
                    {
                        wallMarker = CreateWallMarker(trigger, generator);
            }

            hint = new HiddenRoomHint(
                triggerId,
                trigger,
                generator,
                room,
                roomCoordinate,
                trigger.transform.position,
                wallMarker);
            return true;
        }

        hint = null!;
        return false;
    }

    private static bool TryFindAssociatedRoom(
        HiddenRoomTriggerCollider trigger,
        EnhancedProceduralFloorGenerator generator,
        out TileBasedRoomInstance room)
    {
        if (HiddenRoomConnectRoomsField.GetValue(generator) is IList<TileBasedRoomInstance> hiddenRooms
            && trigger.hiddenRoomIndex >= 0
            && trigger.hiddenRoomIndex < hiddenRooms.Count
            && hiddenRooms[trigger.hiddenRoomIndex] != null)
        {
            room = hiddenRooms[trigger.hiddenRoomIndex];
            return true;
        }

        // Fallback for a game update that changes the private list name or layout.
        var local = (Vector2)trigger.transform.position - (Vector2)generator.transform.position;
        var candidate = new Vector2Int(Mathf.FloorToInt(local.x / 26f), Mathf.FloorToInt(local.y / 18f));
        for (var x = -2; x <= 2; x++)
        {
            for (var y = -2; y <= 2; y++)
            {
                var fallbackRoom = generator.FindRoom(candidate + new Vector2Int(x, y));
                if (fallbackRoom == null)
                {
                    continue;
                }

                room = fallbackRoom;
                return true;
            }
        }

        room = null!;
        return false;
    }

    private HiddenRoomHint? FindHint(int triggerId)
    {
        return hints.FirstOrDefault(item => item.TriggerId == triggerId);
    }

    private void ShowLayerNotice(int count)
    {
        var notice = HiddenRoomHintText.BuildLayerNotice(count);
        var area = hints.LastOrDefault()?.Area;
        if (area.HasValue)
        {
            notice += " · " + HiddenRoomHintText.BuildAreaLabel(area.Value);
        }

        if (showSystemMessage.Value && UIManager.Instance != null)
        {
            var message = UIManager.Instance.GetElement<UI_SystemMessage>();
            if (message != null)
            {
                message.Open(notice, 5f);
            }
        }

        if (GameLogWriter.Instance != null)
        {
            GameLogWriter.Instance.WriteLog(notice, Color.yellow);
        }

        screenNotice = notice;
        screenNoticeUntil = Time.unscaledTime + 7f;
        Logger.LogInfo(notice);
    }

    private GameObject CreateWallMarker(
        HiddenRoomTriggerCollider trigger,
        EnhancedProceduralFloorGenerator? generator)
    {
        var marker = new GameObject("Sephiria Hidden Room Wall Marker");
        var line = marker.AddComponent<LineRenderer>();
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        if (shader != null)
        {
            line.material = new Material(shader)
            {
                name = "Sephiria Hidden Room Marker Material"
            };
        }
        line.useWorldSpace = true;
        line.loop = true;
        line.positionCount = 4;
        line.widthMultiplier = 0.12f;
        line.sortingOrder = 1000;
        line.startColor = new Color(1f, 0.85f, 0.1f, 0.95f);
        line.endColor = line.startColor;

        UpdateWallMarker(marker, trigger, generator);
        marker.AddComponent<WallMarkerPulse>();
        return marker;
    }

    private static void UpdateWallMarker(
        GameObject marker,
        HiddenRoomTriggerCollider trigger,
        EnhancedProceduralFloorGenerator? generator)
    {
        var line = marker.GetComponent<LineRenderer>();
        if (line == null)
        {
            return;
        }

        var size = trigger.boxCollider != null ? trigger.boxCollider.size : new Vector2(1f, 1f);
        var center = trigger.transform.position;
        var horizontal = size.x >= size.y;
        if (generator != null && TryFindActualWall(trigger, generator, out var wallCenter, out var wallHorizontal))
        {
            center = wallCenter;
            horizontal = wallHorizontal;
        }

        // The trigger is spawned one tile outside the hidden wall. Draw the outline
        // around the actual wall tile, not around the attackable ground trigger.
        center.z = trigger.transform.position.z - 0.15f;
        var halfWidth = horizontal ? Mathf.Max(1.2f, size.x * 0.5f) : 0.55f;
        var halfHeight = horizontal ? 0.55f : Mathf.Max(1.2f, size.y * 0.5f);
        line.SetPosition(0, center + new Vector3(-halfWidth, -halfHeight, 0f));
        line.SetPosition(1, center + new Vector3(-halfWidth, halfHeight, 0f));
        line.SetPosition(2, center + new Vector3(halfWidth, halfHeight, 0f));
        line.SetPosition(3, center + new Vector3(halfWidth, -halfHeight, 0f));
    }

    private static bool TryFindActualWall(
        HiddenRoomTriggerCollider trigger,
        EnhancedProceduralFloorGenerator generator,
        out Vector3 wallCenter,
        out bool horizontal)
    {
        if (generator.wall == null)
        {
            wallCenter = default;
            horizontal = false;
            return false;
        }

        var triggerCell = generator.wall.WorldToCell(trigger.transform.position);
        if (TryGetWallNormal(trigger, generator, out var wallNormal, out horizontal))
        {
            var inward = new Vector3Int(-Mathf.RoundToInt(wallNormal.x), -Mathf.RoundToInt(wallNormal.y), 0);
            for (var distance = 1; distance <= 3; distance++)
            {
                var candidate = triggerCell + inward * distance;
                if (generator.wall.GetTile(candidate) == null)
                {
                    continue;
                }

                wallCenter = generator.wall.GetCellCenterWorld(candidate);
                return true;
            }
        }

        // Different room tilesets can shift their trigger pivot. If the expected direction
        // has no wall tile, use the closest real wall around the trigger instead of showing
        // the marker on walkable ground.
        for (var distance = 0; distance <= 3; distance++)
        {
            for (var x = -distance; x <= distance; x++)
            {
                var yDistance = distance - Mathf.Abs(x);
                for (var sign = -1; sign <= 1; sign += 2)
                {
                    var y = yDistance * sign;
                    if (distance == 0 && sign > 0)
                    {
                        continue;
                    }

                    var candidate = triggerCell + new Vector3Int(x, y, 0);
                    if (generator.wall.GetTile(candidate) == null)
                    {
                        continue;
                    }

                    wallCenter = generator.wall.GetCellCenterWorld(candidate);
                    horizontal = y != 0 ? true : x == 0 ? horizontal : false;
                    return true;
                }
            }
        }

        wallCenter = default;
        horizontal = false;
        return false;
    }

    private static bool TryGetWallNormal(
        HiddenRoomTriggerCollider trigger,
        EnhancedProceduralFloorGenerator generator,
        out Vector2 wallNormal,
        out bool horizontal)
    {
        if (HiddenRoomConnectRoomsField.GetValue(generator) is not IList<TileBasedRoomInstance> rooms
            || HiddenRoomConnectPassagesField.GetValue(generator) is not IList<int> passages
            || trigger.hiddenRoomIndex < 0
            || trigger.hiddenRoomIndex >= rooms.Count
            || trigger.hiddenRoomIndex >= passages.Count)
        {
            wallNormal = default;
            horizontal = false;
            return false;
        }

        var room = rooms[trigger.hiddenRoomIndex];
        if (room == null)
        {
            wallNormal = default;
            horizontal = false;
            return false;
        }

        GridDungeonGenerator.GetPassageTileIdx(
            room.Metadata.type,
            passages[trigger.hiddenRoomIndex],
            out var direction);
        if (direction == EGridRoomPassageDir.None)
        {
            wallNormal = default;
            horizontal = false;
            return false;
        }

        wallNormal = direction switch
        {
            EGridRoomPassageDir.Up => Vector2.up,
            EGridRoomPassageDir.Right => Vector2.right,
            EGridRoomPassageDir.Down => Vector2.down,
            EGridRoomPassageDir.Left => Vector2.left,
            _ => Vector2.zero
        };
        if (wallNormal == Vector2.zero)
        {
            horizontal = false;
            return false;
        }

        horizontal = direction is EGridRoomPassageDir.Up or EGridRoomPassageDir.Down;
        return true;
    }

    private void UpdateMapMarkers()
    {
        if (!showMapMarker.Value)
        {
            return;
        }

        foreach (var map in FindObjectsByType<UI_Map>(FindObjectsSortMode.None))
        {
            if (!map || map.contentsChild == null)
            {
                continue;
            }

            if (!hints.Any(hint => hint.Generator.mapInstance == map))
            {
                continue;
            }

            foreach (var hint in hints)
            {
                if (hint.MapMarker != null)
                {
                    continue;
                }

                foreach (var room in map.rooms)
                {
                    if (room is not UI_Map_EnhancedProceduralDungeonRoom roomIcon || roomIcon.room != hint.Room)
                    {
                        continue;
                    }

                    hint.MapMarker = CreateMapMarker(map, roomIcon);
                    mapMarkers[map] = hint.MapMarker;
                    break;
                }
            }
        }
    }

    private static GameObject CreateMapMarker(UI_Map map, UI_Map_EnhancedProceduralDungeonRoom roomIcon)
    {
        var marker = new GameObject("Sephiria Hidden Room Map Marker", typeof(RectTransform), typeof(Image));
        var rect = marker.GetComponent<RectTransform>();
        rect.SetParent(map.contentsChild, worldPositionStays: false);
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        const float markerSize = 12f;
        const float insideMargin = 3f;
        var iconSize = roomIcon.GetRoomIconSize();
        rect.anchoredPosition = roomIcon.GetIconCenterAnchoredPosition()
            + new Vector2(
                Mathf.Max(0f, iconSize.x * 0.5f - markerSize * 0.5f - insideMargin),
                Mathf.Max(0f, iconSize.y * 0.5f - markerSize * 0.5f - insideMargin));
        rect.sizeDelta = Vector2.one * markerSize;

        var image = marker.GetComponent<Image>();
        image.color = new Color(1f, 0.2f, 0.1f, 0.82f);
        image.raycastTarget = false;
        marker.transform.SetAsLastSibling();
        return marker;
    }

    private void PruneDestroyedFloors()
    {
        foreach (var hint in hints.ToArray())
        {
            if (hint.Generator != null)
            {
                continue;
            }

            if (hint.WallMarker != null)
            {
                Destroy(hint.WallMarker);
            }

            if (hint.MapMarker != null)
            {
                Destroy(hint.MapMarker);
            }

            hints.Remove(hint);
        }

        foreach (var map in mapMarkers.Keys.Where(map => map == null).ToArray())
        {
            mapMarkers.Remove(map);
        }
    }

    private void WriteDiagnosticsIfDue()
    {
        if (!enableTestMode.Value || Time.unscaledTime < nextDiagnosticsTime)
        {
            return;
        }

        nextDiagnosticsTime = Time.unscaledTime + 5f;
        var triggers = FindObjectsByType<HiddenRoomTriggerCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        var avatars = FindObjectsByType<UnitAvatar>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        var generators = FindObjectsByType<EnhancedProceduralFloorGenerator>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        Logger.LogInfo($"Test diagnostics: server={NetworkServer.active}, triggers={triggers}, avatars={avatars}, generators={generators}, hints={hints.Count}, pending={pendingTriggers.Count}.");
    }

    private void OnGUI()
    {
        if (!showScreenNotice.Value || Time.unscaledTime > screenNoticeUntil || string.IsNullOrEmpty(screenNotice))
        {
            return;
        }

        var oldColor = GUI.color;
        GUI.color = new Color(1f, 0.92f, 0.45f, 0.95f);
        GUI.Box(new Rect(16f, 16f, 460f, 44f), screenNotice);
        GUI.color = oldColor;
    }

    private sealed class HiddenRoomHint
    {
        public HiddenRoomHint(
            int triggerId,
            HiddenRoomTriggerCollider trigger,
            EnhancedProceduralFloorGenerator generator,
            TileBasedRoomInstance room,
            RoomCoordinate area,
            Vector3 worldPosition,
            GameObject? wallMarker)
        {
            TriggerId = triggerId;
            Trigger = trigger;
            Generator = generator;
            Room = room;
            Area = area;
            WorldPosition = worldPosition;
            WallMarker = wallMarker;
        }

        public int TriggerId { get; }
        public HiddenRoomTriggerCollider Trigger { get; }
        public EnhancedProceduralFloorGenerator Generator { get; }
        public TileBasedRoomInstance Room { get; }
        public RoomCoordinate Area { get; }
        public Vector3 WorldPosition { get; }
        public GameObject? WallMarker { get; }
        public GameObject? MapMarker { get; set; }
    }
}

internal sealed class WallMarkerPulse : MonoBehaviour
{
    private LineRenderer line = null!;
    private float initialAlpha;

    private void Awake()
    {
        line = GetComponent<LineRenderer>();
        initialAlpha = line.startColor.a;
    }

    private void Update()
    {
        if (line == null)
        {
            return;
        }

        var alpha = Mathf.Lerp(0.25f, initialAlpha, (Mathf.Sin(Time.unscaledTime * 4f) + 1f) * 0.5f);
        var color = line.startColor;
        color.a = alpha;
        line.startColor = color;
        line.endColor = color;
    }
}
