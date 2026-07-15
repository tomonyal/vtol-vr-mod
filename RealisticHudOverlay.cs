using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace RealisticHUD.Main;

/// <summary>
/// Adds real-jet style digital readouts to a single aircraft's existing VR HUD:
/// Mach number, a digital radar altitude readout, a master-mode annunciation
/// derived from the selected weapon, and a steerpoint bearing/range/time-to-go line.
/// The text objects are parented under that aircraft's <see cref="CollimatedHUDUI"/> so they
/// inherit the same infinity-focus depth/scale as the rest of the HUD, but this component
/// itself lives on the mod's own persistent (always-active) GameObject: the CollimatedHud
/// object is briefly inactive during cockpit fade-in, and Unity refuses to start a coroutine
/// on an inactive GameObject, which silently killed the whole update loop when this script
/// was attached there directly.
/// </summary>
public class RealisticHudOverlay : MonoBehaviour
{
    private static readonly Color HudGreen = new Color(0.1f, 1f, 0.2f, 0.95f);
    private static readonly StringBuilder sb = new StringBuilder();

    private Actor actor;
    private FlightInfo flightInfo;
    private WeaponManager weaponManager;
    private MeasurementManager measurements;
    private Transform hudTransform;

    private Text masterModeText;
    private Text flightDataText;

    // Bottom-right nav data block, modeled on the real HUD's stacked data lines (radar alt,
    // steerpoint, bearing/range, time-to-go, clock in one right-aligned column) instead of
    // scattering them as individual readouts.
    private Text dataBlockText;

    // Speed tape: a column of tick labels scrolling past the aircraft's own airspeed box.
    // Real HUD speed tapes label ticks with the last digit dropped (e.g. "45" means 450 kt) -
    // see the reference HUD doc's "Airspeed Scale" section - so we do the same here.
    // Absolute HUD coordinates proved unreliable across aircraft (the tape kept landing on top
    // of other readouts), so the ticks are parented into the stock airspeed box's own canvas
    // space and positioned relative to it, inheriting its exact scale.
    private const int SpeedTickCount = 3;
    private const float SpeedTickInterval = 20f; // knots (or whatever unit MeasurementManager is set to)
    private Text[] speedTicks;
    private RectTransform speedTapeLine;
    private Vector2 speedTapeBasePos;
    private float speedTapePixelsPerUnit;

    // Roll (bank) indicator: fixed tick arc at the bottom of the HUD (0/±10/±20/±30/±45 deg)
    // with a caret pointer that rotates with flightInfo.roll, like the real jet's bank scale.
    // Serves as the always-on replacement for the attitude arc that stock VTOL VR only shows
    // on the HMCS visor.
    private const float RollArcRadius = 145f;
    // Reference HUDs show the pointer sitting clearly outside/below the tick arc (ticks read as
    // the "sky" side, pointer below them), not inside it.
    private const float RollPointerRadius = 165f;
    private Transform rollScaleRoot;
    private RectTransform rollPointer;

    // Gun-mode-only sustained-turn performance circle (BFM "turn circle"): radius shrinks with
    // bank angle per the standard coordinated-turn formula r = v^2 / (g * tan(bank)), and the
    // whole ring rotates to stay aligned with the current bank plane - so it visibly tilts as
    // you roll, same as the reference footage. Not present on stock VTOL VR's HUD at all.
    private const int TurnCircleSegmentCount = 32;
    private const float TurnCircleScale = 0.12f;
    private const float TurnCircleMinRadius = 40f;
    private const float TurnCircleMaxRadius = 220f;
    private const float TurnCircleMinBankDeg = 8f;
    // Half circle, not a closed ring or a 3/4 ring - the flat (undrawn) side of the diameter
    // is the horizontal reference, and the dome above it tilts with bank to show the turn
    // plane. i=0 sits at 0 degrees (3 o'clock) and sweeps counterclockwise to 180 (9 o'clock),
    // i.e. straight across the horizontal diameter and over the top - not an arbitrary cut.
    private const float TurnCircleArcDegrees = 180f;
    private Transform turnCircleRoot;
    private Image[] turnCircleSegments;
    private Image turnCircleLeftCap;
    private Image turnCircleRightCap;

    // The stock fixed (non-conformal) HUD readouts spill past the combiner glass on some
    // aircraft; shrinking their shared static parent pulls them inward. Conformal elements
    // (pitch ladder, FPM) keep their calibrated scale - shrinking those would break their
    // real-world angular alignment.
    private const float StaticHudShrink = 0.85f;

    // Material shared by every Image this mod creates (rings, needles, ticks, connecting
    // lines): the combiner glass apparently only renders geometry using the stock HUD's own
    // shader/material - plain Images left on Unity's default UI material were invisible through
    // the glass (though visible via the visor, which doesn't have that restriction). Resolved
    // once from any existing Image already inside this aircraft's collimated HUD hierarchy.
    private Material hudImageMaterial;

    // ILS localizer/glideslope cross-pointer needles. Reuses the same deviation math as the
    // aircraft's own dash-mounted HSI (DashHSI), just rendered as HUD needles instead of on the
    // 2D instrument, and only shown once the pilot has actually tuned a valid ILS frequency.
    private const float IlsCenterX = 0f;
    private const float IlsCenterY = -90f;
    private const float IlsMaxOffsetPx = 60f;
    private DashHSI dashHSI;
    private Image locNeedle;
    private Image gsNeedle;
    private Text ilsFreqLabel;

    // Font/material/color copied from an existing stock HUD readout so our added text
    // renders through the same shader (glow, depth blend, HUD tint/brightness knob, etc.)
    // instead of looking like a plain Unity UI Text pasted on top.
    private Text styleTemplate;

    // Stock flight path marker (see HudPatches.cs): its own FixedUpdate is patched out. Aircraft
    // whose pitch ladder is a HUDLadder get it refreshed right before the ladder reads its
    // position (see HUDLadder_LateUpdate_Patch); other aircraft (e.g. ones using
    // HUDElevationLadder, which never reads the FPM at all) would otherwise never update it
    // again, so it's also driven from here every frame as a universal fallback.
    private HUDVelVector velVector;
    private Image fpmImage;
    private float hudDepth;

    // Stock declutter system (cockpit DECL button): stepping it to its most-decluttered level
    // hides whatever stock sub-labels/units that aircraft's HUD prefab has wired to it, on top
    // of the elements we hide ourselves in UpdateDeclutter.
    private HUDDeclutter hudDeclutter;
    private int savedDeclutterLevel;
    private bool declutterOverridden;

    // Pitch ladder simplification for gun mode: the reference EEGS HUD shows only a single
    // dashed horizon reference line, not the full numbered climb/descent rung ladder. Each rung
    // is a direct child of ladderTransform (instantiated straight off climbTemplate/
    // descentTemplate in HUDElevationLadder.ConstructLadder) - hide all of those except
    // horizonTransform itself while gun mode is active.
    private HUDElevationLadder elevationLadder;
    private List<GameObject> ladderRungObjects;
    private int lastLadderChildCount = -1;
    private int ladderStableTicks;

    // Speed tape creation is also deferred/retried (see TryCreateSpeedTape) rather than done
    // once in Initialize, for the same reason: GetComponentsInChildren<HUDVelocity> came back
    // completely empty in one test session and correct in another, from the same injector
    // timing (0.5-1.5s after CollimatedHUDUI was found) - the cockpit HUD hierarchy isn't
    // guaranteed fully built by then. Keep trying every tick until it succeeds.
    private bool speedTapeReady;

    // Gun sight restyle: the actual aim-point math in HUDGunDirectorSight is untouched (it's the
    // real ballistic solution), we only add a dashed-ring pipper and boresight cross as children
    // of its existing tracked transforms and hide whatever stock sprite was there, purely
    // cosmetic - see CreateGunSightDecoration. The aspect readout additionally reads the locked
    // target's own facing direction, shown as a number+arrow near the lock circle (the target's
    // own silhouette icon already shows aspect visually - a separate chevron marker just
    // cluttered the same spot, so this is a numeric "090>"-style readout instead).
    private HUDGunDirectorSight gunSight;
    private Text aspectText;

    // Pipper's ring/9G-dash/maneuver-line cluster - only meaningful with an actual lead solution,
    // so hidden until gunSight.isTargetLocked (see UpdateGunLock).
    private Transform pipperLockGroup;
    private bool pipperLockGroupShown = true;

    // Stock missile DLZ scale (Rmax/Rmin/no-escape ladder next to the locked target, drawn by
    // HUDDynamicLaunchZone): scaled up and shifted down since it renders quite small and high
    // on the HUD by default.
    private const float DlzScale = 1.6f;
    private const float DlzYOffset = -60f;

    // EEGS-style analog target range arc: a large ring centered on the HUD that unwinds
    // clockwise from 12 o'clock as the locked target gets closer; a full circle represents
    // 12,000 ft slant range (the same full-scale constant the stock rangeImage fill used).
    private const int RangeArcSegmentCount = 36;
    private const float RangeArcRadius = 150f;
    private const float RangeArcFullScaleFt = 12000f;
    private Image[] rangeArcSegments;
    private Text gunRangeText;

    // Stock radar STT lock icon (blinking square + rays, HUDRadarLock.iconObj): redundant with
    // our dashed target ring in gun mode and absent from the reference EEGS HUD, so its Image
    // components are disabled while gun mode is selected and restored on leaving it. The
    // component re-SetActives its GameObject every frame, so Images are the reliable switch.
    private Image[] radarLockIconImages;
    private bool radarLockIconHidden;

    // Stock "noseIndicator" (StaticHUDParent/noseIndicator, sprite hud_noseIndicator) - the
    // actual source of the persistent "W" mark, confirmed via a live sprite-name dump. Lives
    // outside HUDGunDirectorSight entirely (a static, always-on nose reference), which is why
    // none of the gunsight-specific fixes above ever reached it. Something stock re-enables it
    // on a target lock transition, racing our 0.15s coroutine tick - forced off in LateUpdate
    // (every frame) instead, see gunModeCached.
    private Image noseIndicatorImage;
    private bool gunModeCached;

