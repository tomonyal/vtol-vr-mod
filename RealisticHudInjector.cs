using UnityEngine;

namespace RealisticHUD.Main;

/// <summary>
/// Watches for the player's aircraft to be ready and attaches a <see cref="RealisticHudOverlay"/>
/// onto its collimated HUD. Re-scans whenever the previous vehicle/overlay disappears
/// (scene reload, vehicle respawn, returning to briefing, etc).
/// </summary>
public class RealisticHudInjector : MonoBehaviour
{
    private RealisticHudOverlay currentOverlay;
    private Actor currentActor;

    private void OnEnable()
    {
        InvokeRepeating(nameof(ScanForPlayer), 0.5f, 1f);
    }

    private void OnDisable()
    {
        CancelInvoke(nameof(ScanForPlayer));
    }

    private void ScanForPlayer()
    {
        if (currentActor == null || currentOverlay == null)
        {
            currentActor = null;
            currentOverlay = null;
        }
        else
        {
            return; // already attached and alive
        }

        FlightSceneManager fsm = FlightSceneManager.instance;
        if (fsm == null)
        {
            return;
        }

        Actor player = fsm.playerActor;
        if (player == null || player.flightInfo == null)
        {
            return;
        }

        CollimatedHUDUI hud = player.GetComponentInChildren<CollimatedHUDUI>(true);
        if (hud == null)
        {
            return; // this vehicle has no VR collimated HUD (or its HUD isn't built yet)
        }

        currentActor = player;
        // Attach to this injector's own GameObject (persistent, always active) rather than
        // the CollimatedHud object itself, which is briefly inactive during cockpit fade-in
        // and would silently prevent StartCoroutine from ever running.
        currentOverlay = gameObject.AddComponent<RealisticHudOverlay>();
        currentOverlay.Initialize(player, hud.transform);
        Log($"Realistic HUD attached to {player.actorName}");
    }

    public void Cleanup()
    {
        if (currentOverlay != null)
        {
            Destroy(currentOverlay);
        }
        currentOverlay = null;
        currentActor = null;
    }
}
