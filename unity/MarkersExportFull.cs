var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
var roots = scene.GetRootGameObjects();
var output = @"C:\Temp\markers.json";
var progress = @"C:\Temp\markers.progress";

var REQUIRED_COMPONENTS = new[] { "ES3AutoSave", "Outlinable" };
var NAME_FILTERS = new[] { "MainMapBorder" };

var EXPORT_ACTIVE = false;
var EXPORT_SCENE = false;
var EXPORT_COMPONENTS = false;
var EXPORT_ES3 = false;

var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

var field = new System.Func<object,string,object>((o,n) => {
    if (o == null) return null;
    for (var t = o.GetType(); t != null; t = t.BaseType) {
        var f = t.GetField(n, flags);
        if (f != null) try { return f.GetValue(o); } catch {}
    }
    return null;
});

var prop = new System.Func<object,string,object>((o,n) => {
    if (o == null) return null;
    for (var t = o.GetType(); t != null; t = t.BaseType) {
        var p = t.GetProperty(n, flags);
        if (p != null && p.GetIndexParameters().Length == 0) try { return p.GetValue(o, null); } catch {}
    }
    return null;
});

var locKey = new System.Func<object,string>(o => {
    if (o == null) return null;

    var ls = o as UnityEngine.Localization.LocalizedString;
    if (ls != null) {
        try {
            var r = prop(ls, "TableEntryReference");
            if (r != null) {
                var k = prop(r, "Key") as string;
                if (!string.IsNullOrEmpty(k)) return k;
            }
        } catch {}
    }

    var s = o.ToString();
    if (string.IsNullOrEmpty(s)) return null;

    var marker = "TableEntryReference(";
    var p = s.IndexOf(marker, StringComparison.Ordinal);
    if (p < 0) return null;

    p += marker.Length;
    var e = s.IndexOf(')', p);
    if (e < 0) return null;

    var x = s.Substring(p, e - p);
    var d = x.IndexOf(" - ", StringComparison.Ordinal);
    if (d >= 0) x = x.Substring(d + 3);

    return x.Trim().TrimEnd(')');
});

var findLocKey = new System.Func<object,string>(o => {
    if (o == null) return null;

    var k = locKey(o);
    if (!string.IsNullOrEmpty(k)) return k;

    var visited = new System.Collections.Generic.HashSet<object>();

    System.Func<object,int,string> scan = null;
    scan = (v, depth) => {
        if (v == null || depth < 0) return null;

        var direct = locKey(v);
        if (!string.IsNullOrEmpty(direct)) return direct;

        if (v is UnityEngine.Object && !(v is UnityEngine.Localization.LocalizedString))
            return null;

        var type = v.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(string))
            return null;

        if (!type.IsValueType) {
            if (visited.Contains(v)) return null;
            visited.Add(v);
        }

        foreach (var f in type.GetFields(flags)) {
            if (f.IsStatic) continue;

            object x;
            try { x = f.GetValue(v); } catch { continue; }
            if (x == null) continue;

            var result = scan(x, depth - 1);
            if (!string.IsNullOrEmpty(result)) return result;
        }

        foreach (var p in type.GetProperties(flags)) {
            if (p.GetIndexParameters().Length > 0) continue;

            object x;
            try { x = p.GetValue(v, null); } catch { continue; }
            if (x == null) continue;

            var result = scan(x, depth - 1);
            if (!string.IsNullOrEmpty(result)) return result;
        }

        return null;
    };

    return scan(o, 3);
});

var hasRequired = new System.Func<UnityEngine.GameObject,bool>(go => {
    foreach (var wanted in REQUIRED_COMPONENTS) {
        var found = false;

        foreach (var c in go.GetComponents<UnityEngine.Component>()) {
            if (c == null) continue;

            var type = c.GetType();

            if (type.Name == wanted || type.FullName == wanted) {
                found = true;
                break;
            }

            if (wanted == "Outlinable" && type.FullName == "EPOOutline.Outlinable")
                found = true;
        }

        if (!found) return false;
    }

    return true;
});