    // Always-on boresight "gun cross" - reference HUDs show this fixed nose-reference symbol
    // regardless of selected weapon, not just while the gun is armed (unlike
    // HUDGunDirectorSight.fixedSightTransform, which only exists/updates in gun mode).
    private Transform boresightCross;

    // Waypoint diamond marker - independent of HUDWaypoint's own icon transform, which lives at
    // a Z depth far beyond hudDepth (see RestyleWaypointIndicator/CreateWaypointTadpole) and
    // collapsed an icon built on it down to a barely-visible dot.
    private Transform waypointTadpole;
    private GameObject stockWaypointIcon;

    // Great circle steering cue: small ring offset from the boresight cross showing which way
    // to turn/climb to get established on course to the current steerpoint. Reference footage
    // shows this ranging across roughly the same span as the pitch ladder, not staying tightly
    // clustered near the boresight - 40px was far too tight a leash.
    private const float SteeringCueMaxOffsetPx = 160f;
    // The ring itself carries the full direction+magnitude offset (so it visibly moves across
    // the whole range, like a real fly-to cue) - a short fixed-length stub extends further out
    // from the ring, past it, toward the true waypoint direction. Reading in from the waypoint
    // side you hit the stub first, then the ring, matching the reference layout.
    private const float SteeringCueLineStubPx = 15f;
    private Transform steeringCue;
    private RectTransform steeringLine;

    // Low-fuel caution: blinking "FUEL" center-HUD like the reference, driven by the
    // aircraft's own FlightWarnings bingo state (same source as the cockpit voice warning).
    private FlightWarnings flightWarnings;
    private Text fuelWarnText;

    // Fuel quantity readout ("SET"/bingo level and "TOTAL" remaining, in lbs) and the low
    // altitude warning ("AL") setting, both visible in the fully-labeled reference. Bearing/range
    // to bullseye (the shared tactical reference point, identified from the labeled reference
    // diagram as the mystery "142 022"-style number pair) is appended into flightDataText too -
    // these three all folded into one bottom-center block (see UpdateFlightData) rather than
    // three independently-positioned Text objects, which kept overlapping each other.
    private VehicleMaster vehicleMaster;
    private AltitudeWarning altitudeWarning;

    public void Initialize(Actor forActor, Transform hudRoot)
    {
        actor = forActor;
        flightInfo = actor.flightInfo;
        hudTransform = hudRoot;

        styleTemplate = actor.GetComponentInChildren<HUDGMeter>(true)?.gText
                        ?? actor.GetComponentInChildren<HUDAoAMeter>(true)?.text;
        dashHSI = actor.GetComponentInChildren<DashHSI>(true);
        velVector = actor.GetComponentInChildren<HUDVelVector>(true);
        fpmImage = velVector != null ? velVector.GetComponent<Image>() : null;
        CollimatedHUDUI hudUi = hudTransform.GetComponent<CollimatedHUDUI>();
        hudDepth = hudUi != null ? hudUi.depth : 1000f;
        hudDeclutter = actor.GetComponentInChildren<HUDDeclutter>(true);

        // Must run before any of this mod's own Image objects are created below, so it only
        // ever finds a genuine stock HUD Image to copy the material from.
        Image[] stockHudImages = hudTransform.GetComponentsInChildren<Image>(true);
        if (stockHudImages.Length > 0)
        {
            hudImageMaterial = stockHudImages[0].material;
        }

        elevationLadder = actor.GetComponentInChildren<HUDElevationLadder>(true);
        TryCacheLadderRungs();

        if (hudUi != null && hudUi.staticHudParent != null)
        {
            hudUi.staticHudParent.localScale *= StaticHudShrink;

            // "noseIndicator" (sprite hud_noseIndicator) - a static, always-on nose/waterline
            // reference mark parented outside HUDGunDirectorSight entirely, which is why none of
            // the gunsight-specific disabling above ever touched it. Confirmed live via a sprite
            // name dump (positioned dead center, just above the pipper) to be the persistent "W"
            // mark that kept surviving every other fix.
            noseIndicatorImage = hudUi.staticHudParent.Find("noseIndicator")?.GetComponent<Image>();
        }

        masterModeText = CreateText("RH_MasterMode", new Vector2(0f, 230f), TextAnchor.UpperCenter, sizeBoost: 2);
        flightDataText = CreateText("RH_FlightData", new Vector2(0f, -370f), TextAnchor.UpperCenter, sizeBoost: -6);
        dataBlockText = CreateText("RH_DataBlock", new Vector2(230f, -190f), TextAnchor.UpperRight, sizeBoost: -6);
        CreateRollIndicator();
        CreateTurnCircle();
        CreateIlsNeedles();
        CreateGunSightDecoration();
        RestyleWaypointIndicator();
        RestyleDlzScale();
        RestyleMgaBlock();
        CreateBoresightCross();
        CreateSteeringCue();

        HUDRadarLock radarLock = actor.GetComponentInChildren<HUDRadarLock>(true);
        if (radarLock != null && radarLock.iconObj != null)
        {
            radarLockIconImages = radarLock.iconObj.GetComponentsInChildren<Image>(true);
        }

        flightWarnings = actor.GetComponentInChildren<FlightWarnings>(true);
        fuelWarnText = CreateText("RH_FuelWarn", new Vector2(0f, -60f), TextAnchor.MiddleCenter, sizeBoost: 2);
        fuelWarnText.text = "FUEL";
        fuelWarnText.gameObject.SetActive(false);

        vehicleMaster = actor.GetComponentInChildren<VehicleMaster>(true);
        altitudeWarning = actor.GetComponentInChildren<AltitudeWarning>(true);

        StartCoroutine(UpdateRoutine());
    }

    private void CreateGunSightDecoration()
    {
        HUDGunDirectorSight gds = actor.GetComponentInChildren<HUDGunDirectorSight>(true);
        if (gds == null)
        {
            return;
        }

        // pipperTransform is the computed lead/aim point. Per the EEGS reference: a tiny "o" 1G
        // reticle, a short "-" 9G reticle further below that, and the maneuver-potential
        // horizontal lines crossing through the pipper. pipperTransform's own active state is
        // driven by stock funnel/gimbal/lock logic we don't control (and shouldn't fight, since
        // the funnel wedge itself is wanted) - when it IS active it's the real moving lead-
        // computed aim point (can be off-boresight), so it needs its own cross; boresightCross
        // (below) is hidden whenever pipperTransform is actually showing, to avoid duplicating.
        if (gds.pipperTransform != null)
        {
            // Recurse into children, not just the transform's own Image: the stock reticle art
            // is a compound sprite, split across child GameObjects - disabling only the direct
            // Image left the rest rendering behind.
            foreach (Image img in gds.pipperTransform.GetComponentsInChildren<Image>(true))
            {
                img.enabled = false;
            }
            BuildCross(gds.pipperTransform, 6f, 1.5f);

            // The 1G ring/9G dash/maneuver lines only mean anything once there's an actual lead
            // solution being computed against a locked target - grouped under one child so they
            // can be shown/hidden together based on gds.isTargetLocked (see UpdateGunLock).
            GameObject lockGroupGo = new GameObject("RH_PipperLockGroup");
            lockGroupGo.transform.SetParent(gds.pipperTransform, worldPositionStays: false);
            pipperLockGroup = lockGroupGo.transform;
            BuildSolidRing(pipperLockGroup, 4f, 8, new Vector2(0f, -26f));
            BuildNineGDash(pipperLockGroup);
            BuildManeuverPotentialLines(pipperLockGroup);
        }

        // fixedSightTransform: per the decompiled HUDGunDirectorSight.LateUpdate, this gets its
        // *world* .position set directly every frame (boresight direction extended out to the
        // target's range) regardless of its parent in the hierarchy - so despite appearing
        // nested under pipperTransform, it isn't actually dragged by the pipper's lead-driven
        // movement (a prior diagnosis based on the parenting alone was wrong). It's a distinct
        // reference point from the lead-compensated pipper, matching the reference's separate
        // "boresight" mark - keep decorating it.
        if (gds.fixedSightTransform != null)
        {
            foreach (Image img in gds.fixedSightTransform.GetComponentsInChildren<Image>(true))
            {
                img.enabled = false;
            }
            BuildCross(gds.fixedSightTransform, 8f, 1.5f);
        }

        // targetBoxTransform tracks the locked target - reference shows a dashed circle drawn
        // around the target's own silhouette here, not a box. Sweep all child Images (the stock
        // box art may be split across children) BEFORE adding our own ring/aspect images.
        if (gds.targetBoxTransform != null)
        {
            foreach (Image img in gds.targetBoxTransform.GetComponentsInChildren<Image>(true))
            {
                img.enabled = false;
            }
            // Sized so the funnel lines thread just past the ring at typical gun range,
            // matching the reference where the TD circle nests inside the funnel.
            BuildDashedRing(gds.targetBoxTransform, 34f, 16);

            GameObject aspectGo = new GameObject("RH_AspectText");
            aspectGo.transform.SetParent(gds.targetBoxTransform, worldPositionStays: false);
            aspectText = aspectGo.AddComponent<Text>();
            if (styleTemplate != null)
            {
                aspectText.font = styleTemplate.font;
                aspectText.material = styleTemplate.material;
                aspectText.color = styleTemplate.color;
                aspectText.fontStyle = styleTemplate.fontStyle;
                aspectText.fontSize = Mathf.Max(8, styleTemplate.fontSize - 6);
            }
            else
            {
                aspectText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                aspectText.fontSize = 14;
                aspectText.color = HudGreen;
            }
            aspectText.alignment = TextAnchor.MiddleLeft;
            aspectText.horizontalOverflow = HorizontalWrapMode.Overflow;
            aspectText.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform aspectRt = aspectText.rectTransform;
            aspectRt.localScale = Vector3.one;
            aspectRt.sizeDelta = new Vector2(100f, 30f);
            aspectRt.anchoredPosition = new Vector2(40f, -40f);
        }

        // The stock radial-fill range circle is replaced by the EEGS-style arc below.
        if (gds.rangeImage != null)
        {
            gds.rangeImage.enabled = false;
        }

        // gunFunnel.displayObj carries a stock "bore" cross sprite that duplicates our own
        // pipper cross at the same spot - disable its Image without touching the funnel's
        // UILineRenderer3D wedge walls (that wedge is wanted functionality, left alone).
        if (gds.gunFunnel != null && gds.gunFunnel.displayObj != null)
        {
            foreach (Image img in gds.gunFunnel.displayObj.GetComponentsInChildren<Image>(true))
            {
                img.enabled = false;
            }
        }

        // The stock lead line (drawn from the boresight cross to the pipper) doesn't exist on
        // the reference EEGS HUD. The sight re-activates its GameObject every frame while
        // locked, so disable the Image components instead - those it never touches.
        if (gds.leadLineRotator != null)
        {
            foreach (Image img in gds.leadLineRotator.GetComponentsInChildren<Image>(true))
            {
                img.enabled = false;
            }
        }
        BuildRangeArc();
        gunRangeText = CreateText("RH_GunRange", new Vector2(-160f, 130f), TextAnchor.MiddleLeft, sizeBoost: -2);
        gunRangeText.gameObject.SetActive(false);

        // NOTE: previously changed both minRange (100->50) and rangeInterval (100->150) at
        // once to stretch the funnel further down the HUD, and got wild jitter at its outer
        // edge. LinearDataInterp.TrimDataMin actually recomputes its retention window from the
        // current minRange/rangeInterval/rangeCount every RecordNextPoint call, so it isn't a
        // truly fixed-size buffer - the more likely culprit was the two changes compounding
        // (halving minRange while also growing rangeInterval). Trying a single, smaller change
        // this time: only stretch rangeInterval, leave minRange at whatever the aircraft's own
        // prefab already had it set to.
        Log($"[Diag] gunFunnel={(gds.gunFunnel != null)} "
            + $"minRange={(gds.gunFunnel != null ? gds.gunFunnel.minRange : -1f)} "
            + $"rangeInterval={(gds.gunFunnel != null ? gds.gunFunnel.rangeInterval : -1f)} "
            + $"rangeCount={(gds.gunFunnel != null ? gds.gunFunnel.rangeCount : -1)}");
        if (gds.gunFunnel != null)
        {
            gds.gunFunnel.rangeInterval *= 1.8f;
            Log($"[Diag] gunFunnel.rangeInterval after={gds.gunFunnel.rangeInterval}");
        }

        gunSight = gds;
    }

