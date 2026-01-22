using System;
using System.Numerics;

namespace GatherBuddy.AutoGather.Helpers;

public class PositionStuckTracker
{
    private Vector3? _startPosition    = null;
    private DateTime? _enteredRangeAt  = null;
    private bool _isTracking           = false;
    private bool _isInRange            = false;

    public void StartTracking(Vector3 position)
    {
        _startPosition = position;
        _enteredRangeAt = DateTime.UtcNow;
        _isTracking = true;
        _isInRange  = true;
        GatherBuddy.Log.Verbose($"位置防卡死追蹤開始: {position}");
    }

    public void Reset()
    {
        _startPosition = null;
        _enteredRangeAt = null;
        _isTracking = false;
        _isInRange  = false;
    }

    public bool ShouldTrigger(Vector3 currentPosition, float radius, int timeSeconds)
    {
        UpdateRangeState(currentPosition, radius);
        return ShouldTrigger(timeSeconds);
    }

    /// <summary>
    /// Updates whether the player is currently within the tracked range.
    /// Call this every tick so GetTimeInRange() stays accurate.
    /// </summary>
    public void UpdateRangeState(Vector3 currentPosition, float radius)
    {
        if (!_isTracking || _startPosition == null)
            return;

        var distance = Vector3.Distance(currentPosition, _startPosition.Value);
        if (distance > radius)
        {
            _isInRange = false;
            return;
        }

        if (!_isInRange)
        {
            _isInRange      = true;
            _enteredRangeAt = DateTime.UtcNow;
        }
        else if (_enteredRangeAt == null)
        {
            _enteredRangeAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Returns true if the player has been continuously within the range for at least timeSeconds.
    /// Requires UpdateRangeState to be called periodically to keep _isInRange accurate.
    /// </summary>
    public bool ShouldTrigger(int timeSeconds)
        => _isTracking && _isInRange && GetTimeInRange() >= timeSeconds;

    public double GetTimeInRange()
    {
        if (!_isTracking || !_isInRange || _enteredRangeAt == null)
            return 0;

        return (DateTime.UtcNow - _enteredRangeAt.Value).TotalSeconds;
    }

    public bool IsTracking => _isTracking;
}
