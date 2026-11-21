using Il2Cpp;
using Il2CppInterop.Runtime.Attributes;
using Il2CppTLD.Gear;
using UnityEngine;

namespace SkyCoop
{
    /// <summary>
    /// Shows the light of a light source (kerosene lamp, flashlight, torch, flare, lit match) on a gear
    /// placeholder that has no game item logic left: an item in another player's hands, or a lamp or
    /// flashlight lying on the ground. The state comes from the network as a small integer mode.
    ///
    /// Rig layout was taken from the game prefabs (see Tools/PrefabDump/dumps/light-sources.txt):
    ///   GEAR_KeroseneLampB: FX/{LampFlame, LightIndoor, LightOutdoor, LightIndoorCore}, driven by KeroseneLampIntensity.
    ///   GEAR_Flashlight:    FX/LowFX/{LightIndoors, LightOutdoors, LightGlass}, FX/HighFX/{..., LightExtend}, refs in FlashlightItem.
    ///   GEAR_Torch:         FX/{TorchFlame, LightIndoor, LightOutdoor}, RadialGradient, refs in TorchItem/TorchIntensity.
    ///   GEAR_FlareA/BlueFlare: .../FX/{FlareSparks, LightIndoor, LightOutdoor}, refs in FlareIntensity.
    ///   Matches have no light in the world prefab, so one is created here.
    /// </summary>
    public class RemoteLightSource : MonoBehaviour
    {
        public RemoteLightSource(IntPtr ptr) : base(ptr) { }

        public const int c_ModeOff = 0;
        public const int c_ModeLit = 1;   // Lit. Low beam for a flashlight.
        public const int c_ModeHigh = 2;  // Flashlight high beam.

        public enum LightKind
        {
            None,
            Lamp,
            Flashlight,
            Torch,
            Flare,
            Match,
        }

        private class Beam
        {
            public GameObject m_Root;    // Activated while this beam is lit.
            public GameObject m_Indoor;  // Enabled indoors only (may be null).
            public GameObject m_Outdoor; // Enabled outdoors only (may be null).
        }

        private class RigLight
        {
            public Light m_Light;
            public LightTracking m_Tracking;
            public float m_BaseIntensity;
            public float m_Seed;
            public string m_Path;
        }

        private class GroundRecord
        {
            public float m_DroppedAt;
            public float m_FinishAt;
            public int m_Style;
        }

        public LightKind m_Kind = LightKind.None;
        public string m_GearName = "";
        public bool m_Held = false;
        public int m_Mode = -1;
        public Comps.NetworkPlayer m_Holder = null;
        // Ground only: server time (in-game hours) when the fuel, battery or burn time runs out. 0 = never.
        public float m_FinishAt = 0;

        private readonly List<Beam> m_Beams = new List<Beam>();
        private readonly List<GameObject> m_LitOnly = new List<GameObject>();
        private readonly List<RigLight> m_Lights = new List<RigLight>();
        private bool m_Outdoor = true;
        private float m_NextEnvironmentCheck = 0;
        private float m_DiagnoseAt = -1;
        private bool m_Failed = false;
        private HeatSource m_Heat = null;
        private bool m_HasHeat = false;
        private float m_HeatMaxIncrease = 0;
        private float m_HeatInnerRadius = 0;
        private float m_HeatOuterRadius = 0;
        private float m_HeatMinutesToMax = 1;

        private static readonly HashSet<string> s_Diagnosed = new HashSet<string>();
        // Light sources the local player is picking up from the ground, by GUID (see RememberPickup).
        private static readonly Dictionary<string, GroundRecord> s_Pickups = new Dictionary<string, GroundRecord>();

        /// <summary>Server time in in-game hours, the same clock as dropped and finish times. 0 if not known yet.</summary>
        private static float ServerTime()
        {
            return ModMain.Client != null ? ModMain.Client.m_LastServerTime : 0f;
        }

        // ---------------------------------------------------------------- Sender side

