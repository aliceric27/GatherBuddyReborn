using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons.DalamudServices;
using System;
using System.Linq;

namespace GatherBuddy.AutoGather.Helpers;

public class PlayerTargetTracker
{
    private DateTime? _targetedSince = null;
    private bool _evasionPending = false;
    private bool _evasionInProgress = false;

    /// <summary>
    /// 是否有其他玩家正在選中本地玩家
    /// </summary>
    public bool IsBeingTargetedByPlayer()
    {
        var localPlayer = Svc.ClientState.LocalPlayer;
        if (localPlayer == null) return false;

        return Svc.Objects
            .OfType<IPlayerCharacter>()
            .Any(p => p.GameObjectId != localPlayer.GameObjectId &&
                      p.TargetObjectId == localPlayer.GameObjectId);
    }

    /// <summary>
    /// 檢查是否應該躲避。一旦達到閾值會 latch 住，直到 Reset() 被呼叫。
    /// </summary>
    public bool ShouldEvade(int thresholdSeconds)
    {
        // 如果已經觸發躲避，保持 pending 狀態直到完成
        if (_evasionPending)
            return true;

        if (IsBeingTargetedByPlayer())
        {
            _targetedSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _targetedSince.Value).TotalSeconds >= thresholdSeconds)
            {
                _evasionPending = true;
                return true;
            }
        }
        else
        {
            _targetedSince = null;
        }

        return false;
    }

    /// <summary>
    /// 躲避是否正在等待執行（已達閾值但尚未完成躲避動作）
    /// </summary>
    public bool IsEvasionPending => _evasionPending;

    /// <summary>
    /// 躲避流程是否正在進行中（已開始傳送，防止重入）
    /// </summary>
    public bool IsEvasionInProgress => _evasionInProgress;

    /// <summary>
    /// 標記躲避流程已開始執行（防止重入）
    /// </summary>
    public void MarkEvasionStarted() => _evasionInProgress = true;

    /// <summary>
    /// 重置所有狀態
    /// </summary>
    public void Reset()
    {
        _targetedSince = null;
        _evasionPending = false;
        _evasionInProgress = false;
    }
}
