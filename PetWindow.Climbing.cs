using System.Windows;
using HanekawaPet.Platform;

namespace HanekawaPet;

public partial class PetWindow
{
    private const double EdgeDistance = 12;
    private int _climbSide;
    private int _edgeVisitSide;
    private double _climbTargetTop;
    private double _climbBlockedUntil;
    private double _renderedRotation;

    private bool ClimbBlockedByWindow => _chatWindow is { IsVisible: true } ||
        _speechBubble is { IsVisible: true } || _settingsWindow is { IsVisible: true } || _aiSettingsWindow is { IsVisible: true } || _gachaWindow is { IsVisible: true };

    private void CancelClimb(double now)
    {
        if (_climbSide == 0) return;
        _climbSide = 0;
        _climbBlockedUntil = now + 1500;
        SelectState("idle", true);
    }

    private bool TryStartClimb(double now, Rect area, double? chanceSample = null, double? distanceSample = null)
    {
        if (now < _climbBlockedUntil || area.Height <= Height + 2 || area.Width <= Width + 2 || Top <= area.Top + 1) return false;
        var left = Left - area.Left;
        var right = area.Right - Left - Width;
        var side = left <= EdgeDistance && left <= right ? -1 : right <= EdgeDistance ? 1 : 0;
        if (side == 0 || side == _edgeVisitSide || !_pack.Clips.ContainsKey(side < 0 ? "climb-left" : "climb-right")) return false;
        // Decide once per approach; staying beside the edge must not reroll every frame.
        _edgeVisitSide = side;
        if ((chanceSample ?? _random.NextDouble()) >= .5) return false;
        _climbSide = side;
        _climbTargetTop = Math.Max(area.Top, Top - (80 + 160 * (distanceSample ?? _random.NextDouble())));
        Left = side < 0 ? area.Left : area.Right - Width;
        return true;
    }

    private bool AdvanceClimb(double now, double seconds, bool allowed, double? chanceSample, double? distanceSample)
    {
        var area = DesktopPlatform.WorkArea(this);
        if (_climbSide == 0 && Left - area.Left > EdgeDistance + 16 && area.Right - Left - Width > EdgeDistance + 16) _edgeVisitSide = 0;
        if (!allowed) { CancelClimb(now); return false; }
        if (_climbSide == 0 && !TryStartClimb(now, area, chanceSample, distanceSample)) return false;
        Left = _climbSide < 0 ? area.Left : area.Right - Width;
        var target = Math.Max(area.Top, _climbTargetTop);
        Top = Math.Max(target, Top - Math.Clamp(_settings.Speed, 12, 60) * seconds);
        if (Top > target + .01) return true;

        // Stop after the chosen distance, retaining this visit so it cannot immediately restart.
        var side = _climbSide;
        _climbSide = 0;
        _climbBlockedUntil = now + 8000;
        _direction = -side;
        _autoWalking = false;
        _autoUntil = now + 3000;
        _routine.KnockPlayed(now);
        return false;
    }
}