        /// <summary>Mode of the item the local player holds, sent with the holding gear packet.</summary>
        [HideFromIl2Cpp]
        public static int GetHeldMode(GearItem Gear)
        {
            if (!Gear)
            {
                return c_ModeOff;
            }
            try
            {
                if (Gear.IsLitFlashlight())
                {
                    FlashlightItem Flashlight = Gear.GetComponent<FlashlightItem>();
                    if (Flashlight && Flashlight.m_State == FlashlightItem.State.High)
                    {
                        return c_ModeHigh;
                    }
                    return c_ModeLit;
                }
                if (Gear.IsLitLightsource() || Gear.IsLitLamp() || Gear.IsLitTorch() || Gear.IsLitFlare() || Gear.IsLitMatch())
                {
                    return c_ModeLit;
                }
            }
            catch (Exception e)
            {
                Logger.Log(ConsoleColor.Red, $"[RemoteLight] GetHeldMode failed for {Gear.name}: {e.Message}");
            }
            return c_ModeOff;
        }

        /// <summary>
        /// Style of a dropped lamp or flashlight: 1 lit, 2 flashlight high beam, otherwise the given style.
        /// Torches and flares keep their own style encoding (0 fresh, 1 burning, 2 burnt out).
        /// </summary>
        [HideFromIl2Cpp]
        public static int GetDroppedStyle(GearItem Gear, int CurrentStyle)
        {
            if (!Gear)
            {
                return CurrentStyle;
            }
            try
            {
                KeroseneLampItem Lamp = Gear.GetComponent<KeroseneLampItem>();
                if (Lamp)
                {
                    return Lamp.IsOn() ? c_ModeLit : c_ModeOff;
                }
                FlashlightItem Flashlight = Gear.GetComponent<FlashlightItem>();
                if (Flashlight)
                {
                    if (!Flashlight.IsOn())
                    {
                        return c_ModeOff;
                    }
                    return Flashlight.m_State == FlashlightItem.State.High ? c_ModeHigh : c_ModeLit;
                }
            }
            catch (Exception e)
            {
                Logger.Log(ConsoleColor.Red, $"[RemoteLight] GetDroppedStyle failed for {Gear.name}: {e.Message}");
            }
            return CurrentStyle;
        }

        /// <summary>
        /// Server time when a dropped lit lamp runs out of fuel or a lit flashlight out of battery, so other
        /// clients can switch it off like a torch. Other items keep the given value (torches and flares set their own).
        /// </summary>
        [HideFromIl2Cpp]
        public static float GetDroppedFinishTime(GearItem Gear, int Style, float CurrentFinishAt)
        {
            float Now = ServerTime();
            if (!Gear || Style <= c_ModeOff || Now <= 0f)
            {
                return CurrentFinishAt;
            }
            try
            {
                KeroseneLampItem Lamp = Gear.GetComponent<KeroseneLampItem>();
                if (Lamp)
                {
                    float BurnPerHour = Lamp.GetModifiedFuelBurnLitersPerHour().m_Units;
                    if (BurnPerHour > 0f)
                    {
                        return Now + Lamp.m_CurrentFuelLiters.m_Units / BurnPerHour;
                    }
                    return CurrentFinishAt;
                }
                FlashlightItem Flashlight = Gear.GetComponent<FlashlightItem>();
                if (Flashlight)
                {
                    // Beam durations are in hours (low 1 h, high 5 min) for a full charge.
                    float Duration = Style == c_ModeHigh ? Flashlight.m_HighBeamDuration : Flashlight.m_LowBeamDuration;
                    return Now + Flashlight.GetNormalizedCharge() * Duration;
                }
            }
            catch (Exception e)
            {
                Logger.Log(ConsoleColor.Red, $"[RemoteLight] GetDroppedFinishTime failed for {Gear.name}: {e.Message}");
            }
            return CurrentFinishAt;
        }

        /// <summary>
        /// Called when the local player starts picking up a ground item: remembers how long it has been lying
        /// there lit, because the item data on the server is the state from the moment it was dropped.
        /// </summary>
        [HideFromIl2Cpp]
        public static void RememberPickup(Comps.DroppedGearVisual Visual)
        {
            if (!Visual || string.IsNullOrEmpty(Visual.m_GUID) || Visual.m_Style <= c_ModeOff)
            {
                return;
            }
            if (s_Pickups.Count > 64)
            {
                s_Pickups.Clear(); // Entries of failed pickups are never consumed.
            }
            s_Pickups[Visual.m_GUID] = new GroundRecord
            {
                m_DroppedAt = Visual.m_DroppedTime,
                m_FinishAt = Visual.m_FinishProcessAt,
                m_Style = Visual.m_Style,
            };
        }