var nameMatch = new System.Func<UnityEngine.GameObject,bool>(go => {
    if (go == null) return false;

    foreach (var s in NAME_FILTERS) {
        if (!string.IsNullOrEmpty(s) && go.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
    }

    return false;
});

var matches = new System.Collections.Generic.List<UnityEngine.Transform>();
var walk = new System.Collections.Generic.Stack<UnityEngine.Transform>();

foreach (var root in roots)
    walk.Push(root.transform);

while (walk.Count > 0) {
    var t = walk.Pop();
    var go = t.gameObject;

    if (hasRequired(go) || nameMatch(go))
        matches.Add(t);

    for (int i = 0; i < t.childCount; i++)
        walk.Push(t.GetChild(i));
}

var mgr = UnityEngine.Object.FindObjectOfType<ES3Internal.ES3ReferenceMgrBase>();
var features = new System.Collections.Generic.List<object>();

for (int index = 0; index < matches.Count; index++) {
    var t = matches[index];
    var go = t.gameObject;

    var properties = new System.Collections.Generic.Dictionary<string,object>();

    if (EXPORT_ES3 && mgr != null) {
        try {
            var id = mgr.Get(go);
            if (id >= 0) properties["es3ref"] = id;
        } catch {}
    }

    properties["name"] = go.name;

    var path = new System.Collections.Generic.List<string>();
    for (var p = t.parent; p != null; p = p.parent)
        path.Add(p.name);

    path.Reverse();
    properties["path"] = string.Join("/", path.ToArray());

    if (EXPORT_ACTIVE)
        properties["active"] = go.activeSelf;

    if (EXPORT_SCENE)
        properties["scene"] = scene.name;

    if (EXPORT_COMPONENTS) {
        var names = new System.Collections.Generic.List<string>();

        foreach (var c in go.GetComponents<UnityEngine.Component>()) {
            if (c != null && !(c is UnityEngine.Transform))
                names.Add(c.GetType().Name);
        }

        properties["components"] = names;
    }

    string key = null;

    foreach (var c in go.GetComponents<UnityEngine.Component>()) {
        if (c == null || c is UnityEngine.Transform)
            continue;

        var type = c.GetType();

        // First try the fields that normally contain the item's localized name.
        var candidates = new[] {
            "m_PartName",
            "m_ItemName",
            "m_SprayName",
            "m_Name",
            "Name"
        };

        foreach (var name in candidates) {
            var value = field(c, name);

            if (value == null)
                value = prop(c, name);

            if (value == null)
                continue;

            var k = findLocKey(value);

            if (!string.IsNullOrEmpty(k)) {
                key = k;
                break;
            }
        }

        if (!string.IsNullOrEmpty(key))
            break;

        // CarPartBehaviour -> Part -> m_PartName.
        if (type.FullName == "Fuszerka.Vehicles.CarPartBehaviour") {
            var part = prop(c, "Part");

            if (part == null)
                part = field(c, "Part");

            if (part != null) {
                var value = field(part, "m_PartName");

                if (value != null)
                    key = findLocKey(value);
            }

            if (!string.IsNullOrEmpty(key))
                break;
        }

        // ItemIdentifier -> m_ItemDefinition -> Def.
        if (type.Name.IndexOf("ItemIdentifier", StringComparison.OrdinalIgnoreCase) >= 0) {
            var list = field(c, "m_ItemDefinition") as System.Collections.IEnumerable;

            if (list != null) {
                foreach (var item in list) {
                    if (item == null) continue;

                    var def = prop(item, "Def");

                    if (def == null)
                        def = field(item, "Def");

                    if (def != null) {
                        key = findLocKey(def);

                        if (string.IsNullOrEmpty(key))
                            key = findLocKey(prop(def, "Name"));

                        if (!string.IsNullOrEmpty(key))
                            break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(key))
                break;
        }

        // Generic fallback: inspect the component itself for a LocalizedString.
        key = findLocKey(c);

        if (!string.IsNullOrEmpty(key))
            break;
    }

    if (!string.IsNullOrEmpty(key))
        properties["key"] = key;

    var pos = t.position;

    features.Add(new {
        type = "Feature",
        geometry = new {
            type = "Point",
            coordinates = new[] { pos.x, pos.y, pos.z }
        },
        properties = properties
    });

    if ((index + 1) % 500 == 0 || index + 1 == matches.Count)
        System.IO.File.WriteAllText(
            progress,
            "EXPORTING\n" +
            (index + 1) + "/" + matches.Count + "\n" +
            "Output: " + output
        );
}

var geojson = new {
    type = "FeatureCollection",
    features = features
};

var json = Newtonsoft.Json.JsonConvert.SerializeObject(
    geojson,
    Newtonsoft.Json.Formatting.Indented
);

System.IO.File.WriteAllText(output, json, System.Text.Encoding.UTF8);

System.IO.File.WriteAllText(
    progress,
    "DONE\n" +
    matches.Count + "/" + matches.Count + "\n" +
    "Output: " + output
);

"DONE: " + matches.Count + " objects exported to " + output;