    private void BuildRangeArc()
    {
        rangeArcSegments = new Image[RangeArcSegmentCount];
        float step = 360f / RangeArcSegmentCount;
        float chordLength = RangeArcRadius * step * Mathf.Deg2Rad * 1.05f;

        for (int i = 0; i < RangeArcSegmentCount; i++)
        {
            // Segment i sits at angle theta, starting at 12 o'clock and walking
            // counterclockwise (the reference EEGS arc unwinds down the left side).
            float theta = 90f + i * step;
            GameObject go = new GameObject($"RH_RangeArc{i}");
            go.transform.SetParent(hudTransform, worldPositionStays: false);

            Image img = AddImage(go);

            RectTransform rt = img.rectTransform;
            rt.localScale = Vector3.one;
            rt.sizeDelta = new Vector2(1.5f, chordLength);
            rt.anchoredPosition = new Vector2(
                RangeArcRadius * Mathf.Cos(theta * Mathf.Deg2Rad),
                RangeArcRadius * Mathf.Sin(theta * Mathf.Deg2Rad));
            rt.localRotation = Quaternion.Euler(0f, 0f, theta);
            go.SetActive(false);
            rangeArcSegments[i] = img;
        }
    }

    private void BuildSolidRing(Transform parent, float radius, int segmentCount, Vector2 centerOffset)
    {
        float step = 360f / segmentCount;
        float chordLength = radius * step * Mathf.Deg2Rad * 1.05f;
        for (int i = 0; i < segmentCount; i++)
        {
            float theta = i * step;
            GameObject go = new GameObject($"RH_Ring{i}");
            go.transform.SetParent(parent, worldPositionStays: false);

            Image img = AddImage(go);

            RectTransform rt = img.rectTransform;
            rt.localScale = Vector3.one;
            rt.sizeDelta = new Vector2(1f, chordLength);
            rt.anchoredPosition = centerOffset + new Vector2(
                radius * Mathf.Cos(theta * Mathf.Deg2Rad),
                radius * Mathf.Sin(theta * Mathf.Deg2Rad));
            rt.localRotation = Quaternion.Euler(0f, 0f, theta);
        }
    }

    private void BuildManeuverPotentialLines(Transform parent)
    {
        // The long horizontal line pair crossing through the pipper in the EEGS reference.
        BuildManeuverBar(parent, -32f);
        BuildManeuverBar(parent, 32f);
    }

    // 9G reticle: a short "-" further below the 1G ring, per the EEGS reference's separate
    // "1g pipper" and "9g pipper" marks. Real EEGS aircraft compute this from actual available
    // G/airspeed; a fixed offset is a rough stand-in rather than that full calculation.
    private void BuildNineGDash(Transform parent)
    {
        GameObject go = new GameObject("RH_9GDash");
        go.transform.SetParent(parent, worldPositionStays: false);

        Image img = AddImage(go);

        RectTransform rt = img.rectTransform;
        rt.localScale = Vector3.one;
        rt.sizeDelta = new Vector2(14f, 1.5f);
        rt.anchoredPosition = new Vector2(0f, -45f);
    }

    private void BuildManeuverBar(Transform parent, float xCenter)
    {
        GameObject go = new GameObject("RH_ManeuverLine");
        go.transform.SetParent(parent, worldPositionStays: false);

        Image img = AddImage(go);

        RectTransform rt = img.rectTransform;
        rt.localScale = Vector3.one;
        rt.sizeDelta = new Vector2(40f, 1.5f);
        rt.anchoredPosition = new Vector2(xCenter, 0f);
    }