        /// <summary>
        /// Called right after a picked up item is deserialized: burns the fuel, battery or burn time it used
        /// while it lay lit on the ground (lamp, flashlight, torch, flare).
        /// </summary>
        [HideFromIl2Cpp]
        public static void ApplyTimeOnGround(GearItem Gear, string GUID)
        {
            if (!Gear || string.IsNullOrEmpty(GUID))
            {
                return;
            }
            GroundRecord Record;
            if (!s_Pickups.TryGetValue(GUID, out Record))
            {
                return;
            }
            s_Pickups.Remove(GUID);

            float Now = ServerTime();
            if (Now <= 0f || Record.m_DroppedAt <= 0f)
            {
                return;
            }
            float End = Record.m_FinishAt > 0f ? Mathf.Min(Now, Record.m_FinishAt) : Now;
            float Hours = End - Record.m_DroppedAt;
            if (Hours <= 0f)
            {
                return;
            }

            try
            {
                KeroseneLampItem Lamp = Gear.GetComponent<KeroseneLampItem>();
                if (Lamp)
                {
                    if (Record.m_Style == c_ModeLit)
                    {
                        Lamp.ReduceFuel(Hours);
                        Logger.Log(ConsoleColor.Cyan, $"[RemoteLight] {Gear.name} burned {Hours:F2} h of fuel on the ground, {Lamp.GetPercentFuelRemaining():F0}% left");
                    }
                    return;
                }
                FlashlightItem Flashlight = Gear.GetComponent<FlashlightItem>();
                if (Flashlight)
                {
                    float Duration = Record.m_Style == c_ModeHigh ? Flashlight.m_HighBeamDuration : Flashlight.m_LowBeamDuration;
                    float Charge = Flashlight.GetNormalizedCharge();
                    if (Duration > 0f && Charge > 0f)
                    {
                        // Scale the raw charge, so its unit does not matter.
                        float NewCharge = Mathf.Max(0f, Charge - Hours / Duration);
                        Flashlight.m_CurrentBatteryCharge *= NewCharge / Charge;
                        Logger.Log(ConsoleColor.Cyan, $"[RemoteLight] {Gear.name} used {Hours:F2} h of battery on the ground, charge {Charge:F2} -> {NewCharge:F2}");
                    }
                    return;
                }
                // Torch and flare styles: 1 burning, 2 burnt out (see GearsSync.SendDropItem).
                TorchItem Torch = Gear.GetComponent<TorchItem>();
                if (Torch)
                {
                    if (Record.m_Style == 1)
                    {
                        Torch.m_ElapsedBurnMinutes = Mathf.Min(Torch.m_ElapsedBurnMinutes + Hours * 60f, Torch.GetModifiedBurnLifetimeMinutes());
                        Logger.Log(ConsoleColor.Cyan, $"[RemoteLight] {Gear.name} burned {Hours * 60f:F0} min on the ground");
                    }
                    return;
                }
                FlareItem Flare = Gear.GetComponent<FlareItem>();
                if (Flare)
                {
                    if (Record.m_Style == 1)
                    {
                        Flare.m_ElapsedBurnMinutes = Mathf.Min(Flare.m_ElapsedBurnMinutes + Hours * 60f, Flare.GetModifiedBurnLifetimeMinutes());
                        Logger.Log(ConsoleColor.Cyan, $"[RemoteLight] {Gear.name} burned {Hours * 60f:F0} min on the ground");
                    }
                    return;
                }
            }
            catch (Exception e)
            {
                Logger.Log(ConsoleColor.Red, $"[RemoteLight] ApplyTimeOnGround failed for {Gear.name}: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- Setup

        /// <summary>
        /// Adds a light rig to a freshly instantiated gear object. Must be called before the item
        /// components are stripped from the root. Returns null for items that are not light sources.
        /// </summary>
        /// <param name="Root">Instantiated gear object.</param>
        /// <param name="Prefab">Prefab it was instantiated from, used to read the original light intensities.</param>
        /// <param name="Held">True for a placeholder in another player's hands, false for an item on the ground.</param>
        [HideFromIl2Cpp]
        public static RemoteLightSource TryAttach(GameObject Root, GameObject Prefab, bool Held)
        {
            if (!Root)
            {
                return null;
            }
            try
            {
                LightKind Kind = GetKind(Root);
                if (Kind == LightKind.None)
                {
                    return null;
                }
                // Torches and flares on the ground keep their own burn logic (see DroppedGearVisual patches).
                if (!Held && Kind != LightKind.Lamp && Kind != LightKind.Flashlight)
                {
                    return null;
                }

                RemoteLightSource Rig = Root.AddComponent<RemoteLightSource>();
                Rig.m_Kind = Kind;
                Rig.m_GearName = Root.name;
                Rig.m_Held = Held;
                Rig.BuildBeams(Root);
                Rig.CollectLights(Root, Prefab);
                // Kept on placeholders by the strip whitelists in AssetManager (lamp, torch, flare have one).
                // Its settings are cached: the stripped lamp item may destroy it (KeroseneLampItem.DestroyHeatSource).
                Rig.m_Heat = Root.GetComponent<HeatSource>();
                if (Rig.m_Heat)
                {
                    Rig.m_HasHeat = true;
                    Rig.m_HeatMaxIncrease = Rig.m_Heat.m_MaxTempIncrease;
                    Rig.m_HeatInnerRadius = Rig.m_Heat.m_MaxTempIncreaseInnerRadius;
                    Rig.m_HeatOuterRadius = Rig.m_Heat.m_MaxTempIncreaseOuterRadius;
                    Rig.m_HeatMinutesToMax = Rig.m_Heat.m_TimeToReachMaxTempMinutes;
                }
                Rig.DisableGameLightDrivers(Root);
                Rig.ApplyMode(c_ModeOff, true);
                return Rig;
            }
            catch (Exception e)
            {
                Logger.Log(ConsoleColor.Red, $"[RemoteLight] Setup failed for {Root.name}: {e}");
                return null;
            }
        }

        [HideFromIl2Cpp]
        private static LightKind GetKind(GameObject Root)
        {
            if (Root.GetComponent<FlashlightItem>())
            {
                return LightKind.Flashlight;
            }
            if (Root.GetComponent<KeroseneLampItem>())
            {
                return LightKind.Lamp;
            }
            if (Root.GetComponent<TorchItem>())
            {
                return LightKind.Torch;
            }
            if (Root.GetComponent<FlareItem>())
            {
                return LightKind.Flare;
            }
            if (Root.GetComponent<MatchesItem>())
            {
                return LightKind.Match;
            }
            return LightKind.None;
        }

        [HideFromIl2Cpp]
        private void BuildBeams(GameObject Root)
        {
            switch (m_Kind)
            {
                case LightKind.Flashlight:
                    {
                        FlashlightItem Flashlight = Root.GetComponent<FlashlightItem>();
                        AddBeam(Flashlight.m_FxObjectLow, ToObject(Flashlight.m_LightIndoor), ToObject(Flashlight.m_LightOutdoor));
                        AddBeam(Flashlight.m_FxObjectHigh, ToObject(Flashlight.m_LightIndoorHigh), ToObject(Flashlight.m_LightOutdoorHigh));
                        break;
                    }
                case LightKind.Lamp:
                    {
                        KeroseneLampIntensity Intensity = Root.GetComponent<KeroseneLampIntensity>();
                        GameObject Indoor = Intensity ? ToObject(Intensity.m_IndoorLight) : FindChild(Root.transform, "LightIndoor");
                        GameObject Outdoor = Intensity ? ToObject(Intensity.m_OutdoorLight) : FindChild(Root.transform, "LightOutdoor");
                        AddBeam(ParentOf(Indoor) ?? FindChild(Root.transform, "FX"), Indoor, Outdoor);
                        break;
                    }
                case LightKind.Torch:
                    {
                        TorchItem Torch = Root.GetComponent<TorchItem>();
                        TorchIntensity Intensity = Root.GetComponent<TorchIntensity>();
                        GameObject Indoor = Intensity ? Intensity.m_IndoorLight : FindChild(Root.transform, "LightIndoor");
                        GameObject Outdoor = Intensity ? Intensity.m_OutdoorLight : FindChild(Root.transform, "LightOutdoor");
                        GameObject Fx = Torch && Torch.m_FXGameObject ? Torch.m_FXGameObject : ParentOf(Indoor);
                        AddBeam(Fx, Indoor, Outdoor);
                        GameObject Gradient = Intensity && Intensity.m_RadialGradient ? Intensity.m_RadialGradient : FindChild(Root.transform, "RadialGradient");
                        if (Gradient)
                        {
                            m_LitOnly.Add(Gradient);
                        }
                        break;
                    }
                case LightKind.Flare:
                    {
                        FlareIntensity Intensity = Root.GetComponent<FlareIntensity>();
                        GameObject Indoor = Intensity ? Intensity.m_IndoorLight : FindChild(Root.transform, "LightIndoor");
                        GameObject Outdoor = Intensity ? Intensity.m_OutdoorLight : FindChild(Root.transform, "LightOutdoor");
                        AddBeam(ParentOf(Indoor) ?? FindChild(Root.transform, "FX"), Indoor, Outdoor);
                        break;
                    }
                case LightKind.Match:
                    {
                        // The match flame only exists on the first person model, so make a small warm light.
                        GameObject LightObject = new GameObject("SkyCoop_MatchLight");
                        LightObject.transform.SetParent(Root.transform, false);
                        LightObject.transform.localPosition = new Vector3(0f, 0.1f, 0f);
                        Light MatchLight = LightObject.AddComponent<Light>();
                        MatchLight.type = LightType.Point;
                        MatchLight.range = 4f;
                        MatchLight.intensity = 1.5f;
                        MatchLight.color = new Color(1f, 0.62f, 0.3f);
                        MatchLight.shadows = LightShadows.None;
                        AddBeam(LightObject, null, null);
                        break;
                    }
            }
        }

        [HideFromIl2Cpp]
        private void AddBeam(GameObject Root, GameObject Indoor, GameObject Outdoor)
        {
            if (!Root && !Indoor && !Outdoor)
            {
                return;
            }
            m_Beams.Add(new Beam { m_Root = Root, m_Indoor = Indoor, m_Outdoor = Outdoor });
        }

        [HideFromIl2Cpp]
        private void CollectLights(GameObject Root, GameObject Prefab)
        {
            HashSet<IntPtr> Seen = new HashSet<IntPtr>();
            foreach (Beam B in m_Beams)
            {
                foreach (GameObject Holder in new[] { B.m_Root, B.m_Indoor, B.m_Outdoor })
                {
                    if (!Holder)
                    {
                        continue;
                    }
                    foreach (Light L in Holder.GetComponentsInChildren<Light>(true))
                    {
                        if (!Seen.Add(L.Pointer))
                        {
                            continue;
                        }
                        RigLight Rig = new RigLight();
                        Rig.m_Light = L;
                        Rig.m_Tracking = L.GetComponent<LightTracking>();
                        Rig.m_Path = GetPath(Root.transform, L.transform);
                        Rig.m_BaseIntensity = GetBaseIntensity(L, Prefab, Rig.m_Path);
                        Rig.m_Seed = UnityEngine.Random.Range(0f, 100f);
                        m_Lights.Add(Rig);
                    }
                }
            }
        }

        /// <summary>
        /// Intensity the light should have while lit. Taken from the prefab, because item Awake code may
        /// already have changed the instance. When the light has a LightRandomIntensity with a real range
        /// (flares, flashlight) its middle is used, since the game drives the light within that range.
        /// The torch range is 0..1 and is overridden by TorchIntensity at runtime, so the prefab value wins there.
        /// </summary>
        [HideFromIl2Cpp]
        private static float GetBaseIntensity(Light Instance, GameObject Prefab, string Path)
        {
            float Intensity = Instance.intensity;
            Light PrefabLight = null;
            if (Prefab && !string.IsNullOrEmpty(Path))
            {
                Transform T = Prefab.transform.Find(Path);
                if (T)
                {
                    PrefabLight = T.GetComponent<Light>();
                }
            }
            if (PrefabLight)
            {
                Intensity = PrefabLight.intensity;
            }

            LightRandomIntensity Random = Instance.GetComponent<LightRandomIntensity>();
            if (Random && Random.m_Max > 1f && Random.m_Max >= Random.m_Min)
            {
                Intensity = (Random.m_Min + Random.m_Max) * 0.5f;
            }
            return Intensity;
        }

        /// <summary>
        /// Light intensity is driven here, so the game's own modulators must not fight it.
        /// On held placeholders the item logic is switched off too: it expects a real inventory item.
        /// </summary>
        [HideFromIl2Cpp]
        private void DisableGameLightDrivers(GameObject Root)
        {
            foreach (LightRandomIntensity C in Root.GetComponentsInChildren<LightRandomIntensity>(true))
            {
                C.enabled = false;
            }
            foreach (LightFlickering C in Root.GetComponentsInChildren<LightFlickering>(true))
            {
                C.enabled = false;
            }
            foreach (KeroseneLampIntensity C in Root.GetComponentsInChildren<KeroseneLampIntensity>(true))
            {
                C.enabled = false;
            }

            if (!m_Held)
            {
                return;
            }
            foreach (TorchIntensity C in Root.GetComponentsInChildren<TorchIntensity>(true))
            {
                C.enabled = false;
            }
            foreach (FlareIntensity C in Root.GetComponentsInChildren<FlareIntensity>(true))
            {
                C.enabled = false;
            }
            TorchItem Torch = Root.GetComponent<TorchItem>();
            if (Torch)
            {
                Torch.enabled = false;
            }
            FlareItem Flare = Root.GetComponent<FlareItem>();
            if (Flare)
            {
                Flare.enabled = false;
            }
        }

        // ---------------------------------------------------------------- Runtime

        public void SetMode(int Mode)
        {
            ApplyMode(Mode, false);
        }

        /// <summary>A lamp or flashlight on the ground whose fuel or battery has run out by now.</summary>
        private bool IsBurntOut()
        {
            float Now = ServerTime();
            return !m_Held && m_FinishAt > 0f && Now > 0f && Now >= m_FinishAt;
        }

        private void ApplyMode(int Mode, bool Force)
        {
            if (Mode > c_ModeOff && IsBurntOut())
            {
                Mode = c_ModeOff;
            }
            if (Mode == m_Mode && !Force)
            {
                return;
            }
            // Called from packet handlers (SetGear, HandleGearDropped): never let an exception escape.
            try
            {
                m_Mode = Mode;
                m_Outdoor = IsOutdoorLighting();
                RefreshObjects();
                ApplyIntensities();
                ApplyHeat();

                if (m_Mode > c_ModeOff)
                {
                    string Key = $"{m_GearName}|{(m_Held ? "held" : "ground")}|{m_Mode}";
                    if (s_Diagnosed.Add(Key))
                    {
                        m_DiagnoseAt = Time.time + 1f;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log(ConsoleColor.Red, $"[RemoteLight] SetMode({Mode}) failed for {m_GearName}: {e}");
            }
        }

        private int ActiveBeamIndex()
        {
            return m_Mode == c_ModeHigh && m_Beams.Count > 1 ? 1 : 0;
        }

        private void RefreshObjects()
        {
            bool Lit = m_Mode > c_ModeOff;
            int Active = ActiveBeamIndex();
            for (int i = 0; i < m_Beams.Count; i++)
            {
                Beam B = m_Beams[i];
                bool On = Lit && i == Active;
                if (B.m_Root)
                {
                    B.m_Root.SetActive(On);
                }
                if (!On)
                {
                    continue;
                }
                if (B.m_Indoor && B.m_Outdoor && B.m_Indoor.Pointer != B.m_Outdoor.Pointer)
                {
                    B.m_Indoor.SetActive(!m_Outdoor);
                    B.m_Outdoor.SetActive(m_Outdoor);
                }
                else
                {
                    if (B.m_Indoor)
                    {
                        B.m_Indoor.SetActive(true);
                    }
                    if (B.m_Outdoor)
                    {
                        B.m_Outdoor.SetActive(true);
                    }
                }
            }
            foreach (GameObject Obj in m_LitOnly)
            {
                if (Obj)
                {
                    Obj.SetActive(Lit);
                }
            }
        }

        private float FlickerAmplitude()
        {
            switch (m_Kind)
            {
                case LightKind.Torch: return 0.25f;
                case LightKind.Flare: return 0.2f;
                case LightKind.Match: return 0.2f;
                case LightKind.Lamp: return 0.04f;
                default: return 0f;
            }
        }

        private void ApplyIntensities()
        {
            if (m_Mode <= c_ModeOff)
            {
                return;
            }
            float Amplitude = FlickerAmplitude();
            float Now = Time.time;
            foreach (RigLight Rig in m_Lights)
            {
                if (!Rig.m_Light)
                {
                    continue;
                }
                float Scale = 1f;
                if (Amplitude > 0f)
                {
                    Scale += Amplitude * (Mathf.PerlinNoise(Now * 6f, Rig.m_Seed) * 2f - 1f);
                }
                float Value = Rig.m_BaseIntensity * Scale;
                if (Rig.m_Tracking)
                {
                    // LightTracking may expect the gear item the placeholder no longer has.
                    // If it throws once, stop using it and drive the Light directly.
                    try
                    {
                        Rig.m_Tracking.SetLightIntensity(Value);
                    }
                    catch (Exception e)
                    {
                        Logger.Log(ConsoleColor.Yellow, $"[RemoteLight] LightTracking.SetLightIntensity failed on {m_GearName}/{Rig.m_Path}, using Light directly: {e.Message}");
                        Rig.m_Tracking = null;
                    }
                }
                Rig.m_Light.intensity = Value;
            }
        }

        /// <summary>
        /// Lamps, torches and flares warm whoever stands next to them (+3 C, within 2 m in the prefabs),
        /// so the copy of another player's item does the same. Only while lit and visible: a hidden
        /// placeholder stays parented to the hand and would otherwise heat from nowhere.
        /// </summary>
        private void ApplyHeat()
        {
            if (!m_HasHeat)
            {
                return;
            }
            try
            {
                bool On = m_Mode > c_ModeOff && isActiveAndEnabled;
                if (!m_Heat)
                {
                    if (!On)
                    {
                        return;
                    }
                    // Destroyed together with the stripped item component: put an equal one back.
                    m_Heat = gameObject.AddComponent<HeatSource>();
                    m_Heat.m_MaxTempIncrease = m_HeatMaxIncrease;
                    m_Heat.m_MaxTempIncreaseInnerRadius = m_HeatInnerRadius;
                    m_Heat.m_MaxTempIncreaseOuterRadius = m_HeatOuterRadius;
                    m_Heat.m_TimeToReachMaxTempMinutes = m_HeatMinutesToMax;
                }
                if (On && !m_Heat.IsTurnedOn())
                {
                    m_Heat.TurnOn();
                }
                else if (!On && m_Heat.IsTurnedOn())
                {
                    m_Heat.TurnOffImmediate();
                }
            }
            catch (Exception e)
            {
                Logger.Log(ConsoleColor.Yellow, $"[RemoteLight] Heat source failed on {m_GearName}, heat disabled: {e.Message}");
                m_HasHeat = false;
                m_Heat = null;
            }
        }

        void OnEnable()
        {
            // HeatSource.Start may reset its state on first activation; Update re-applies it every 0.5 s too.
            if (!m_Failed)
            {
                ApplyHeat();
            }
        }

        void OnDisable()
        {
            if (!m_Heat)
            {
                return;
            }
            try
            {
                if (m_Heat.IsTurnedOn())
                {
                    m_Heat.TurnOffImmediate();
                }
            }
            catch
            {
                m_HasHeat = false;
                m_Heat = null;
            }
        }

        private static bool IsOutdoorLighting()
        {
            try
            {
                Weather W = GameManager.GetWeatherComponent();
                if (W)
                {
                    return W.UseOutdoorLightingForLightSources();
                }
            }
            catch
            {
            }
            return true;
        }

        void Update()
        {
            if (m_Failed)
            {
                return;
            }
            try
            {
                // Re-apply object states regularly, not only on mode change: game code can still toggle
                // the FX after setup (for example the stripped lamp item's OnDisable/OnDestroy at end of frame).
                if (Time.time >= m_NextEnvironmentCheck)
                {
                    m_NextEnvironmentCheck = Time.time + 0.5f;
                    if (m_Mode > c_ModeOff && IsBurntOut())
                    {
                        ApplyMode(c_ModeOff, false);
                    }
                    m_Outdoor = IsOutdoorLighting();
                    RefreshObjects();
                    ApplyHeat();
                }
                if (m_Mode <= c_ModeOff)
                {
                    return;
                }
                ApplyIntensities();

                if (m_DiagnoseAt > 0 && Time.time >= m_DiagnoseAt)
                {
                    m_DiagnoseAt = -1;
                    Diagnose();
                }
            }
            catch (Exception e)
            {
                // Called every frame from native code: report once and stop instead of spamming the log.
                m_Failed = true;
                Logger.Log(ConsoleColor.Red, $"[RemoteLight] Update failed for {m_GearName}, light rig disabled: {e}");
            }
        }

        void LateUpdate()
        {
            // The flashlight beam follows where its holder looks, not how the model sits in the hand.
            if (m_Failed || m_Mode <= c_ModeOff || m_Kind != LightKind.Flashlight || !m_Holder)
            {
                return;
            }
            try
            {
                Quaternion Aim = m_Holder.transform.rotation * Quaternion.Euler(m_Holder.m_Tilt, 0f, 0f);
                foreach (RigLight Rig in m_Lights)
                {
                    if (Rig.m_Light && Rig.m_Light.type == LightType.Spot)
                    {
                        Rig.m_Light.transform.rotation = Aim;
                    }
                }
            }
            catch (Exception e)
            {
                m_Failed = true;
                Logger.Log(ConsoleColor.Red, $"[RemoteLight] LateUpdate failed for {m_GearName}, light rig disabled: {e}");
            }
        }

        /// <summary>One log entry per gear name, holder kind and mode, so the first test run gives tuning data.</summary>
        private void Diagnose()
        {
            try
            {
                System.Text.StringBuilder Sb = new System.Text.StringBuilder();
                Sb.Append($"[RemoteLight] {m_GearName} kind={m_Kind} {(m_Held ? "held" : "ground")} mode={m_Mode} outdoor={m_Outdoor} beams={m_Beams.Count}");
                Sb.Append($" heat={(m_Heat ? (m_Heat.IsTurnedOn() ? "on" : "off") : "none")} finishAt={m_FinishAt:F2} now={ServerTime():F2}");
                for (int i = 0; i < m_Beams.Count; i++)
                {
                    Beam B = m_Beams[i];
                    Sb.Append($"\n  beam{i}: root={Name(B.m_Root)} indoor={Name(B.m_Indoor)} outdoor={Name(B.m_Outdoor)}");
                }
                foreach (RigLight Rig in m_Lights)
                {
                    if (!Rig.m_Light)
                    {
                        Sb.Append($"\n  light {Rig.m_Path}: destroyed");
                        continue;
                    }
                    Light L = Rig.m_Light;
                    Sb.Append($"\n  light {Rig.m_Path}: activeInHierarchy={L.gameObject.activeInHierarchy} enabled={L.enabled} type={L.type} intensity={L.intensity:F2} base={Rig.m_BaseIntensity:F2} range={L.range:F1} tracking={(bool)Rig.m_Tracking}");
                }
                Logger.Log(ConsoleColor.Cyan, Sb.ToString());
            }
            catch (Exception e)
            {
                Logger.Log(ConsoleColor.Red, $"[RemoteLight] Diagnose failed: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- Helpers

        [HideFromIl2Cpp]
        private static GameObject ToObject(Component C)
        {
            return C ? C.gameObject : null;
        }

        [HideFromIl2Cpp]
        private static GameObject ToObject(GameObject G)
        {
            return G ? G : null;
        }

        [HideFromIl2Cpp]
        private static GameObject ParentOf(GameObject G)
        {
            if (G && G.transform.parent)
            {
                return G.transform.parent.gameObject;
            }
            return null;
        }

        [HideFromIl2Cpp]
        private static GameObject FindChild(Transform Root, string Name)
        {
            foreach (Transform T in Root.GetComponentsInChildren<Transform>(true))
            {
                if (T.name == Name)
                {
                    return T.gameObject;
                }
            }
            return null;
        }

        [HideFromIl2Cpp]
        private static string GetPath(Transform Root, Transform T)
        {
            List<string> Parts = new List<string>();
            while (T && T.Pointer != Root.Pointer)
            {
                Parts.Insert(0, T.name);
                T = T.parent;
            }
            return string.Join("/", Parts);
        }

        [HideFromIl2Cpp]
        private static string Name(GameObject G)
        {
            return G ? G.name : "-";
        }
    }
}
