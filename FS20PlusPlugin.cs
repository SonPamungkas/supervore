using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace FS20PlusMod
{
    [BepInPlugin("com.nuclearoption.fs20plus", "FS-20+ Vortex Mod", "1.0.0")]
    [BepInDependency("com.offiry.qol", BepInDependency.DependencyFlags.SoftDependency)]
    public class FS20PlusPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal const float TargetMaxSpeed        = 4500f;
        internal const float BaseBoostThrust       = 400000f;  // 4x original 100,000 N
        internal const float ScramjetMinMach       = 4.5f;
        internal const float ScramjetMinAltM       = 10000f;   // 10,000 m
        internal const float ScramjetThrustPerEngine = 500000f;
        internal const float FlameoutAltM         = 50000f;
        internal const float VtolThrusterMultiplier = 4f;      // 8,000 -> 32,000 N

        internal static bool scramjetActive = false;
        internal static bool flameout = false;

        private static bool IsVortex(Aircraft aircraft)
        {
            if (aircraft == null) return false;
            try { return aircraft.definition != null && aircraft.definition.jsonKey == "SmallFighter1"; }
            catch { return false; }
        }

        private static readonly AnimationCurve flatAltitudeCurve;
        static FS20PlusPlugin()
        {
            flatAltitudeCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(50000f, 1f));
            flatAltitudeCurve.preWrapMode = WrapMode.ClampForever;
            flatAltitudeCurve.postWrapMode = WrapMode.ClampForever;
        }

        private void Awake()
        {
            Log = Logger;
            new Harmony("com.nuclearoption.fs20plus").PatchAll();
            Log.LogInfo("FS-20+ Vortex Mod loaded.");
        }

        // ─── Engine ───────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(Turbojet), "FixedUpdate")]
        public static class TurbojetFixedUpdatePatch
        {
            private static readonly FieldInfo aircraftField   = AccessTools.Field(typeof(Turbojet), "aircraft");
            private static readonly FieldInfo maxSpeedField   = AccessTools.Field(typeof(Turbojet), "maxSpeed");
            private static readonly FieldInfo minDensityField = AccessTools.Field(typeof(Turbojet), "minDensity");
            private static readonly FieldInfo altThrustField  = AccessTools.Field(typeof(Turbojet), "altitudeThrust");
            private static readonly FieldInfo thrustField     = AccessTools.Field(typeof(Turbojet), "thrust");

            private static readonly HashSet<int> logged       = new HashSet<int>();
            private static readonly HashSet<int> swivelDone   = new HashSet<int>();
            private static bool wasScramjet = false;
            private static bool wasFlameout = false;

            public static void Prefix(Turbojet __instance)
            {
                var aircraft = aircraftField?.GetValue(__instance) as Aircraft;
                if (!IsVortex(aircraft)) return;

                int id    = __instance.GetInstanceID();
                bool first = !logged.Contains(id);

                float alt = __instance.transform.position.y - Datum.originPosition.y;
                flameout = alt >= FlameoutAltM;

                if (flameout)
                {
                    __instance.maxThrust = 0f;
                    if (minDensityField != null) minDensityField.SetValue(__instance, 999f);
                    if (flameout != wasFlameout)
                    {
                        Log.LogInfo("[Flameout] Engine flameout at " + alt.ToString("F0") + " m");
                        wasFlameout = flameout;
                    }
                    if (first) logged.Add(id);
                    return;
                }
                if (flameout != wasFlameout)
                {
                    Log.LogInfo("[Flameout] Engine relight at " + alt.ToString("F0") + " m");
                    wasFlameout = flameout;
                }

                float targetThrust = scramjetActive ? ScramjetThrustPerEngine : BaseBoostThrust;
                if (Math.Abs(__instance.maxThrust - targetThrust) > 1f)
                {
                    if (first) Log.LogInfo("[Engine] " + __instance.gameObject.name + " maxThrust -> " + targetThrust);
                    __instance.maxThrust = targetThrust;
                }

                if (maxSpeedField != null)
                {
                    float v = (float)maxSpeedField.GetValue(__instance);
                    if (Math.Abs(v - TargetMaxSpeed) > 1f)
                        maxSpeedField.SetValue(__instance, TargetMaxSpeed);
                }
                if (minDensityField != null)
                {
                    float v = (float)minDensityField.GetValue(__instance);
                    if (v > -0.5f) minDensityField.SetValue(__instance, -1f);
                }
                if (altThrustField != null)
                {
                    var curve = altThrustField.GetValue(__instance) as AnimationCurve;
                    if (curve != flatAltitudeCurve)
                        altThrustField.SetValue(__instance, flatAltitudeCurve);
                }

                // Boost VTOL thrusters once per aircraft instance
                int aircraftId = aircraft.GetInstanceID();
                if (!swivelDone.Contains(aircraftId))
                {
                    BoostSwivelThrusters(aircraft);
                    swivelDone.Add(aircraftId);
                }

                if (first) logged.Add(id);
            }

            public static void Postfix(Turbojet __instance)
            {
                var aircraft = aircraftField?.GetValue(__instance) as Aircraft;
                if (!IsVortex(aircraft)) return;

                if (flameout)
                {
                    scramjetActive = false;
                    if (thrustField != null) thrustField.SetValue(__instance, 0f);
                    return;
                }

                float alt  = __instance.transform.position.y - Datum.originPosition.y;
                float sos  = Mathf.Max(-0.005f * alt + 340f, 290f);
                float mach = aircraft.speed / sos;

                bool shouldBeActive = mach >= ScramjetMinMach && alt >= ScramjetMinAltM;
                // Hysteresis: stay on until 10% below thresholds
                if (scramjetActive && !shouldBeActive)
                    scramjetActive = mach >= ScramjetMinMach * 0.9f && alt >= ScramjetMinAltM * 0.9f;
                else
                    scramjetActive = shouldBeActive;

                if (scramjetActive != wasScramjet)
                {
                    Log.LogInfo("[Scramjet] " + (scramjetActive ? "ON" : "OFF") + " — Mach " + mach.ToString("F2") + " alt " + alt.ToString("F0") + " m");
                    wasScramjet = scramjetActive;
                }
            }

            private static void BoostSwivelThrusters(Aircraft aircraft)
            {
                try
                {
                    var sds = aircraft.GetComponentInChildren<SwivelDuctSystem>();
                    if (sds == null) { Log.LogInfo("[VTOL] No SwivelDuctSystem found on Vortex"); return; }

                    var thrustersField = AccessTools.Field(typeof(SwivelDuctSystem), "thrusters");
                    if (thrustersField == null) { Log.LogWarning("[VTOL] 'thrusters' field not found on SwivelDuctSystem"); return; }

                    var thrusters = thrustersField.GetValue(sds) as IList;
                    if (thrusters == null || thrusters.Count == 0) { Log.LogWarning("[VTOL] thrusters list is null or empty"); return; }

                    for (int i = 0; i < thrusters.Count; i++)
                    {
                        var t = thrusters[i];
                        if (t == null) continue;
                        var f = AccessTools.Field(t.GetType(), "maxThrust");
                        if (f == null) continue;
                        float orig = (float)f.GetValue(t);
                        float boosted = orig * VtolThrusterMultiplier;
                        f.SetValue(t, boosted);
                        Log.LogInfo("[VTOL] thrusters[" + i + "] maxThrust " + orig + " -> " + boosted);
                    }
                }
                catch (Exception e) { Log.LogError("[VTOL] Failed to boost SwivelDuct thrusters: " + e); }
            }
        }

        // ─── Radar ────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(Radar), "Awake")]
        public static class RadarRangePatch
        {
            private static readonly FieldInfo attachedUnitField = AccessTools.Field(typeof(TargetDetector), "attachedUnit");

            public static void Postfix(Radar __instance)
            {
                var unit = attachedUnitField?.GetValue(__instance) as Aircraft;
                if (!IsVortex(unit)) return;
                float oldRange  = __instance.RadarParameters.maxRange;
                float oldSignal = __instance.RadarParameters.maxSignal;
                __instance.RadarParameters.maxRange  = oldRange  * 2f;
                __instance.RadarParameters.maxSignal = oldSignal * 2f;
                Log.LogInfo("[Radar] maxRange " + oldRange + " -> " + __instance.RadarParameters.maxRange +
                            ", maxSignal " + oldSignal + " -> " + __instance.RadarParameters.maxSignal);
            }
        }

        // ─── FBW Hypersonic Damper ────────────────────────────────────────────────

        [HarmonyPatch(typeof(ControlsFilter), "Filter",
            new Type[] { typeof(ControlInputs), typeof(Vector3), typeof(Rigidbody), typeof(float), typeof(bool) })]
        public static class FBWHypersonicDamperPatch
        {
            public static void Postfix(ControlInputs inputs, Vector3 rawInputs, Rigidbody rb, float gForce, bool flightAssist)
            {
                var aircraft = rb.GetComponent<Aircraft>();
                if (!IsVortex(aircraft)) return;
                float alt  = rb.transform.position.y - Datum.originPosition.y;
                float sos  = Mathf.Max(-0.005f * alt + 340f, 290f);
                float mach = aircraft.speed / sos;
                if (mach > 3f)
                {
                    float damper = 3f / mach;
                    inputs.pitch *= damper;
                    inputs.roll  *= damper;
                }
            }
        }

        // ─── Airbrake Joint Reinforcement ─────────────────────────────────────────

        [HarmonyPatch(typeof(Airbrake), "FixedUpdate")]
        public static class AirbrakeStrengthPatch
        {
            private static readonly FieldInfo abAircraftField  = AccessTools.Field(typeof(Airbrake), "aircraft");
            private static readonly FieldInfo abAttachedField  = AccessTools.Field(typeof(Airbrake), "attachedAircraft");
            private static readonly FieldInfo abPartField      = AccessTools.Field(typeof(Airbrake), "part");
            private static readonly HashSet<int> reinforced    = new HashSet<int>();

            public static void Prefix(Airbrake __instance)
            {
                var aircraft = abAttachedField?.GetValue(__instance) as Aircraft
                            ?? abAircraftField?.GetValue(__instance) as Aircraft;
                if (!IsVortex(aircraft)) return;
                int id = __instance.GetInstanceID();
                if (reinforced.Contains(id)) return;
                var part = abPartField?.GetValue(__instance) as UnitPart;
                if (part != null)
                {
                    foreach (var j in part.GetComponents<FixedJoint>())
                    {
                        j.breakForce  = float.PositiveInfinity;
                        j.breakTorque = float.PositiveInfinity;
                    }
                    Log.LogInfo("[Airbrake] Reinforced joints on " + __instance.gameObject.name);
                }
                reinforced.Add(id);
            }
        }

        // ─── Speed Gauge ──────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(SpeedGauge), "Refresh")]
        public static class SpeedGaugeOverspeedPatch
        {
            private static readonly FieldInfo thresholdField      = AccessTools.Field(typeof(SpeedGauge), "overspeedThreshold");
            private static readonly FieldInfo sgAircraftField     = AccessTools.Field(typeof(SpeedGauge), "aircraft");
            private static readonly FieldInfo overspeedDispField  = AccessTools.Field(typeof(SpeedGauge), "overspeedDisplay");
            private static readonly FieldInfo lastOverspeedField  = AccessTools.Field(typeof(SpeedGauge), "lastOverspeed");
            private static readonly FieldInfo overspeedVoiceField = AccessTools.Field(typeof(SpeedGauge), "overspeedVoice");
            private static readonly FieldInfo airspeedDispField   = AccessTools.Field(typeof(SpeedGauge), "airspeedDisplay");
            private static bool voiceNulled = false;

            public static void Prefix(SpeedGauge __instance)
            {
                var aircraft = sgAircraftField?.GetValue(__instance) as Aircraft;
                if (!IsVortex(aircraft)) return;
                thresholdField?.SetValue(__instance, TargetMaxSpeed);
                if (!voiceNulled && overspeedVoiceField != null)
                {
                    overspeedVoiceField.SetValue(__instance, null);
                    voiceNulled = true;
                }
                lastOverspeedField?.SetValue(__instance, Time.timeSinceLevelLoad);
            }

            public static void Postfix(SpeedGauge __instance)
            {
                var aircraft = sgAircraftField?.GetValue(__instance) as Aircraft;
                if (!IsVortex(aircraft)) return;
                var disp = overspeedDispField?.GetValue(__instance) as Text;
                if (disp != null && disp.enabled) disp.enabled = false;
                var spd = airspeedDispField?.GetValue(__instance) as Text;
                if (spd != null && spd.color == Color.red) spd.color = Color.white;
            }
        }

        [HarmonyPatch(typeof(SpeedGauge), "Initialize")]
        public static class SpeedGaugeInitPatch
        {
            private static readonly FieldInfo thresholdField = AccessTools.Field(typeof(SpeedGauge), "overspeedThreshold");
            public static void Postfix(SpeedGauge __instance, Aircraft aircraft)
            {
                if (!IsVortex(aircraft)) return;
                thresholdField?.SetValue(__instance, TargetMaxSpeed);
            }
        }

        // ─── EOTS (Electro-Optical Targeting System) ──────────────────────────────

        [HarmonyPatch(typeof(TargetDetector), "Awake")]
        public static class EOTSPatch
        {
            private static readonly FieldInfo attachedUnitField  = AccessTools.Field(typeof(TargetDetector), "attachedUnit");
            private static readonly FieldInfo visualRangeField   = AccessTools.Field(typeof(TargetDetector), "visualRange");
            private static readonly FieldInfo magnificationField = AccessTools.Field(typeof(TargetDetector), "magnification");
            private static readonly FieldInfo maxSpeedField      = AccessTools.Field(typeof(TargetDetector), "maxSpeed");

            public static void Postfix(TargetDetector __instance)
            {
                var aircraft = attachedUnitField?.GetValue(__instance) as Aircraft;
                if (!IsVortex(aircraft)) return;

                if (visualRangeField != null)
                {
                    float old = (float)visualRangeField.GetValue(__instance);
                    visualRangeField.SetValue(__instance, 50000f);
                    Log.LogInfo("[EOTS] visualRange " + old + " -> 50000");
                }
                if (magnificationField != null)
                {
                    float old = (float)magnificationField.GetValue(__instance);
                    magnificationField.SetValue(__instance, 10f);
                    Log.LogInfo("[EOTS] magnification " + old + " -> 10");
                }
                if (maxSpeedField != null)
                {
                    float old = (float)maxSpeedField.GetValue(__instance);
                    maxSpeedField.SetValue(__instance, 5000f);
                    Log.LogInfo("[EOTS] maxSpeed " + old + " -> 5000");
                }
            }
        }

        // ─── Power Supply (Capacitor) ─────────────────────────────────────────────

        [HarmonyPatch(typeof(PowerSupply), "Awake")]
        public static class PowerSupplyPatch
        {
            private static readonly FieldInfo aircraftField     = AccessTools.Field(typeof(PowerSupply), "aircraft");
            private static readonly FieldInfo maxChargeField    = AccessTools.Field(typeof(PowerSupply), "maxCharge");
            private static readonly FieldInfo chargePerRPMField = AccessTools.Field(typeof(PowerSupply), "chargePerRPM");
            private static readonly FieldInfo maxPowerField     = AccessTools.Field(typeof(PowerSupply), "maxPower");

            public static void Postfix(PowerSupply __instance)
            {
                var aircraft = aircraftField?.GetValue(__instance) as Aircraft;
                if (!IsVortex(aircraft)) return;

                if (maxChargeField != null)
                {
                    float old = (float)maxChargeField.GetValue(__instance);
                    maxChargeField.SetValue(__instance, old * 10f);
                    Log.LogInfo("[Power] maxCharge " + old + " -> " + old * 10f);
                }
                if (chargePerRPMField != null)
                {
                    float old = (float)chargePerRPMField.GetValue(__instance);
                    chargePerRPMField.SetValue(__instance, old * 10f);
                    Log.LogInfo("[Power] chargePerRPM " + old + " -> " + old * 10f);
                }
                if (maxPowerField != null)
                {
                    float old = (float)maxPowerField.GetValue(__instance);
                    maxPowerField.SetValue(__instance, old * 10f);
                    Log.LogInfo("[Power] maxPower " + old + " -> " + old * 10f);
                }
            }
        }

        // ─── Scramjet HUD Indicator ───────────────────────────────────────────────

        [HarmonyPatch(typeof(FlightHud), "Update")]
        public static class ScramjetHudPatch
        {
            private static GameObject hudObject;
            private static Text hudText;
            private static bool wasActive = false;
            private static float pulseTimer = 0f;

            public static void Postfix(FlightHud __instance)
            {
                if (hudObject == null)
                {
                    try
                    {
                        var canvasField = AccessTools.Field(typeof(FlightHud), "canvas");
                        var canvas = canvasField?.GetValue(__instance) as Canvas;
                        if (canvas == null) return;

                        hudObject = new GameObject("FS20ScramjetIndicator");
                        hudObject.transform.SetParent(canvas.transform, false);

                        hudText = hudObject.AddComponent<Text>();
                        hudText.text       = "SCRAMJET ACTIVE";
                        hudText.font       = Resources.GetBuiltinResource<Font>("Arial.ttf");
                        hudText.fontSize   = 22;
                        hudText.fontStyle  = FontStyle.Bold;
                        hudText.alignment  = TextAnchor.MiddleCenter;
                        hudText.color      = new Color(0f, 1f, 0.6f, 1f);

                        var outline = hudObject.AddComponent<Outline>();
                        outline.effectColor    = new Color(0f, 0f, 0f, 0.8f);
                        outline.effectDistance = new Vector2(1.5f, -1.5f);

                        var rect = hudObject.GetComponent<RectTransform>();
                        rect.anchorMin       = new Vector2(0.5f, 0.75f);
                        rect.anchorMax       = new Vector2(0.5f, 0.75f);
                        rect.pivot           = new Vector2(0.5f, 0.5f);
                        rect.anchoredPosition = Vector2.zero;
                        rect.sizeDelta       = new Vector2(300f, 40f);

                        hudObject.SetActive(false);
                    }
                    catch { return; }
                }

                if (scramjetActive != wasActive)
                {
                    hudObject.SetActive(scramjetActive);
                    wasActive = scramjetActive;
                    if (scramjetActive) pulseTimer = 0f;
                }

                if (scramjetActive && hudText != null)
                {
                    pulseTimer += Time.deltaTime;
                    float alpha = 0.7f + 0.3f * Mathf.Sin(pulseTimer * 3f);
                    hudText.color = new Color(0f, 1f, 0.6f, alpha);
                }
            }
        }
    }
}
