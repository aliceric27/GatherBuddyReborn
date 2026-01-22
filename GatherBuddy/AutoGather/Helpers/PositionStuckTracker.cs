using System;
using System.Numerics;

namespace GatherBuddy.AutoGather.Helpers;

public class PositionStuckTracker
{
    private Vector3? _startPosition = null;
    private DateTime? _stuckStartTime = null;
    private bool _isTracking = false;

    public void StartTracking(Vector3 position)
    {
        _startPosition = position;
        _stuckStartTime = DateTime.UtcNow;
        _isTracking = true;
        GatherBuddy.Log.Verbose($"位置防卡死追蹤開始: {position}");
    }

    public void Reset()
    {
        _startPosition = null;
        _stuckStartTime = null;
        _isTracking = false;
    }

    public bool ShouldTrigger(Vector3 currentPosition, float radius, int timeSeconds)
    {
        if (!_isTracking || _startPosition == null || _stuckStartTime == null)
            return false;

        float distance = Vector3.Distance(currentPosition, _startPosition.Value);

        if (distance > radius)
        {
            _stuckStartTime = DateTime.UtcNow;
            return false;
        }

        var elapsedSeconds = (DateTime.UtcNow - _stuckStartTime.Value).TotalSeconds;
        return elapsedSeconds >= timeSeconds;
    }

    public double GetTimeInRange()
    {
        if (_stuckStartTime == null)
            return 0;
        return (DateTime.UtcNow - _stuckStartTime.Value).TotalSeconds;
    }

    public bool IsTracking => _isTracking;
}