    private void BuildAspectBar(Transform parent, float distance, float length, float splayAngle)
    {
        GameObject go = new GameObject("RH_AspectBar");
        go.transform.SetParent(parent, worldPositionStays: false);

        Image img = AddImage(go);

        RectTransform rt = img.rectTransform;
        rt.localScale = Vector3.one;
        rt.sizeDelta = new Vector2(1.5f, length);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, distance);
        rt.localRotation = Quaternion.Euler(0f, 0f, splayAngle);
    }

    private void BuildDashedRing(Transform parent, float radius, int segmentCount)
    {
        float anglePerSegment = 360f / segmentCount;
        float dashLength = radius * anglePerSegment * Mathf.Deg2Rad * 0.8f;

        for (int i = 0; i < segmentCount; i += 2)
        {
            GameObject go = new GameObject($"RH_GunRing{i}");
            go.transform.SetParent(parent, worldPositionStays: false);

            Image img = AddImage(go);

            float angle = i * anglePerSegment;
            Vector3 offset = Quaternion.Euler(0f, 0f, angle) * new Vector3(0f, radius, 0f);

            RectTransform rt = img.rectTransform;
            rt.localScale = Vector3.one;
            rt.anchoredPosition = new Vector2(offset.x, offset.y);
            // +90: each dash's default orientation (unrotated) is elongated along local Y, and
            // "angle" alone points that same axis radially outward (matching its own position
            // vector) instead of tangent to the circle - rendering as a spoke/asterisk instead
            // of a ring. Rotating an extra 90 degrees aligns it with the tangent instead.
            rt.localRotation = Quaternion.Euler(0f, 0f, angle + 90f);
            rt.sizeDelta = new Vector2(2f, dashLength);
        }
    }

    private void BuildCross(Transform parent, float armLength, float thickness)
    {
        GameObject h = new GameObject("RH_CrossH");
        h.transform.SetParent(parent, worldPositionStays: false);
        Image hImg = AddImage(h);
        hImg.rectTransform.localScale = Vector3.one;
        hImg.rectTransform.sizeDelta = new Vector2(armLength * 2f, thickness);
        hImg.rectTransform.anchoredPosition = Vector2.zero;

        GameObject v = new GameObject("RH_CrossV");
        v.transform.SetParent(parent, worldPositionStays: false);
        Image vImg = AddImage(v);
        vImg.rectTransform.localScale = Vector3.one;
        vImg.rectTransform.sizeDelta = new Vector2(thickness, armLength * 2f);
        vImg.rectTransform.anchoredPosition = Vector2.zero;
    }

    private void CreateIlsNeedles()
    {
        locNeedle = CreateBar("RH_ILS_LOC", new Vector2(4f, 90f));
        gsNeedle = CreateBar("RH_ILS_GS", new Vector2(90f, 4f));
        ilsFreqLabel = CreateText("RH_ILSFreq", new Vector2(IlsCenterX, IlsCenterY - 85f), TextAnchor.MiddleCenter, sizeBoost: -6);

        locNeedle.gameObject.SetActive(false);
        gsNeedle.gameObject.SetActive(false);
        ilsFreqLabel.gameObject.SetActive(false);
    }

    // Every Image this mod creates goes through here so it picks up hudImageMaterial - plain
    // Images on Unity's default material didn't render through the HUD combiner glass (see the
    // hudImageMaterial field comment).
    private Image AddImage(GameObject go)
    {
        Image img = go.AddComponent<Image>();
        img.color = styleTemplate != null ? styleTemplate.color : HudGreen;
        if (hudImageMaterial != null)
        {
            img.material = hudImageMaterial;
        }
        return img;
    }

    private Image CreateBar(string name, Vector2 size)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(hudTransform, worldPositionStays: false);

        Image img = AddImage(go);

        RectTransform rt = img.rectTransform;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        rt.sizeDelta = size;
        rt.anchoredPosition = new Vector2(IlsCenterX, IlsCenterY);
        return img;
    }

    // Retried every tick (see UpdateRoutine) instead of a one-shot Initialize call:
    // GetComponentsInChildren<HUDVelocity> came back completely empty in one test session and
    // correct in another, from the exact same injector timing - the cockpit HUD hierarchy isn't
    // guaranteed fully built ~0.5-1.5s after CollimatedHUDUI is found. Stops retrying once it
    // finds a box (speedTapeReady) or once the vehicle is gone.
    private void TryCreateSpeedTape()
    {
        if (speedTapeReady)
        {
            return;
        }

        // Anchor everything to the stock HUD airspeed readout so the tape lands beside it on
        // any aircraft. Prefer the HUDVelocity instance living under this collimated HUD.
        Text stockSpeed = null;
        foreach (HUDVelocity hv in actor.GetComponentsInChildren<HUDVelocity>(true))
        {
            if (hv.text == null)
            {
                continue;
            }
            stockSpeed = hv.text;
            if (hv.GetComponentInParent<CollimatedHUDUI>() != null)
            {
                break;
            }
        }
        if (stockSpeed == null)
        {
            return; // not built yet (or this vehicle has none) - try again next tick
        }
        speedTapeReady = true;
        Log("[Diag] TryCreateSpeedTape: found HUDVelocity box, building tape.");

        // Altitude-ladder style: a continuous vertical line with tick marks at a tight, regular
        // interval, extending upward from the box (downward consistently collided with the
        // M/G/AoA block that sits close beneath the box on every aircraft tested, so that
        // direction is not used). Spacing is intentionally tight, matching the stock altitude
        // tape's look, rather than the wide gaps tried earlier.
        RectTransform stockRt = stockSpeed.rectTransform;
        float spacing = stockSpeed.fontSize * 1.4f;
        speedTapeBasePos = stockRt.anchoredPosition;
        speedTapePixelsPerUnit = spacing / SpeedTickInterval;
        Log($"[Diag] CreateSpeedTape: fontSize={stockSpeed.fontSize} "
            + $"anchoredPos={stockRt.anchoredPosition} "
            + $"anchorMin={stockRt.anchorMin} anchorMax={stockRt.anchorMax} pivot={stockRt.pivot}");

        speedTicks = new Text[SpeedTickCount];
        for (int i = 0; i < SpeedTickCount; i++)
        {
            GameObject go = new GameObject($"RH_SpeedTick{i}");
            go.transform.SetParent(stockRt.parent, worldPositionStays: false);

            Text t = go.AddComponent<Text>();
            t.font = stockSpeed.font;
            t.material = stockSpeed.material;
            t.color = stockSpeed.color;
            t.fontStyle = stockSpeed.fontStyle;
            t.fontSize = stockSpeed.fontSize;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;

            // Copy the stock box's anchor/pivot, not just anchoredPosition - anchoredPosition
            // is measured relative to the anchor reference point, so a freshly-added Text's
            // default center anchors put it in a completely different spot if the stock box
            // uses different anchors (this was silently placing every tick off in empty space).
            RectTransform rt = t.rectTransform;
            rt.anchorMin = stockRt.anchorMin;
            rt.anchorMax = stockRt.anchorMax;
            rt.pivot = stockRt.pivot;
            rt.localScale = stockRt.localScale;
            rt.localRotation = stockRt.localRotation;
            rt.sizeDelta = new Vector2(stockSpeed.fontSize * 2.5f, spacing);
            rt.anchoredPosition = speedTapeBasePos;
            speedTicks[i] = t;
        }

        // The continuous vertical line itself, spanning from just above the box up through the
        // topmost tick - pivot at the bottom so its height alone controls how far up it reaches.
        GameObject lineGo = new GameObject("RH_SpeedLine");
        lineGo.transform.SetParent(stockRt.parent, worldPositionStays: false);
        Image lineImg = lineGo.AddComponent<Image>();
        lineImg.color = stockSpeed.color;
        if (hudImageMaterial != null)
        {
            lineImg.material = hudImageMaterial;
        }
        speedTapeLine = lineImg.rectTransform;
        speedTapeLine.anchorMin = stockRt.anchorMin;
        speedTapeLine.anchorMax = stockRt.anchorMax;
        speedTapeLine.pivot = new Vector2(stockRt.pivot.x, 0f);
        speedTapeLine.localScale = stockRt.localScale;
        speedTapeLine.localRotation = stockRt.localRotation;
        speedTapeLine.sizeDelta = new Vector2(2f, spacing * SpeedTickCount);
        speedTapeLine.anchoredPosition = speedTapeBasePos + new Vector2(0f, spacing * 0.5f);
    }

    // The ladder's rung objects are instantiated in HUDElevationLadder.Start(), which may not
    // have finished running yet when our injector attaches (same class of timing issue as the
    // FPM/CollimatedHud bug) - one test session cached only 2 rungs instead of the full ~38,
    // meaning childCount was mid-construction (nonzero but not yet complete) at the moment we
    // first checked. Require childCount to stay unchanged for a couple of ticks before trusting
    // it, instead of accepting the first nonzero reading.
    private void TryCacheLadderRungs()
    {
        if (ladderRungObjects != null || elevationLadder == null || elevationLadder.ladderTransform == null)
        {
            return;
        }

        Transform lt = elevationLadder.ladderTransform;
        int count = lt.childCount;
        if (count == 0)
        {
            return; // not constructed yet - try again next tick
        }

        if (count == lastLadderChildCount)
        {
            ladderStableTicks++;
        }
        else
        {
            ladderStableTicks = 0;
            lastLadderChildCount = count;
        }
        if (ladderStableTicks < 2)
        {
            return; // still growing (or just reached this count) - confirm next tick
        }

        ladderRungObjects = new List<GameObject>();
        for (int i = 0; i < lt.childCount; i++)
        {
            GameObject child = lt.GetChild(i).gameObject;
            if (elevationLadder.horizonTransform == null || child != elevationLadder.horizonTransform.gameObject)
            {
                ladderRungObjects.Add(child);
            }
        }
        Log($"[Diag] Cached {ladderRungObjects.Count} pitch ladder rungs (stable at {count}).");
    }

    private void CreateRollIndicator()
    {
        GameObject root = new GameObject("RH_RollScale");
        root.transform.SetParent(hudTransform, worldPositionStays: false);
        RectTransform rootRt = root.AddComponent<RectTransform>();
        rootRt.localScale = Vector3.one;
        rootRt.localRotation = Quaternion.identity;
        rootRt.anchoredPosition = Vector2.zero;
        rollScaleRoot = root.transform;

        int[] tickAngles = { 0, 10, -10, 20, -20, 30, -30, 45, -45 };
        foreach (int angle in tickAngles)
        {
            bool major = angle == 0 || Mathf.Abs(angle) == 45;
            BuildRollTick(root.transform, angle, major ? 14f : 9f, RollArcRadius);
        }

        // Pointer: a caret ("^"), clearly separated below the tick arc, rotated to the current
        // bank.
        GameObject ptr = new GameObject("RH_RollPointer");
        ptr.transform.SetParent(root.transform, worldPositionStays: false);
        rollPointer = ptr.AddComponent<RectTransform>();
        rollPointer.localScale = Vector3.one;
        BuildAspectBar(ptr.transform, 0f, 10f, 30f);
        BuildAspectBar(ptr.transform, 0f, 10f, -30f);
        SetPointerAngle(rollPointer, 0f, RollPointerRadius);
    }

    // Segments are built once at a fixed thickness and repositioned/rescaled every tick in
    // UpdateTurnCircle - unlike BuildSolidRing's static rings, this one's radius changes
    // continuously with bank/speed, and naively scaling a parent transform to change radius
    // would also scale the line thickness into an unreadable wedge.
    private void CreateTurnCircle()
    {
        GameObject root = new GameObject("RH_TurnCircleRoot");
        root.transform.SetParent(hudTransform, worldPositionStays: false);
        RectTransform rootRt = root.AddComponent<RectTransform>();
        rootRt.localScale = Vector3.one;
        rootRt.anchoredPosition = Vector2.zero;
        turnCircleRoot = root.transform;

        turnCircleSegments = new Image[TurnCircleSegmentCount];
        for (int i = 0; i < TurnCircleSegmentCount; i++)
        {
            GameObject go = new GameObject($"RH_TurnCircleSeg{i}");
            go.transform.SetParent(turnCircleRoot, worldPositionStays: false);
            Image img = AddImage(go);
            img.rectTransform.localScale = Vector3.one;
            turnCircleSegments[i] = img;
        }

        // Short horizontal caps at the two open ends of the half-circle, like the flat strokes
        // in hiragana "hi" (ひ), rather than the arc just stopping mid-tangent.
        GameObject leftCapGo = new GameObject("RH_TurnCircleLeftCap");
        leftCapGo.transform.SetParent(turnCircleRoot, worldPositionStays: false);
        turnCircleLeftCap = AddImage(leftCapGo);
        turnCircleLeftCap.rectTransform.localScale = Vector3.one;

        GameObject rightCapGo = new GameObject("RH_TurnCircleRightCap");
        rightCapGo.transform.SetParent(turnCircleRoot, worldPositionStays: false);
        turnCircleRightCap = AddImage(rightCapGo);
        turnCircleRightCap.rectTransform.localScale = Vector3.one;

        root.SetActive(false);
    }

    private void UpdateTurnCircle(bool gunMode)
    {
        if (turnCircleRoot == null)
        {
            return;
        }
        if (!gunMode)
        {
            if (turnCircleRoot.gameObject.activeSelf)
            {
                turnCircleRoot.gameObject.SetActive(false);
            }
            return;
        }
        if (!turnCircleRoot.gameObject.activeSelf)
        {
            turnCircleRoot.gameObject.SetActive(true);
        }

        // Standard coordinated-turn radius: r = v^2 / (g * tan(bank)). Bank is floored well
        // above zero so the radius stays finite near wings-level instead of blowing up.
        float bankDeg = Mathf.Max(Mathf.Abs(flightInfo.roll), TurnCircleMinBankDeg);
        float speed = Mathf.Max(flightInfo.airspeed, 1f);
        float radiusMeters = speed * speed / (9.81f * Mathf.Tan(bankDeg * Mathf.Deg2Rad));
        float radius = Mathf.Clamp(radiusMeters * TurnCircleScale, TurnCircleMinRadius, TurnCircleMaxRadius);

        float step = 360f / TurnCircleSegmentCount;
        float chordLength = radius * step * Mathf.Deg2Rad * 1.05f;
        // Drop the very first and last segment (the ones right at the diameter endpoints) so
        // the arc stops a bit short of where the caps sit - a small visible gap there reads
        // better than the caps crossing/overlapping the arc's own endpoint segments.
        int visibleSegments = Mathf.RoundToInt(TurnCircleSegmentCount * (TurnCircleArcDegrees / 360f));
        for (int i = 0; i < TurnCircleSegmentCount; i++)
        {
            Image seg = turnCircleSegments[i];
            bool visible = i >= 1 && i <= visibleSegments - 1;
            if (seg.gameObject.activeSelf != visible)
            {
                seg.gameObject.SetActive(visible);
            }
            if (!visible)
            {
                continue;
            }

            // +180: sweep the BOTTOM half (left -> down through the bottom -> right), so
            // level flight shows a cup/bowl bulging downward, not a dome bulging upward
            // (which would only be correct upside-down).
            float theta = 180f + i * step;
            RectTransform rt = seg.rectTransform;
            rt.sizeDelta = new Vector2(1.5f, chordLength);
            rt.anchoredPosition = new Vector2(
                radius * Mathf.Cos(theta * Mathf.Deg2Rad),
                radius * Mathf.Sin(theta * Mathf.Deg2Rad));
            // theta directly (not +90, that's for BuildDashedRing's different up-vector-based
            // position formula) - matches BuildSolidRing's cos/sin convention, where rotation
            // == position angle already lands each segment tangent to the circle.
            rt.localRotation = Quaternion.Euler(0f, 0f, theta);
        }

        // Flat horizontal caps at the two open ends (left at 180 degrees, right at 0/360) -
        // the diameter's endpoints, where the arc's own tangent is vertical. Centering the cap
        // on the endpoint made half of it project outward past the curve like a crossing "+";
        // pivoting so the cap only extends outward from the endpoint reads as a continuation
        // of the arc instead.
        const float capLength = 9f;
        turnCircleLeftCap.rectTransform.sizeDelta = new Vector2(capLength, 1.5f);
        turnCircleLeftCap.rectTransform.pivot = new Vector2(1f, 0.5f);
        turnCircleLeftCap.rectTransform.anchoredPosition = new Vector2(-radius, 0f);
        turnCircleLeftCap.rectTransform.localRotation = Quaternion.identity;

        turnCircleRightCap.rectTransform.sizeDelta = new Vector2(capLength, 1.5f);
        turnCircleRightCap.rectTransform.pivot = new Vector2(0f, 0.5f);
        turnCircleRightCap.rectTransform.anchoredPosition = new Vector2(radius, 0f);
        turnCircleRightCap.rectTransform.localRotation = Quaternion.identity;

        // Same sign convention as SetRollPointerAngle(-flightInfo.roll) - tilts the ring to
        // stay aligned with the current bank plane, like the reference footage.
        turnCircleRoot.localRotation = Quaternion.Euler(0f, 0f, -flightInfo.roll);
    }

    private void BuildRollTick(Transform parent, float angleDeg, float length, float radius)
    {
        GameObject go = new GameObject($"RH_RollTick{angleDeg}");
        go.transform.SetParent(parent, worldPositionStays: false);

        Image img = AddImage(go);

        // angleDeg measured from straight down; ticks sit on the arc pointing radially outward.
        Vector2 dir = new Vector2(Mathf.Sin(angleDeg * Mathf.Deg2Rad), -Mathf.Cos(angleDeg * Mathf.Deg2Rad));
        RectTransform rt = img.rectTransform;
        rt.localScale = Vector3.one;
        rt.sizeDelta = new Vector2(3f, length);
        rt.anchoredPosition = dir * (radius + length * 0.5f);
        rt.localRotation = Quaternion.Euler(0f, 0f, angleDeg + 180f);
    }

    private void SetRollPointerAngle(float bankDeg)
    {
        SetPointerAngle(rollPointer, bankDeg, RollPointerRadius);
    }

    private static void SetPointerAngle(RectTransform pointer, float bankDeg, float radius)
    {
        float clamped = Mathf.Clamp(bankDeg, -50f, 50f);
        Vector2 dir = new Vector2(Mathf.Sin(clamped * Mathf.Deg2Rad), -Mathf.Cos(clamped * Mathf.Deg2Rad));
        pointer.anchoredPosition = dir * radius;
        pointer.localRotation = Quaternion.Euler(0f, 0f, clamped + 180f);
    }

    // Always-on boresight "gun cross": same ray-cast approach as the flight path marker, just
    // using the airframe's own forward direction instead of the velocity vector, and shown in
    // every mode (not only when HUDGunDirectorSight's gun-mode-only fixedSightTransform is
    // active).
    private void CreateBoresightCross()
    {
        GameObject go = new GameObject("RH_BoresightCross");
        go.transform.SetParent(hudTransform, worldPositionStays: false);
        boresightCross = go.transform;
        BuildCross(boresightCross, 6f, 1.5f);
    }

    // Great circle steering cue: small ring that offsets sideways from the boresight cross,
    // showing which way to turn to converge on the course to the current steerpoint. Only
    // shown when a steerpoint/waypoint is actually set. Parented to the boresight cross itself
    // (not the HUD root) so it just needs a small anchoredPosition nudge in local space,
    // inheriting boresight's own per-frame ray-cast position/rotation for free.
    private void CreateSteeringCue()
    {
        GameObject go = new GameObject("RH_SteeringCue");
        go.transform.SetParent(boresightCross, worldPositionStays: false);
        go.AddComponent<RectTransform>();
        steeringCue = go.transform;
        BuildSolidRing(steeringCue, 6f, 10, Vector2.zero);

        // Connecting line from the boresight cross out to the cue, like a steering needle.
        GameObject lineGo = new GameObject("RH_SteeringLine");
        lineGo.transform.SetParent(boresightCross, worldPositionStays: false);
        Image lineImg = AddImage(lineGo);
        steeringLine = lineImg.rectTransform;
        steeringLine.localScale = Vector3.one;
        steeringLine.sizeDelta = new Vector2(0f, 2f);
        steeringLine.anchoredPosition = Vector2.zero;

        go.SetActive(false);
        lineGo.SetActive(false);
    }

    // The stock DLZ ladder (HUDDynamicLaunchZone) renders quite small and sits high on the
    // HUD by default; scale its whole display up and drop it down a bit for readability.
    // Shrinks the M/G/AoA digital block a bit further and nudges the value numbers (not the
    // labels) right, for a bit more breathing room. gText and aoaMeter.text are the only two
    // of that block we have direct references to; if they share an immediate parent (the
    // block's container), shrinking that also shrinks the Mach line drawn alongside them even
    // though we don't have a handle on that Text directly.
    private void RestyleMgaBlock()
    {
        HUDGMeter gMeter = actor.GetComponentInChildren<HUDGMeter>(true);
        HUDAoAMeter aoaMeter = actor.GetComponentInChildren<HUDAoAMeter>(true);
        if (gMeter == null || gMeter.gText == null || aoaMeter == null || aoaMeter.text == null)
        {
            return;
        }

        Transform gParent = gMeter.gText.transform.parent;
        Transform aoaParent = aoaMeter.text.transform.parent;
        Log($"[Diag] RestyleMgaBlock: gParent={gParent?.name} aoaParent={aoaParent?.name} "
            + $"shared={gParent == aoaParent}");
        if (gParent != null && gParent == aoaParent)
        {
            gParent.localScale *= 0.85f;
        }

        float shift = gMeter.gText.fontSize * 0.6f;
        ShiftAnchoredX(gMeter.gText.rectTransform, shift);
        ShiftAnchoredX(aoaMeter.text.rectTransform, shift);
    }

    private static void ShiftAnchoredX(RectTransform rt, float shift)
    {
        Vector2 pos = rt.anchoredPosition;
        pos.x += shift;
        rt.anchoredPosition = pos;
    }

    private void RestyleDlzScale()
    {
        HUDDynamicLaunchZone dlz = actor.GetComponentInChildren<HUDDynamicLaunchZone>(true);
        if (dlz == null || dlz.displayObject == null)
        {
            Log($"[Diag] RestyleDlzScale: dlz={(dlz != null)} "
                + $"displayObject={(dlz != null && dlz.displayObject != null)} - skipped.");
            return;
        }

        Transform dt = dlz.displayObject.transform;
        Log($"[Diag] RestyleDlzScale: before scale={dt.localScale} pos={dt.localPosition} "
            + $"parent={dt.parent?.name}");
        dt.localScale *= DlzScale;
        Vector3 pos = dt.localPosition;
        pos.y += DlzYOffset;
        dt.localPosition = pos;
        Log($"[Diag] RestyleDlzScale: after scale={dt.localScale} pos={dt.localPosition}");
    }

    // Replaces the stock waypoint guidance visuals: the "tunnel" of floating gates on the way
    // to the waypoint is removed entirely, and the waypoint icon art is swapped for the
    // real-jet steerpoint diamond. The distance label is a Text, untouched by the Image sweep.
    private void RestyleWaypointIndicator()
    {
        HUDWaypoint wpt = actor.GetComponentInChildren<HUDWaypoint>(true);
        if (wpt == null)
        {
            return;
        }

        if (wpt.tunnelImages != null)
        {
            foreach (Image img in wpt.tunnelImages)
            {
                if (img != null)
                {
                    img.enabled = false;
                }
            }
        }
        // Also cover the template itself, in case the tunnel hasn't been constructed yet -
        // clones instantiated later inherit the disabled Image.
        if (wpt.tunnelTemplate != null)
        {
            foreach (Image img in wpt.tunnelTemplate.GetComponentsInChildren<Image>(true))
            {
                img.enabled = false;
            }
        }

        if (wpt.waypointIcon != null)
        {
            // Leave the stock icon hidden entirely rather than building on it: it lives at
            // HUDWaypoint's own far Z depth (startDepth + tunnelCount*depthInterval, thousands
            // of units vs. this mod's hudDepth of ~1000), which collapsed a tadpole built there
            // down to a barely-visible dot instead of a readable ring+leg. CreateWaypointTadpole
            // (below) builds an independent copy at the standard hudDepth instead, ray-cast
            // toward the real waypoint bearing every frame like boresightCross/FPM.
            // HUDWaypoint.EnableTunnel() re-activates this GameObject on every new-waypoint
            // transition, so it's also forced off every frame in LateUpdate below.
            stockWaypointIcon = wpt.waypointIcon.gameObject;
            stockWaypointIcon.SetActive(false);
        }

        CreateWaypointTadpole();
    }

    // Independent of HUDWaypoint's own icon hierarchy (see RestyleWaypointIndicator) so it can
    // be positioned at hudDepth instead of HUDWaypoint's own far-away depth. A hollow diamond
    // (4 equally-spaced BuildSolidRing segments - 4 points on a circle connected by chords is
    // exactly a square, giving a clean diamond outline with no separate leg/tail to be confused
    // with the steering cue's own ring+stub shape).
    private void CreateWaypointTadpole()
    {
        GameObject go = new GameObject("RH_WaypointTadpole");
        go.transform.SetParent(hudTransform, worldPositionStays: false);
        waypointTadpole = go.transform;
        BuildSolidRing(waypointTadpole, 10f, 4, Vector2.zero);
        waypointTadpole.gameObject.SetActive(false);
    }

    private Text CreateText(string name, Vector2 anchoredPos, TextAnchor anchor, int sizeBoost = 0)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(hudTransform, worldPositionStays: false);

        Text t = go.AddComponent<Text>();
        if (styleTemplate != null)
        {
            t.font = styleTemplate.font;
            t.material = styleTemplate.material;
            t.color = styleTemplate.color;
            t.fontStyle = styleTemplate.fontStyle;
            t.fontSize = Mathf.Max(8, styleTemplate.fontSize + sizeBoost);
        }
        else
        {
            t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize = 20 + sizeBoost;
            t.fontStyle = FontStyle.Bold;
            t.color = HudGreen;
        }
        t.alignment = anchor;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;

        RectTransform rt = t.rectTransform;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        rt.sizeDelta = new Vector2(600f, 60f);
        rt.anchoredPosition = anchoredPos;
        return t;
    }

    private void LateUpdate()
    {
        if (velVector != null && fpmImage != null)
        {
            FlightPathMarker.UpdatePosition(velVector, fpmImage, hudDepth);
        }

        if (boresightCross != null && actor != null)
        {
            Ray ray = new Ray(VRHead.position, actor.transform.forward);
            boresightCross.position = ray.GetPoint(hudDepth);
            boresightCross.rotation = Quaternion.LookRotation(ray.direction, hudTransform.up);
        }

        if (waypointTadpole != null)
        {
            WaypointManager wm = WaypointManager.instance;
            Vector3? wptPos = wm != null ? wm.currentWaypointPos : null;
            if (wptPos != null)
            {
                Ray ray = new Ray(VRHead.position, wptPos.Value - VRHead.position);
                waypointTadpole.position = ray.GetPoint(hudDepth);
                waypointTadpole.rotation = Quaternion.LookRotation(ray.direction, hudTransform.up);
                waypointTadpole.gameObject.SetActive(true);
            }
            else
            {
                waypointTadpole.gameObject.SetActive(false);
            }
        }

        if (stockWaypointIcon != null)
        {
            stockWaypointIcon.SetActive(false);
        }

        // Forced every frame (not just the 0.15s coroutine tick) - something stock re-enables
        // this on a target lock transition, and only a same-frame LateUpdate reliably wins that
        // race rather than flashing on for up to 150ms.
        if (noseIndicatorImage != null)
        {
            noseIndicatorImage.enabled = !gunModeCached;
        }
    }

    private IEnumerator UpdateRoutine()
    {
        WaitForSeconds wait = new WaitForSeconds(0.15f);
        while (enabled)
        {
            if (actor == null || flightInfo == null || !dataBlockText)
            {
                yield break; // vehicle or HUD gone; injector will detect and re-scan
            }

            if (weaponManager == null)
            {
                weaponManager = actor.GetComponentInChildren<WeaponManager>(true);
            }
            if (measurements == null)
            {
                measurements = actor.GetComponentInChildren<MeasurementManager>(true);
            }

            bool gunMode = IsGunMode();
            gunModeCached = gunMode;

            UpdateMasterMode();
            UpdateDeclutter(gunMode);
            UpdateGunLock(gunMode);
            UpdateDataBlock(gunMode);
            UpdateFlightData(gunMode);
            if (!gunMode)
            {
                UpdateRollPointer();
            }
            TryCreateSpeedTape();
            UpdateSpeedTape(gunMode);
            UpdateIls();
            UpdateAspectMarker();
            UpdateGunRange(gunMode);
            UpdateFuelWarning();
            UpdateSteeringCue();
            UpdateTurnCircle(gunMode);

            yield return wait;
        }
    }

    private bool IsGunMode()
    {
        return weaponManager != null
            && weaponManager.currentEquip != null
            && weaponManager.currentEquip.weaponType == HPEquippable.WeaponTypes.Gun;
    }

    // Shows the pipper's ring/9G-dash/maneuver-line cluster only while an actual lead solution
    // is being computed (gunSight.isTargetLocked) - meaningless clutter otherwise, since without
    // a locked target there's no lead to depict.
    private void UpdateGunLock(bool gunMode)
    {
        if (pipperLockGroup == null)
        {
            return;
        }

        bool shouldShow = gunMode && gunSight != null && gunSight.isTargetLocked;
        if (shouldShow != pipperLockGroupShown)
        {
            pipperLockGroup.gameObject.SetActive(shouldShow);
            pipperLockGroupShown = shouldShow;
        }
    }

    // Real HUDs decrease the amount of readout in a gun/dogfight fight to keep the reticle area
    // clear: drop the steerpoint line, FPA/max-G, clock, and the speed tape's scrolling tick
    // marks, keeping only the master mode, digital speed and altitude boxes. Also step the
    // aircraft's own stock declutter system (its DECL button) to its most-decluttered level, so
    // whatever stock sub-labels that HUD prefab hides at that level disappear too, restoring
    // whatever level the pilot had set once gun mode is deselected.
    private bool ladderRungsHidden;

    private void UpdateDeclutter(bool gunMode)
    {
        if (rollScaleRoot != null)
        {
            rollScaleRoot.gameObject.SetActive(!gunMode);
        }

        TryCacheLadderRungs();
        if (ladderRungObjects != null && gunMode != ladderRungsHidden)
        {
            foreach (GameObject rung in ladderRungObjects)
            {
                if (rung != null)
                {
                    rung.SetActive(!gunMode);
                }
            }
            ladderRungsHidden = gunMode;
        }

        if (radarLockIconImages != null && gunMode != radarLockIconHidden)
        {
            foreach (Image img in radarLockIconImages)
            {
                img.enabled = !gunMode;
            }
            radarLockIconHidden = gunMode;
        }

        // Hide whenever the stock pipper reticle is itself actually on screen (its own cross
        // then marks the real, possibly off-boresight, lead-computed aim point) - checking
        // pipperTransform's actual active state directly covers every reason it can turn on
        // (lock, gimbal-active, etc.), not just the "locked" case. Otherwise (funnel wedge
        // showing, pipper off) this stays up as the one reliable fixed reticle mark.
        bool pipperShowing = gunSight != null && gunSight.pipperTransform != null
            && gunSight.pipperTransform.gameObject.activeInHierarchy;
        boresightCross?.gameObject.SetActive(!(gunMode && pipperShowing));

        if (hudDeclutter == null)
        {
            return;
        }

        if (gunMode && !declutterOverridden)
        {
            savedDeclutterLevel = hudDeclutter.currentLevel;
            int discreteCount = hudDeclutter.discreteObjects != null ? hudDeclutter.discreteObjects.Length : 0;
            int additiveCount = hudDeclutter.additiveObjects != null ? hudDeclutter.additiveObjects.Length : 0;
            int maxLevel = Mathf.Max(0, Mathf.Max(discreteCount, additiveCount) - 1);
            // Level 1 rather than max: full declutter also removed the pitch ladder/horizon
            // line, which the reference gun HUD keeps (the dashed horizon at lower left).
            hudDeclutter.SetDeclutter(Mathf.Min(1, maxLevel));
            declutterOverridden = true;
        }
        else if (!gunMode && declutterOverridden)
        {
            hudDeclutter.SetDeclutter(savedDeclutterLevel);
            declutterOverridden = false;
        }
    }

    // Composes the bottom-right data block, real-HUD style: radar altitude on top, then the
    // steerpoint line(s), time-to-go and the mission clock, all in one right-aligned column.
    // Gun mode keeps only the radar altitude line (declutter).
    private void UpdateDataBlock(bool gunMode)
    {
        sb.Clear();

        float ra = flightInfo.radarAltitude;
        if (ra >= 0f && ra <= flightInfo.maxRadarAlt)
        {
            float displayAlt = measurements != null ? measurements.ConvertedAltitude(ra) : ra * 3.28084f;
            sb.Append("RA ").Append(Mathf.RoundToInt(displayAlt).ToString("00000"));
        }
        else
        {
            sb.Append("RA -----");
        }

        if (altitudeWarning != null)
        {
            float alDisplay = measurements != null
                ? measurements.ConvertedAltitude(altitudeWarning.minAltitude)
                : altitudeWarning.minAltitude * 3.28084f;
            sb.Append('\n').Append("AL ").Append(Mathf.RoundToInt(alDisplay));
        }

        if (!gunMode)
        {
            AppendSteerpointLines();
        }
        else
        {
            // Reference F-16 gun block: ARM/SAFE, target aspect ("TA 21" = 210 deg off the
            // target's nose), radar slant range ("F 032" = 3.2 nm), then the clock.
            sb.Append('\n').Append(weaponManager != null && weaponManager.isMasterArmed ? "ARM" : "SAFE");

            Actor target = gunSight != null && gunSight.isTargetLocked ? gunSight.targetActor : null;
            if (target != null)
            {
                Vector3 losFromTarget = actor.position - target.position;
                Vector3 tgtFwdFlat = target.transform.forward;
                tgtFwdFlat.y = 0f;
                Vector3 losFlat = losFromTarget;
                losFlat.y = 0f;
                float aspect = Vector3.SignedAngle(tgtFwdFlat, losFlat, Vector3.up);
                if (aspect < 0f)
                {
                    aspect += 360f;
                }
                sb.Append('\n').Append("TA ").Append(Mathf.RoundToInt(aspect / 10f).ToString("00"));

                float nm = MeasurementManager.DistToNauticalMile(losFromTarget.magnitude);
                sb.Append('\n').Append("F ").Append(Mathf.RoundToInt(nm * 10f).ToString("000"));
            }
        }

        AppendClock();

        dataBlockText.text = sb.ToString();
    }

    // Reference HUDs print the system clock as HHMMSS (e.g. "131606").
    private static void AppendClock()
    {
        VTOLVR.VT_OCTimeOfDay tod = VTOLVR.VT_OCTimeOfDay.instance;
        if (tod == null)
        {
            return;
        }
        double t = tod.GetLocalTime();
        int h = (int)t;
        float rem = (float)(t - h);
        int m = (int)(rem * 60f);
        int s = (int)(rem * 3600f) - m * 60;
        sb.Append('\n').Append(h.ToString("00")).Append(m.ToString("00")).Append(s.ToString("00"));
    }

    private void AppendSteerpointLines()
    {
        WaypointManager wm = WaypointManager.instance;
        Vector3? wptPos = wm != null ? wm.currentWaypointPos : null;
        if (wm == null || wptPos == null)
        {
            return;
        }

        Vector3 flatToWpt = wptPos.Value - actor.transform.position;
        flatToWpt.y = 0f;

        float bearing = VectorUtils.SignedAngle(Vector3.forward, flatToWpt, Vector3.right);
        if (bearing < 0f)
        {
            bearing += 360f;
        }

        float distance = flatToWpt.magnitude;
        string distStr = measurements != null ? measurements.FormattedDistance(distance) : $"{distance:0}m";

        // Identify the steerpoint by list index when it's a numbered route point; scenario
        // waypoints set directly (rescue zones etc.) aren't in the list, so fall back to name.
        int index = wm.waypoints.IndexOf(wm.currentWaypoint) + 1;
        sb.Append('\n');
        if (index > 0)
        {
            sb.Append("STPT ").Append(index.ToString("00"));
        }
        else
        {
            string name = wm.currentWaypoint.name;
            sb.Append(name.Length > 10 ? name.Substring(0, 10) : name);
        }

        // F-16 style bearing>range line.
        sb.Append('\n').Append(Mathf.RoundToInt(bearing).ToString("000")).Append('>').Append(distStr);

        sb.Append('\n').Append("TTG ");
        // Groundspeed too low for a meaningful time-to-go (parked/taxiing) - dash it out,
        // same as real HUDs do when the nav solution isn't valid yet.
        const float minSpeedForTtg = 5f; // m/s
        if (flightInfo.airspeed < minSpeedForTtg)
        {
            sb.Append("--:--");
        }
        else
        {
            float ttgSeconds = distance / flightInfo.airspeed;
            int mm = Mathf.FloorToInt(ttgSeconds / 60f);
            int ss = Mathf.FloorToInt(ttgSeconds % 60f);
            sb.Append(mm.ToString("00")).Append(':').Append(ss.ToString("00"));
        }
    }

    private void UpdateRollPointer()
    {
        if (rollPointer != null)
        {
            SetRollPointerAngle(-flightInfo.roll);
        }
    }

    private void UpdateFuelWarning()
    {
        // Blink at ~1Hz while below bingo; the coroutine tick (0.15s) is fast enough for this.
        bool show = flightWarnings != null
            && flightWarnings.isBingoFuel
            && Mathf.Repeat(Time.time, 1f) < 0.6f;
        if (fuelWarnText.gameObject.activeSelf != show)
        {
            fuelWarnText.gameObject.SetActive(show);
        }
    }


    private void UpdateSteeringCue()
    {
        if (steeringCue == null)
        {
            return;
        }

        WaypointManager wm = WaypointManager.instance;
        Vector3? wptPos = wm != null ? wm.currentWaypointPos : null;
        if (wm == null || wptPos == null)
        {
            if (steeringCue.gameObject.activeSelf)
            {
                steeringCue.gameObject.SetActive(false);
                steeringLine.gameObject.SetActive(false);
            }
            return;
        }

        if (!steeringCue.gameObject.activeSelf)
        {
            steeringCue.gameObject.SetActive(true);
            steeringLine.gameObject.SetActive(true);
        }

        Vector3 toWpt = wptPos.Value - actor.transform.position;
        Vector3 flatToWpt = toWpt;
        flatToWpt.y = 0f;
        // Vector3.right is the axis convention FlightInfo.heading and AppendSteerpointLines
        // both use for this SignedAngle call - using Vector3.up here (a plausible-looking but
        // wrong axis for a horizontal bearing) was the bug behind the cue only ever swinging
        // one direction.
        float bearing = VectorUtils.SignedAngle(Vector3.forward, flatToWpt, Vector3.right);
        // Heading error can be up to +-180 deg (e.g. flying away from the steerpoint), unlike
        // AoA/sideslip - projecting that as a real 3D ray (like the FPM/boresight cross do)
        // sent the cue flying off toward the screen edge at anything past a few degrees.
        // Clamp in on-screen pixels instead, as a child of the boresight cross.
        float steeringErrorH = Mathf.Clamp(Mathf.DeltaAngle(flightInfo.heading, bearing), -30f, 30f);
        float offsetX = steeringErrorH / 30f * SteeringCueMaxOffsetPx;

        // Vertical: angle above/below the horizon needed to reach the steerpoint's altitude,
        // compared against current pitch - climb cue is up, descend cue is down.
        float horizontalDist = flatToWpt.magnitude;
        float elevationAngle = Mathf.Atan2(toWpt.y, horizontalDist) * Mathf.Rad2Deg;
        float steeringErrorV = Mathf.Clamp(elevationAngle - flightInfo.pitch, -30f, 30f);
        float offsetY = steeringErrorV / 30f * SteeringCueMaxOffsetPx;

        Vector2 offset = new Vector2(offsetX, offsetY);
        float magnitude = offset.magnitude;
        Vector2 dir = magnitude > 0.01f ? offset / magnitude : Vector2.right;

        // Ring carries the full offset (direction + magnitude) so it visibly moves across the
        // whole range like a real fly-to cue; the line is a short fixed-length stub extending
        // further out from the ring, past it, toward the true waypoint direction. Approaching
        // from the waypoint side this reads stub-then-ring, not ring-then-stub.
        steeringCue.GetComponent<RectTransform>().anchoredPosition = offset;

        steeringLine.sizeDelta = new Vector2(SteeringCueLineStubPx, 2f);
        steeringLine.anchoredPosition = offset + dir * (SteeringCueLineStubPx * 0.5f);
        steeringLine.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
    }

    private void UpdateMasterMode()
    {
        string mode = "NAV";
        bool armed = false;
        bool gunMode = false;

        if (weaponManager != null)
        {
            armed = weaponManager.isMasterArmed;
            HPEquippable eq = weaponManager.currentEquip;
            if (eq != null)
            {
                switch (eq.weaponType)
                {
                    case HPEquippable.WeaponTypes.AAM:
                        mode = "A-A";
                        break;
                    case HPEquippable.WeaponTypes.AGM:
                    case HPEquippable.WeaponTypes.AGMCruise:
                    case HPEquippable.WeaponTypes.Bomb:
                    case HPEquippable.WeaponTypes.Rocket:
                    case HPEquippable.WeaponTypes.AntiShip:
                    case HPEquippable.WeaponTypes.AntiRadMissile:
                        mode = "A-G";
                        break;
                    case HPEquippable.WeaponTypes.Gun:
                        mode = "GUN";
                        gunMode = true;
                        break;
                }
            }
        }

        // Reference gun HUD labels the top line "AA {slant range kft}{L|R}" while a target is
        // locked (e.g. "AA 02R" = 2,000 ft, target right of the nose); ARM/SAFE moves down
        // into the data block in that mode.
        if (gunMode)
        {
            Actor target = gunSight != null && gunSight.isTargetLocked ? gunSight.targetActor : null;
            if (target != null)
            {
                Vector3 los = target.position - actor.position;
                int kft = Mathf.Clamp(Mathf.RoundToInt(los.magnitude * 3.28084f / 1000f), 0, 99);
                string side = Vector3.Dot(los, actor.transform.right) >= 0f ? "R" : "L";
                masterModeText.text = $"AA {kft:00}{side}";
            }
            else
            {
                masterModeText.text = "NO RAD";
            }
            return;
        }

        masterModeText.text = armed ? $"{mode}  MASTER ARM" : mode;
    }

    // Bottom-center info block: FPA/max-G, fuel quantity (SET bingo/TOTAL remaining), and
    // bearing/range to bullseye - three previously separate, independently-positioned Text
    // objects that kept overlapping each other in the bottom-left corner. Folded into one
    // Text so lines stack top-down from a single anchor, the same pattern that already worked
    // fine for dataBlockText on the right. FPA/max-G is dropped in gun mode (decluttered like
    // the rest of the non-essential readouts); fuel and bullseye stay up in every mode.
    private void UpdateFlightData(bool gunMode)
    {
        sb.Clear();
        bool hasContent = false;

        if (!gunMode)
        {
            // Flight path angle: climb/descent angle of the velocity vector, same value real
            // HUDs print next to the flight path marker. Only meaningful once actually moving.
            string fpaStr;
            if (flightInfo.airspeed < 5f)
            {
                fpaStr = "FPA --.-";
            }
            else
            {
                float fpaDeg = Mathf.Asin(Mathf.Clamp(flightInfo.verticalSpeed / flightInfo.airspeed, -1f, 1f)) * Mathf.Rad2Deg;
                fpaStr = $"FPA {fpaDeg:+0.0;-0.0}";
            }

            sb.Append(fpaStr).Append("  MAXG ").Append(flightInfo.maxInstantaneousG.ToString("0.0"));
            hasContent = true;
        }

        if (flightWarnings != null && flightWarnings.fuelTank != null && vehicleMaster != null && vehicleMaster.fuelTanks != null)
        {
            float totalLbs = 0f;
            foreach (FuelTank tank in vehicleMaster.fuelTanks)
            {
                if (tank != null)
                {
                    totalLbs += MeasurementManager.LitersToLbs(tank.fuel, tank.fuelDensity);
                }
            }
            float bingoLbs = MeasurementManager.LitersToLbs(flightWarnings.bingoFuelLevel, flightWarnings.fuelTank.fuelDensity);

            if (hasContent)
            {
                sb.Append('\n');
            }
            sb.Append("SET ").Append(Mathf.RoundToInt(bingoLbs)).Append("LBS  TOTAL ").Append(Mathf.RoundToInt(totalLbs)).Append("LBS");
            hasContent = true;
        }

        // Bearing/range to bullseye (the shared tactical reference point) - identified from the
        // labeled reference diagram as the mystery "142 022"-style number pair.
        Transform bullseye = WaypointManager.instance != null ? WaypointManager.instance.bullseye : null;
        if (bullseye != null)
        {
            Vector3 flatToBullseye = bullseye.position - actor.transform.position;
            flatToBullseye.y = 0f;
            float bearing = VectorUtils.SignedAngle(Vector3.forward, flatToBullseye, Vector3.right);
            if (bearing < 0f)
            {
                bearing += 360f;
            }
            float rangeNm = MeasurementManager.DistToNauticalMile(flatToBullseye.magnitude);

            if (hasContent)
            {
                sb.Append('\n');
            }
            sb.Append("BE ").Append(Mathf.RoundToInt(bearing).ToString("000")).Append(">").Append(Mathf.RoundToInt(rangeNm).ToString("000"));
        }

        flightDataText.text = sb.ToString();
    }

    private void UpdateSpeedTape(bool gunMode)
    {
        if (speedTicks == null)
        {
            return; // vehicle had no HUD airspeed box to anchor to
        }

        float currentSpeed = measurements != null
            ? measurements.ConvertedSpeed(flightInfo.indicatedAirspeed)
            : flightInfo.indicatedAirspeed * 1.94384f;

        // Only shown above the box (higher speeds) - below is where the M/G/AoA block sits,
        // too close beneath the box to reliably clear no matter how much spacing/exclusion was
        // tried, so that direction is skipped entirely rather than continuing to collide with it.
        float baseValue = Mathf.Round(currentSpeed / SpeedTickInterval) * SpeedTickInterval;
        bool anyVisible = false;
        for (int i = 0; i < SpeedTickCount; i++)
        {
            int offsetSteps = i + 1;
            float tickValue = baseValue + offsetSteps * SpeedTickInterval;
            Text tick = speedTicks[i];
            if (gunMode || tickValue < 0f)
            {
                tick.gameObject.SetActive(false);
                continue;
            }
            tick.gameObject.SetActive(true);
            anyVisible = true;
            float y = (tickValue - currentSpeed) * speedTapePixelsPerUnit;
            tick.rectTransform.anchoredPosition = speedTapeBasePos + new Vector2(0f, y);
            tick.text = Mathf.RoundToInt(tickValue / 10f).ToString("00");
        }

        if (speedTapeLine.gameObject.activeSelf != anyVisible)
        {
            speedTapeLine.gameObject.SetActive(anyVisible);
        }
    }

    private void UpdateIls()
    {
        Runway rwy = dashHSI?.ilsRunway;
        if (rwy == null)
        {
            if (locNeedle.gameObject.activeSelf)
            {
                locNeedle.gameObject.SetActive(false);
                gsNeedle.gameObject.SetActive(false);
                ilsFreqLabel.gameObject.SetActive(false);
            }
            return;
        }

        if (!locNeedle.gameObject.activeSelf)
        {
            locNeedle.gameObject.SetActive(true);
            gsNeedle.gameObject.SetActive(true);
            ilsFreqLabel.gameObject.SetActive(true);
        }

        Transform refTf = dashHSI.referenceTransform ? dashHSI.referenceTransform : actor.transform;
        Vector3 position = refTf.position;
        Transform rwyTf = rwy.transform;

        // Glideslope: signed distance from the tilted glide-path plane (same formula DashHSI
        // uses to drive its own dash needle).
        Vector3 glideNormal = Vector3.Cross(Quaternion.AngleAxis(rwy.ilsGlideAngle, rwyTf.right) * rwyTf.forward, rwyTf.right);
        float gsRaw = Vector3.Dot(position - rwyTf.position, glideNormal);
        float gsNorm = Mathf.Clamp(gsRaw / dashHSI.maxILSDistOffset, -1f, 1f);
        gsNeedle.rectTransform.anchoredPosition = new Vector2(IlsCenterX, IlsCenterY - gsNorm * IlsMaxOffsetPx);

        // Localizer: lateral distance from the extended runway centerline.
        float crsHeading = VectorUtils.Bearing(rwyTf.position, rwyTf.position + rwyTf.forward);
        Vector3 courseVec = VectorUtils.BearingVector(crsHeading);
        Vector3 lateralAxis = Vector3.Cross(Vector3.up, courseVec);
        float locRaw = Vector3.Dot(position - rwyTf.position, lateralAxis);
        float locNorm = Mathf.Clamp(locRaw / dashHSI.maxILSDistOffset, -1f, 1f);
        locNeedle.rectTransform.anchoredPosition = new Vector2(IlsCenterX - locNorm * IlsMaxOffsetPx, IlsCenterY);

        ilsFreqLabel.text = $"ILS {rwy.ilsFrequency / 10f:000.0}";
    }

    private void UpdateGunRange(bool gunMode)
    {
        if (rangeArcSegments == null)
        {
            return;
        }

        Actor target = gunMode && gunSight != null && gunSight.isTargetLocked ? gunSight.targetActor : null;
        if (target == null)
        {
            foreach (Image seg in rangeArcSegments)
            {
                if (seg.gameObject.activeSelf)
                {
                    seg.gameObject.SetActive(false);
                }
            }
            if (gunRangeText.gameObject.activeSelf)
            {
                gunRangeText.gameObject.SetActive(false);
            }
            return;
        }

        // Only draw the arc once the target is inside the arc's full-scale range; beyond that a
        // full circle would just dominate the HUD (the reference only ever shows a partial arc).
        float rangeMeters = Vector3.Distance(actor.position, target.position);
        float fraction = rangeMeters * 3.28084f / RangeArcFullScaleFt;
        int visibleCount = fraction < 1f ? Mathf.RoundToInt(fraction * RangeArcSegmentCount) : 0;
        for (int i = 0; i < RangeArcSegmentCount; i++)
        {
            bool show = i < visibleCount;
            if (rangeArcSegments[i].gameObject.activeSelf != show)
            {
                rangeArcSegments[i].gameObject.SetActive(show);
            }
        }

        if (!gunRangeText.gameObject.activeSelf)
        {
            gunRangeText.gameObject.SetActive(true);
        }
        gunRangeText.text = MeasurementManager.DistToNauticalMile(rangeMeters).ToString("0.0");
    }

    private void UpdateAspectMarker()
    {
        if (gunSight == null || aspectText == null)
        {
            return;
        }

        Actor target = gunSight.targetActor;
        if (target == null)
        {
            if (aspectText.gameObject.activeSelf)
            {
                aspectText.gameObject.SetActive(false);
            }
            return;
        }

        if (!aspectText.gameObject.activeSelf)
        {
            aspectText.gameObject.SetActive(true);
        }

        // Aspect angle: 0 = target's tail toward us, 180 = head-on. ">"/"<" shows which side
        // the target's nose is offset toward, matching the reference's "090>" style readout.
        Vector3 localFwd = gunSight.targetBoxTransform.InverseTransformDirection(target.transform.forward);
        float signedAngle = Mathf.Atan2(localFwd.x, localFwd.y) * Mathf.Rad2Deg;
        float aspect = 180f - Mathf.Abs(signedAngle);
        aspectText.text = signedAngle >= 0f
            ? $"{Mathf.RoundToInt(aspect):000}>"
            : $"<{Mathf.RoundToInt(aspect):000}";
    }
}
