using SephiriaHiddenRoomHints;

var tests = new (string Name, Action Run)[]
{
    ("layer notice reports a single hidden room", () =>
    {
        var text = HiddenRoomHintText.BuildLayerNotice(1);
        AssertEqual("本层存在隐藏房间（1处）", text);
    }),
    ("layer notice reports multiple hidden rooms", () =>
    {
        var text = HiddenRoomHintText.BuildLayerNotice(3);
        AssertEqual("本层存在隐藏房间（3处）", text);
    }),
    ("area label includes map coordinate and room size", () =>
    {
        var room = new RoomCoordinate(2, 5, 2, 1);
        AssertEqual("地图区域 (2,5)，范围 2×1", HiddenRoomHintText.BuildAreaLabel(room));
    }),
    ("duplicate trigger ids are only accepted once", () =>
    {
        var seen = new HashSet<int>();
        AssertTrue(HiddenRoomHintTracker.TryAcceptTrigger(seen, 42));
        AssertTrue(!HiddenRoomHintTracker.TryAcceptTrigger(seen, 42));
        AssertTrue(HiddenRoomHintTracker.TryAcceptTrigger(seen, 43));
    }),
    ("first floor gets at least one hidden room in test mode", () =>
    {
        AssertIntEqual(1, TestModeRules.GetHiddenRoomCount(true, true, 0));
        AssertIntEqual(2, TestModeRules.GetHiddenRoomCount(true, true, 2));
    }),
    ("other floors and disabled test mode keep their hidden room count", () =>
    {
        AssertIntEqual(0, TestModeRules.GetHiddenRoomCount(true, false, 0));
        AssertIntEqual(0, TestModeRules.GetHiddenRoomCount(false, true, 0));
    }),
    ("instant kill only targets living non-player units", () =>
    {
        AssertTrue(TestModeRules.ShouldInstantKill(true, false, false));
        AssertTrue(!TestModeRules.ShouldInstantKill(true, true, false));
        AssertTrue(!TestModeRules.ShouldInstantKill(true, false, true));
        AssertTrue(!TestModeRules.ShouldInstantKill(false, false, false));
    }),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failures.Add($"FAIL {test.Name}: {error.Message}");
    }
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;

static void AssertEqual(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
    }
}

static void AssertIntEqual(int expected, int actual)
{
    if (expected != actual)
    {
        throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
    }
}

static void AssertTrue(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("expected condition to be true");
    }
}
