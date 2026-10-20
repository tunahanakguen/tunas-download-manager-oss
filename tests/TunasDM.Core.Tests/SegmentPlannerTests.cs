namespace TunasDM.Core.Tests;

public class SegmentPlannerTests
{
    private static SegmentPlanner Planner(long minSize, params (long Start, long Position, long End)[] segments) =>
        new(segments.Select(s => new SegmentState { Start = s.Start, Position = s.Position, End = s.End }), minSize, canSplit: true, resumed: false);

    [Fact]
    public void NewConnection_SplitsLargestRemainingSegmentInHalf()
    {
        var planner = Planner(100, (0, 0, 1000));
        var first = planner.Acquire(0)!;
        planner.Commit(first, 100);

        var second = planner.Acquire(1)!;

        Assert.Equal(550, second.Start);
        Assert.Equal(1000, second.End);
        Assert.Equal(550, first.End);
    }

    [Fact]
    public void DoesNotSplit_WhenHalvesWouldBeSmallerThanMinimum()
    {
        var planner = Planner(500, (0, 0, 999));
        planner.Acquire(0);

        Assert.Null(planner.Acquire(1));
    }

    [Fact]
    public void FreeSegment_IsTakenOverBeforeSplitting()
    {
        var planner = Planner(10, (0, 0, 500), (500, 600, 1000));
        var first = planner.Acquire(0)!;
        var second = planner.Acquire(1)!;

        Assert.Equal(0, first.Start);
        Assert.Equal(500, second.Start);
        Assert.Equal(600, second.Position);
    }

    [Fact]
    public void Reserve_StopsAtShrunkenEnd()
    {
        var planner = Planner(10, (0, 0, 1000));
        var first = planner.Acquire(0)!;
        planner.Acquire(1);

        Assert.Equal(500, first.End);
        planner.Commit(first, planner.Reserve(first, 450));
        Assert.Equal(50, planner.Reserve(first, 450));
        planner.Commit(first, 50);
        Assert.Equal(0, planner.Reserve(first, 450));
    }

    [Fact]
    public void Snapshot_MergesCompletedNeighbours()
    {
        var planner = Planner(10, (0, 300, 300), (300, 450, 600), (600, 600, 1000));

        var snapshot = planner.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal((0L, 450L, 600L), (snapshot[0].Start, snapshot[0].Position, snapshot[0].End));
        Assert.Equal((600L, 600L, 1000L), (snapshot[1].Start, snapshot[1].Position, snapshot[1].End));
    }

    [Fact]
    public void ResumedPartialSegments_NeedVerification()
    {
        var planner = new SegmentPlanner(
            [new SegmentState { Start = 0, Position = 100, End = 1000 }, new SegmentState { Start = 1000, Position = 1000, End = 2000 }],
            10, canSplit: true, resumed: true);

        Assert.True(planner.Acquire(0)!.NeedsVerify);
        Assert.False(planner.Acquire(1)!.NeedsVerify);
    }
}
