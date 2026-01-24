using System;
using System.Numerics;
using ECommons.GameHelpers;
using GatherBuddy.AutoGather.Movement;
using static GatherBuddy.AutoGather.AutoGatherConfig;

namespace GatherBuddy.AutoGather.Helpers;

public enum AntiStuckState
{
    Normal,
    EscalationArmed,
    DrasticActionReady,
    Cooldown
}

public sealed class AntiStuckManager : IDisposable
{
    private readonly AdvancedUnstuck _advancedUnstuck;
    private readonly PositionStuckTracker _areaTracker = new();
    
    private AntiStuckState _state = AntiStuckState.Normal;
    private int _consecutiveAdvancedFails;
    private DateTime _lastDrasticActionAt = DateTime.MinValue;
    private int _drasticActionsThisSession;
    private Vector3 _currentDestination;
    private bool _isGathering;
    private bool _autoGatherEnabled;
    private DateTime _lastEscalationCheckAt = DateTime.MinValue;
    private AdvancedUnstuckCheckResult _lastAdvancedResult = AdvancedUnstuckCheckResult.Pass;

    public AntiStuckManager(AdvancedUnstuck advancedUnstuck)
    {
        _advancedUnstuck = advancedUnstuck;
    }

    private AntiStuckConfig Config => GatherBuddy.Config.AutoGatherConfig.AntiStuck;

    public AntiStuckState State => _state;
    public int ConsecutiveFails => _consecutiveAdvancedFails;
    public double TimeInArea => _areaTracker.GetTimeInRange();
    public bool IsAreaTracking => _areaTracker.IsTracking && !_isGathering;
    public int DrasticActionsThisSession => _drasticActionsThisSession;
    public double CooldownRemaining => _state == AntiStuckState.Cooldown 
        ? Math.Max(0, Config.DrasticCooldownSeconds - (DateTime.UtcNow - _lastDrasticActionAt).TotalSeconds) 
        : 0;

    public void SetDestination(Vector3 destination)
    {
        var distance = Vector3.Distance(_currentDestination, destination);
        if (distance > 0.5f)
        {
            _currentDestination = destination;
            if (destination != default)
            {
                ResetAreaTracking("destination changed");
                if (_state == AntiStuckState.EscalationArmed && _autoGatherEnabled && Config.EscalationEnabled && !_isGathering)
                    _areaTracker.StartTracking(Player.Position);
            }
        }
    }

    public void OnAutoGatherEnabledChanged(bool enabled)
    {
        if (_autoGatherEnabled == enabled)
            return;

        _autoGatherEnabled = enabled;

        if (!enabled)
        {
            // AutoGather 被停用（包含排程停用）：立即停止/重置追蹤，避免背景累積時間。
            if (_state != AntiStuckState.Normal || _areaTracker.IsTracking)
            {
                _state = AntiStuckState.Normal;
                ResetAreaTracking("auto gather disabled");
            }
            return;
        }

        // AutoGather 剛啟用：若有目的地且允許升級策略，直接開始區域停滯倒數。
        if (Config.Enabled && Config.EscalationEnabled && _currentDestination != default && !_isGathering)
        {
            _state = AntiStuckState.EscalationArmed;
            ResetAreaTracking("auto gather enabled");
            _areaTracker.StartTracking(Player.Position);
        }
    }

    public void OnGatheringStateChanged(bool isGathering)
    {
        if (_isGathering != isGathering)
        {
            _isGathering = isGathering;
            if (isGathering)
            {
                GatherBuddy.Log.Verbose("AntiStuck: 採集中，暫停區域追蹤");
            }
            else
            {
                if (_autoGatherEnabled && Config.Enabled && Config.EscalationEnabled && _currentDestination != default)
                {
                    _state = AntiStuckState.EscalationArmed;
                    _areaTracker.StartTracking(Player.Position);
                }
                GatherBuddy.Log.Verbose("AntiStuck: 採集結束，重新開始區域追蹤");
            }
        }
    }

    public void OnSessionStart()
    {
        _drasticActionsThisSession = 0;
        Reset("session start");
    }

    public void Reset(string reason)
    {
        _consecutiveAdvancedFails = 0;
        _state = AntiStuckState.Normal;
        _lastAdvancedResult = AdvancedUnstuckCheckResult.Pass;
        _lastEscalationCheckAt = DateTime.MinValue;
        ResetAreaTracking(reason);
    }

    private void ResetAreaTracking(string reason)
    {
        _areaTracker.Reset();
        GatherBuddy.Log.Verbose($"AntiStuck: 區域追蹤重置 ({reason})");
    }

    public AdvancedUnstuckCheckResult Tick(bool isPathGenerating, bool isPathing)
    {
        if (!Config.Enabled)
        {
            if (_state != AntiStuckState.Normal || _areaTracker.IsTracking)
                Reset("anti-stuck disabled");
            return AdvancedUnstuckCheckResult.Pass;
        }

        UpdateAreaTracking();
        UpdateState();

        if (!Config.LocalRecoveryEnabled)
            return AdvancedUnstuckCheckResult.Pass;

        var result = _advancedUnstuck.Check(_currentDestination, isPathGenerating, isPathing);
        
        ProcessAdvancedUnstuckResult(result);

        return result;
    }

