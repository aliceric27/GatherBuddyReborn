using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons.DalamudServices;
using System;
using System.Linq;

namespace GatherBuddy.AutoGather.Helpers;

public class PlayerTargetTracker
{
    private DateTime? _targetedSince = null;

    public bool IsBeingTargetedByPlayer()
    {
        var localPlayer = Svc.ClientState.LocalPlayer;
        if (localPlayer == null) return false;

        return Svc.Objects
            .OfType<IPlayerCharacter>()
            .Any(p => p.GameObjectId != localPlayer.GameObjectId &&
                      p.TargetObjectId == localPlayer.GameObjectId);
    }

    public bool ShouldEvade(int thresholdSeconds)
    {
        if (IsBeingTargetedByPlayer())
        {
            _targetedSince ??= DateTime.UtcNow;
            return (DateTime.UtcNow - _targetedSince.Value).TotalSeconds >= thresholdSeconds;
        }
        else
        {
            _targetedSince = null;
            return false;
        }
    }

    public void Reset() => _targetedSince = null;
}
