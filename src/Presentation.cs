using System.Globalization;

namespace SephiriaHiddenRoomHints;

public readonly struct RoomCoordinate
{
    public RoomCoordinate(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
}

public static class HiddenRoomHintText
{
    public static string BuildLayerNotice(int count)
    {
        return string.Format(CultureInfo.InvariantCulture, "本层存在隐藏房间（{0}处）", count);
    }

    public static string BuildAreaLabel(RoomCoordinate room)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "地图区域 ({0},{1})，范围 {2}×{3}",
            room.X,
            room.Y,
            room.Width,
            room.Height);
    }
}

public static class HiddenRoomHintTracker
{
    public static bool TryAcceptTrigger(ISet<int> seenTriggerIds, int triggerId)
    {
        return seenTriggerIds.Add(triggerId);
    }
}

public static class TestModeRules
{
    public static int GetHiddenRoomCount(bool enabled, bool isFirstFloor, int currentCount)
    {
        if (!enabled || !isFirstFloor)
        {
            return currentCount;
        }

        return Math.Max(1, currentCount);
    }

    public static bool ShouldInstantKill(bool enabled, bool isPlayer, bool isDead)
    {
        return enabled && !isPlayer && !isDead;
    }
}
