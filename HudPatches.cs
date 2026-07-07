using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace RealisticHUD.Main;

/// <summary>
/// The stock flight path marker (<see cref="HUDVelVector"/>) only repositions itself in
/// FixedUpdate - the physics tick, which runs at a lower/uneven rate than the VR render
/// framerate - making it look laggy relative to the rest of the head-tracked HUD. We take over
/// its positioning every rendered frame instead (see <see cref="FlightPathMarker"/>).
///
/// The pitch ladder (<see cref="HUDLadder"/>) shifts itself sideways every LateUpdate to stay
/// centered on the FPM's current position (reading vVectorTransform.position). Unity does not
/// guarantee which of two unrelated components' LateUpdate runs first, so simply moving the FPM
/// update into our own component's LateUpdate left the ladder reading a stale, previous-frame
/// FPM position - the ladder tracked attitude fine, it just always lagged one frame behind the
/// FPM. Running our FPM update as a Prefix on HUDLadder.LateUpdate guarantees it happens
/// immediately before the ladder reads it, so both move in lock-step.
/// </summary>
[HarmonyPatch(typeof(HUDVelVector), "FixedUpdate")]
internal static class HUDVelVector_FixedUpdate_Patch
{
    private static bool Prefix()
    {
        return false;
    }
}

/// <summary>
/// EEGS-style gun mode: the real F-16 gun sight keeps the funnel displayed while a target is
/// locked (funnel + target circle + pipper together), but stock VTOL VR turns the funnel off
/// the moment a target is acquired. Re-enable it right after the sight's target update. Only
/// when it isn't already active - SetActive restarts the funnel's drawing coroutine, so calling
/// it every physics tick would break the funnel. The actor-lock branch only disables the funnel
/// once on acquisition (not per-frame), so this doesn't fight the stock code; the ground-point
/// fallback branch (which does disable per-frame) leaves targetActor null and is excluded.
/// </summary>
[HarmonyPatch(typeof(HUDGunDirectorSight), "UpdateTarget")]
internal static class HUDGunDirectorSight_UpdateTarget_Patch
{
    private static void Postfix(HUDGunDirectorSight __instance)
    {
        if (__instance.allowGunFunnel
            && __instance.gunFunnel != null
            && __instance.isTargetLocked
            && __instance.targetActor != null
            && !__instance.gunFunnel.isActive)
        {
            __instance.gunFunnel.SetActive(true);
        }
    }
}

[HarmonyPatch(typeof(HUDLadder), "LateUpdate")]
internal static class HUDLadder_LateUpdate_Patch
{
    private class FpmRefs
    {
        public HUDVelVector VelVector;
        public Image Image;
        public float Depth;
    }

    private static readonly ConditionalWeakTable<HUDLadder, FpmRefs> Cache = new();

    private static void Prefix(HUDLadder __instance)
    {
        FpmRefs refs = Cache.GetValue(__instance, ResolveFpmRefs);
        if (refs.VelVector == null || refs.Image == null)
        {
            return;
        }

        FlightPathMarker.UpdatePosition(refs.VelVector, refs.Image, refs.Depth);
    }

    private static FpmRefs ResolveFpmRefs(HUDLadder ladder)
    {
        FpmRefs refs = new FpmRefs();
        Transform vv = ladder.vVectorTransform;
        if (vv == null)
        {
            return refs;
        }

        refs.VelVector = vv.GetComponent<HUDVelVector>();
        refs.Image = vv.GetComponent<Image>();
        CollimatedHUDUI hud = vv.GetComponentInParent<CollimatedHUDUI>();
        refs.Depth = hud != null ? hud.depth : 1000f;
        return refs;
    }
}

/// <summary>
/// Reimplementation of <see cref="HUDVelVector"/>'s FixedUpdate logic, unchanged except for
/// running from <see cref="HUDLadder_LateUpdate_Patch"/> instead of the physics tick.
/// </summary>
internal static class FlightPathMarker
{
    public static void UpdatePosition(HUDVelVector velVector, Image fpmImage, float depth)
    {
        FlightInfo flightInfo = velVector.flightInfo;
        if (flightInfo == null || flightInfo.rb == null)
        {
            return;
        }

        Transform velTf = velVector.transform;
        Transform ghostTf = velVector.ghostVelTf;

        if (flightInfo.airspeed <= 5f)
        {
            if (fpmImage.enabled)
            {
                fpmImage.enabled = false;
                velTf.localPosition = Vector3.zero;
            }
            if (ghostTf != null && ghostTf.gameObject.activeSelf)
            {
                ghostTf.gameObject.SetActive(false);
            }
            return;
        }

        Transform parent = velTf.parent;

        if (flightInfo.windSpeed > 3f && ghostTf != null)
        {
            Ray ray = new Ray(VRHead.position, -flightInfo.relativeWind);
            velTf.position = ray.GetPoint(depth);
            velTf.rotation = Quaternion.LookRotation(ray.direction, parent.up);
            fpmImage.enabled = true;

            if (flightInfo.surfaceSpeed > 5f)
            {
                Ray groundRay = new Ray(VRHead.position, flightInfo.rb.velocity);
                ghostTf.position = groundRay.GetPoint(depth);
                ghostTf.rotation = Quaternion.LookRotation(groundRay.direction, parent.up);
                if (!ghostTf.gameObject.activeSelf)
                {
                    ghostTf.gameObject.SetActive(true);
                }
            }
            else if (ghostTf.gameObject.activeSelf)
            {
                ghostTf.gameObject.SetActive(false);
            }
        }
        else
        {
            Ray ray = new Ray(VRHead.position, flightInfo.rb.velocity);
            velTf.position = ray.GetPoint(depth);
            velTf.rotation = Quaternion.LookRotation(ray.direction, parent.up);
            fpmImage.enabled = true;

            if (ghostTf != null && ghostTf.gameObject.activeSelf)
            {
                ghostTf.gameObject.SetActive(false);
            }
        }
    }
}