    private void UpdateAreaTracking()
    {
        if (_isGathering || _currentDestination == default)
            return;

        if (!_areaTracker.IsTracking)
            return;

        _areaTracker.UpdateRangeState(Player.Position, Config.AreaRadius);
    }

    private void ProcessAdvancedUnstuckResult(AdvancedUnstuckCheckResult result)
    {
        if (result == AdvancedUnstuckCheckResult.Fail && _lastAdvancedResult != AdvancedUnstuckCheckResult.Fail)
        {
            _consecutiveAdvancedFails++;
            GatherBuddy.Log.Verbose($"AntiStuck: 近端復原失敗，連續失敗次數: {_consecutiveAdvancedFails}");
        }
        else if (result == AdvancedUnstuckCheckResult.Pass && _lastAdvancedResult != AdvancedUnstuckCheckResult.Pass && _consecutiveAdvancedFails > 0)
        {
            _consecutiveAdvancedFails--;
        }
        
        _lastAdvancedResult = result;
    }

    private void UpdateState()
    {
        if (_state == AntiStuckState.Cooldown)
        {
            if (CooldownRemaining <= 0)
            {
                _state = AntiStuckState.Normal;
                GatherBuddy.Log.Verbose("AntiStuck: 冷卻結束，回到正常狀態");
            }
            return;
        }

        if (!_autoGatherEnabled || !Config.EscalationEnabled)
        {
            if (_state != AntiStuckState.Normal)
            {
                _state = AntiStuckState.Normal;
                ResetAreaTracking(_autoGatherEnabled ? "escalation disabled" : "auto gather disabled");
            }
            return;
        }

        if (_state == AntiStuckState.DrasticActionReady)
        {
            if (_currentDestination == default)
            {
                _state = AntiStuckState.Normal;
                ResetAreaTracking("ready state expired");
                GatherBuddy.Log.Verbose("AntiStuck: 條件不再滿足，退出準備執行狀態");
            }
            return;
        }

        if (_state == AntiStuckState.EscalationArmed)
        {
            if (_currentDestination == default)
            {
                _state = AntiStuckState.Normal;
                ResetAreaTracking("recovered from armed state");
                GatherBuddy.Log.Verbose("AntiStuck: 狀況改善，退出升級待命狀態");
                return;
            }

            if (_isGathering)
                return;

            // 每秒做一次「是否該升級」檢查；RangeState 仍維持每 tick 更新。
            if ((DateTime.UtcNow - _lastEscalationCheckAt).TotalSeconds < 1)
                return;
            _lastEscalationCheckAt = DateTime.UtcNow;

            if (_areaTracker.ShouldTrigger(Config.AreaTimeSeconds))
            {
                _state = AntiStuckState.DrasticActionReady;
                GatherBuddy.Log.Warning($"AntiStuck: 區域停滯 {_areaTracker.GetTimeInRange():F1} 秒，準備執行強制措施");
            }
            return;
        }

        if (_state == AntiStuckState.Normal && _currentDestination != default && !_isGathering)
        {
            _state = AntiStuckState.EscalationArmed;
            ResetAreaTracking("entering armed state");
            _areaTracker.StartTracking(Player.Position);
            GatherBuddy.Log.Warning("AntiStuck: 自動採集啟用，開始區域停滯倒數");
        }
    }

    public bool ShouldExecuteDrasticAction()
    {
        if (!Config.Enabled || !Config.EscalationEnabled)
        {
            if (_state == AntiStuckState.DrasticActionReady || _state == AntiStuckState.EscalationArmed)
            {
                _state = AntiStuckState.Normal;
                ResetAreaTracking("anti-stuck disabled");
            }
            return false;
        }

        if (_state != AntiStuckState.DrasticActionReady)
            return false;

        if (_currentDestination == default)
        {
            _state = AntiStuckState.Normal;
            ResetAreaTracking("destination cleared");
            return false;
        }

        if (Config.DrasticAction == PositionUnstuckAction.Off)
        {
            _state = AntiStuckState.Normal;
            ResetAreaTracking("drastic action disabled");
            return false;
        }

        if (_drasticActionsThisSession >= Config.MaxDrasticPerSession)
        {
            GatherBuddy.Log.Warning($"AntiStuck: 本次採集已執行 {_drasticActionsThisSession} 次強制措施，達到上限");
            _state = AntiStuckState.Cooldown;
            _lastDrasticActionAt = DateTime.UtcNow;
            return false;
        }

        return true;
    }

    public void MarkDrasticActionExecuted()
    {
        _lastDrasticActionAt = DateTime.UtcNow;
        _drasticActionsThisSession++;
        _state = AntiStuckState.Cooldown;
        _consecutiveAdvancedFails = 0;
        _lastAdvancedResult = AdvancedUnstuckCheckResult.Pass;
        _areaTracker.Reset();
        GatherBuddy.Log.Information($"AntiStuck: 強制措施已執行，進入冷卻 ({Config.DrasticCooldownSeconds} 秒)");
    }

    public PositionUnstuckAction GetDrasticAction() => Config.DrasticAction;

    public void Dispose()
    {
        _areaTracker.Reset();
    }
}
