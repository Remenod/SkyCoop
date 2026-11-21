using Il2Cpp;
using Il2CppTLD.PDID;
using UnityEngine;

namespace SkyCoop
{
    /// <summary>
    /// Read-only check of the GUIDs that multiplayer sync relies on, for the scenes loaded right now.
    /// Runs after every scene load in multiplayer (short summary) and from the console command
    /// "guidcheck" (full lists). Writes to the MelonLoader log, lines start with [GUID].
    ///
    /// What it reports:
    ///   - ObjectGuid components with an empty id, and ids used by more than one object. Sync messages
    ///     find their object through PdidTable by id, so a shared id sends them to the wrong object.
    ///   - Synced objects without an ObjectGuid (containers, doors, breakdowns, harvestables, water sources,
    ///     beds, vehicle doors): those cannot be synced and are left to the local game.
    ///   - Scene transitions (LoadScene, LoadSceneParent) sharing a GUID: the game finds the exit door by it.
    /// </summary>
    public static class GuidAudit
    {
        private const int c_ShortListLimit = 10;

        public static void Run(bool Verbose)
        {
            try
            {
                int ListLimit = Verbose ? int.MaxValue : c_ShortListLimit;
                List<string> Details = new List<string>();

                // ObjectGuid ids.
                Dictionary<string, List<string>> ById = new Dictionary<string, List<string>>();
                int Total = 0;
                int Empty = 0;
                foreach (ObjectGuid ObjGuid in UnityEngine.Object.FindObjectsOfType<ObjectGuid>(true))
                {
                    Total++;
                    string Id = ObjGuid.GetPDID();
                    if (string.IsNullOrEmpty(Id))
                    {
                        Empty++;
                        AddLimited(Details, $"  empty id: {Describe(ObjGuid.gameObject)}", ListLimit);
                        continue;
                    }
                    List<string> Owners;
                    if (!ById.TryGetValue(Id, out Owners))
                    {
                        Owners = new List<string>();
                        ById.Add(Id, Owners);
                    }
                    Owners.Add(Describe(ObjGuid.gameObject));
                }
                int DuplicateIds = 0;
                foreach (KeyValuePair<string, List<string>> Pair in ById)
                {
                    if (Pair.Value.Count > 1)
                    {
                        DuplicateIds++;
                        AddLimited(Details, $"  shared id {Pair.Key} x{Pair.Value.Count}: {string.Join(" | ", Pair.Value)}", ListLimit);
                    }
                }

                // Synced object types that need an ObjectGuid on the same object (as the sync code reads it).
                int Missing = 0;
                Missing += CountWithoutGuid<Container>("Container", Details, ListLimit);
                Missing += CountWithoutGuid<OpenClose>("OpenClose", Details, ListLimit);
                Missing += CountWithoutGuid<BreakDown>("BreakDown", Details, ListLimit);
                Missing += CountWithoutGuid<Harvestable>("Harvestable", Details, ListLimit);
                Missing += CountWithoutGuid<WaterSource>("WaterSource", Details, ListLimit);
                Missing += CountWithoutGuid<Bed>("Bed", Details, ListLimit);
                Missing += CountWithoutGuid<VehicleDoor>("VehicleDoor", Details, ListLimit);

                // Scene transitions. A LoadScene with a parent shares the parent's GUID on purpose.
                Dictionary<string, List<string>> Transitions = new Dictionary<string, List<string>>();
                int TransitionCount = 0;
                foreach (LoadScene Door in UnityEngine.Object.FindObjectsOfType<LoadScene>(true))
                {
                    if (Door.m_LoadSceneParent != null)
                    {
                        continue;
                    }
                    TransitionCount++;
                    AddTransition(Transitions, Door.m_GUID, $"LoadScene {Describe(Door.gameObject)}");
                }
                foreach (LoadSceneParent Parent in UnityEngine.Object.FindObjectsOfType<LoadSceneParent>(true))
                {
                    TransitionCount++;
                    AddTransition(Transitions, Parent.m_GUID, $"LoadSceneParent {Describe(Parent.gameObject)}");
                }
                int DuplicateTransitions = 0;
                foreach (KeyValuePair<string, List<string>> Pair in Transitions)
                {
                    if (Pair.Value.Count > 1)
                    {
                        DuplicateTransitions++;
                        AddLimited(Details, $"  shared transition GUID {Pair.Key} x{Pair.Value.Count}: {string.Join(" | ", Pair.Value)}", ListLimit);
                    }
                }

                bool Clean = Empty == 0 && DuplicateIds == 0 && Missing == 0 && DuplicateTransitions == 0;
                string Summary = $"[GUID] {LoadedScenes()}: {Total} ObjectGuid (empty {Empty}, shared ids {DuplicateIds}), "
                    + $"synced objects without GUID {Missing}, transitions {TransitionCount} (shared {DuplicateTransitions})";
                Logger.Log(Clean ? ConsoleColor.Green : ConsoleColor.Yellow, Summary);
                foreach (string Line in Details)
                {
                    Logger.Log(ConsoleColor.Yellow, Line);
                }
                if (!Verbose && Details.Count >= c_ShortListLimit)
                {
                    Logger.Log(ConsoleColor.Yellow, "  ... list cut, run the console command guidcheck for everything");
                }
            }
            catch (Exception e)
            {
                Logger.Log(ConsoleColor.Red, $"[GUID] Audit failed: {e}");
            }
        }

        private static int CountWithoutGuid<T>(string Kind, List<string> Details, int ListLimit) where T : Component
        {
            int Count = 0;
            foreach (T C in UnityEngine.Object.FindObjectsOfType<T>(true))
            {
                ObjectGuid ObjGuid = C.GetComponent<ObjectGuid>();
                if (ObjGuid && !string.IsNullOrEmpty(ObjGuid.GetPDID()))
                {
                    continue;
                }
                Count++;
                AddLimited(Details, $"  {Kind} without GUID: {Describe(C.gameObject)}", ListLimit);
            }
            return Count;
        }

        private static void AddTransition(Dictionary<string, List<string>> Transitions, string Id, string What)
        {
            if (string.IsNullOrEmpty(Id))
            {
                Id = "<empty>";
            }
            List<string> Owners;
            if (!Transitions.TryGetValue(Id, out Owners))
            {
                Owners = new List<string>();
                Transitions.Add(Id, Owners);
            }
            Owners.Add(What);
        }

        private static void AddLimited(List<string> Details, string Line, int ListLimit)
        {
            if (Details.Count < ListLimit)
            {
                Details.Add(Line);
            }
        }

        private static string Describe(GameObject Obj)
        {
            if (!Obj)
            {
                return "<destroyed>";
            }
            string Path = Obj.name;
            Transform Parent = Obj.transform.parent;
            int Depth = 0;
            while (Parent && Depth < 3)
            {
                Path = $"{Parent.name}/{Path}";
                Parent = Parent.parent;
                Depth++;
            }
            if (Parent)
            {
                Path = $".../{Path}";
            }
            Vector3 P = Obj.transform.position;
            return FormattableString.Invariant($"{Obj.scene.name}:{Path} ({P.x:F1}, {P.y:F1}, {P.z:F1})");
        }

        private static string LoadedScenes()
        {
            List<string> Names = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                Names.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name);
            }
            return string.Join("+", Names);
        }
    }
}
