using AssetsTools.NET;
using AssetsTools.NET.Extra;

// usage: PrefabDump <bundleDir> [--fields=Script1,Script2] <prefabName> [prefabName...]
// Finds root GameObjects with the given names in addressable bundles and dumps their hierarchy,
// Light settings and the fields of light-related MonoBehaviours.
var dir = args[0];
var wanted = new HashSet<string>(args.Skip(1).Where(a => !a.StartsWith("--")));
var interesting = new HashSet<string> { "KeroseneLampItem", "KeroseneLampIntensity", "FlashlightItem", "TorchItem", "TorchIntensity",
    "FlareItem", "FlareIntensity", "LightRandomIntensity", "MatchesItem", "HeatSource" };
foreach (var a in args.Where(a => a.StartsWith("--fields=")))
    foreach (var n in a.Substring("--fields=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries)) interesting.Add(n);
var am = new AssetsManager();
var found = new HashSet<string>();
var scripts = new Dictionary<(string, long), string>();
foreach (var path in Directory.GetFiles(dir, "*.bundle"))
{
    BundleFileInstance b;
    try { b = am.LoadBundleFile(path, true); } catch { continue; }
    for (int fi = 0; fi < b.file.BlockAndDirInfo.DirectoryInfos.Count; fi++)
    {
        AssetsFileInstance inst;
        try { inst = am.LoadAssetsFileFromBundle(b, fi, false); } catch { continue; }
        if (inst == null) continue;
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MonoScript))
        {
            try { scripts[(inst.name, info.PathId)] = am.GetBaseField(inst, info)["m_ClassName"].AsString; } catch { }
        }
    }
    am.UnloadAll();
}
Console.WriteLine($"monoscripts indexed: {scripts.Count}");
foreach (var path in Directory.GetFiles(dir, "*.bundle"))
{
        BundleFileInstance bun;
    try { bun = am.LoadBundleFile(path, true); } catch { continue; }
    for (int fi = 0; fi < bun.file.BlockAndDirInfo.DirectoryInfos.Count; fi++)
    {
        AssetsFileInstance inst;
        try { inst = am.LoadAssetsFileFromBundle(bun, fi, false); } catch { continue; }
        if (inst == null) continue;
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject))
        {
            AssetTypeValueField go;
            try { go = am.GetBaseField(inst, info); } catch { continue; }
            var name = go["m_Name"].AsString;
            if (!wanted.Contains(name)) continue;
            // only roots: transform has no father
            var trPtr = go["m_Component.Array"][0]["component"];
            var tr = am.GetExtAsset(inst, trPtr).baseField;
            if (tr == null || tr["m_Father.m_PathID"].AsLong != 0) continue;
            found.Add(name);
            Console.WriteLine($"######## {name}  (bundle {Path.GetFileName(path)}, typetree={inst.file.Metadata.TypeTreeEnabled})");
            Dump(inst, go, 0);
        }
    }
    am.UnloadAll();
}
Console.WriteLine("not found: " + string.Join(", ", wanted.Except(found)));

void Dump(AssetsFileInstance inst, AssetTypeValueField go, int depth)
{
    string pad = new string(' ', depth * 2);
    Console.WriteLine($"{pad}[GO] {go["m_Name"].AsString} active={go["m_IsActive"].AsBool} layer={go["m_Layer"].AsInt}");
    AssetTypeValueField transform = null;
    foreach (var c in go["m_Component.Array"])
    {
        var ext = am.GetExtAsset(inst, c["component"]);
        if (ext.baseField == null) { Console.WriteLine($"{pad}   - <unresolved>"); continue; }
        var cls = (AssetClassID)ext.info.TypeId;
        if (cls == AssetClassID.Transform || cls == AssetClassID.RectTransform) { transform = ext.baseField; continue; }
        if (cls == AssetClassID.MonoBehaviour)
        {
            string sname = ScriptName(ext.file, ext.baseField["m_Script"]);
            bool enabled = ext.baseField["m_Enabled"].AsByte != 0;
            Console.WriteLine($"{pad}   - MB {sname} enabled={enabled}");
            if (interesting.Contains(sname))
            {
                AssetTypeValueField mb = ext.baseField;
                try { mb = am.GetBaseField(ext.file, ext.info, AssetReadFlags.None); } catch { }
                foreach (var f in mb.Children)
                {
                    if (f.FieldName.StartsWith("m_GameObject") || f.FieldName == "m_Script" || f.FieldName == "m_Enabled" || f.FieldName == "m_EditorHideFlags" || f.FieldName == "m_Name" || f.FieldName == "m_EditorClassIdentifier") continue;
                    Console.WriteLine($"{pad}       {f.FieldName} = {Val(ext.file, f)}");
                }
            }
            continue;
        }
        if (cls == AssetClassID.Light)
        {
            var l = ext.baseField;
            Console.WriteLine($"{pad}   - Light enabled={l["m_Enabled"].AsByte} type={l["m_Type"].AsInt} intensity={l["m_Intensity"].AsFloat} range={l["m_Range"].AsFloat} spot={l["m_SpotAngle"].AsFloat} shadows={l["m_Shadows.m_Type"].AsInt} color=({l["m_Color.r"].AsFloat:F2},{l["m_Color.g"].AsFloat:F2},{l["m_Color.b"].AsFloat:F2}) cullMask={l["m_CullingMask.m_Bits"].AsUInt} renderMode={l["m_RenderMode"].AsInt}");
            continue;
        }
        Console.WriteLine($"{pad}   - {cls}");
    }
    if (transform == null) return;
    foreach (var ch in transform["m_Children.Array"])
    {
        var childTr = am.GetExtAsset(inst, ch).baseField;
        if (childTr == null) continue;
        var childGo = am.GetExtAsset(inst, childTr["m_GameObject"]).baseField;
        if (childGo != null) Dump(inst, childGo, depth + 1);
    }
}

string ScriptName(AssetsFileInstance file, AssetTypeValueField ptr)
{
    int fid = ptr["m_FileID"].AsInt; long pid = ptr["m_PathID"].AsLong;
    string cab = fid == 0 ? file.name : Path.GetFileName(file.file.Metadata.Externals[fid - 1].PathName);
    return scripts.TryGetValue((cab, pid), out var n) ? n : $"?({cab}:{pid})";
}

string Val(AssetsFileInstance file, AssetTypeValueField f)
{
    try
    {
        if (f.Children.Count == 2 && f.Children[0].FieldName == "m_FileID" && f.Children[1].FieldName == "m_PathID")
        {
            if (f["m_PathID"].AsLong == 0) return "null";
            var ext = am.GetExtAsset(file, f);
            if (ext.baseField == null) return $"ptr({f["m_PathID"].AsLong})";
            var cls = (AssetClassID)ext.info.TypeId;
            string goName = "";
            try
            {
                if (cls == AssetClassID.GameObject) goName = ext.baseField["m_Name"].AsString;
                else { var g = am.GetExtAsset(ext.file, ext.baseField["m_GameObject"]).baseField; goName = g?["m_Name"].AsString; }
            }
            catch { }
            return $"-> {cls} on '{goName}'";
        }
        if (f.Value != null) return f.Value.AsString ?? f.Value.ToString();
        if (f.Children.Count > 0 && f.Children.Count <= 4 && f.Children.All(c => c.Value != null))
            return "(" + string.Join(", ", f.Children.Select(c => c.FieldName + "=" + c.Value.ToString())) + ")";
        return $"<{f.TypeName}>";
    }
    catch (Exception e) { return "<err " + e.Message + ">"; }
}
