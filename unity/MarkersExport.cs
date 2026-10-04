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

var ALLOWED_FIELDS = new[] { "m_color" };

var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

var findField = new System.Func<object, string, System.Reflection.FieldInfo>((obj, name) => {
    if (obj == null) return null;
    var type = obj.GetType();
    while (type != null) {
        var field = type.GetField(name, flags);
        if (field != null) return field;
        type = type.BaseType;
    }
    return null;
});

var getField = new System.Func<object, string, object>((obj, name) => {
    var field = findField(obj, name);
    if (field == null) return null;
    try { return field.GetValue(obj); } catch { return null; }
});

var serializeValue = new System.Func<object, string>((value) => {
    if (value == null) return null;

    if (value is UnityEngine.Color) {
        var c = (UnityEngine.Color)value;
        return Newtonsoft.Json.JsonConvert.SerializeObject(new {
            r = c.r,
            g = c.g,
            b = c.b,
            a = c.a
        });
    }

    if (value is UnityEngine.Vector2) {
        var v = (UnityEngine.Vector2)value;
        return Newtonsoft.Json.JsonConvert.SerializeObject(new { x = v.x, y = v.y });
    }

    if (value is UnityEngine.Vector3) {
        var v = (UnityEngine.Vector3)value;
        return Newtonsoft.Json.JsonConvert.SerializeObject(new { x = v.x, y = v.y, z = v.z });
    }

    if (value is UnityEngine.Vector4) {
        var v = (UnityEngine.Vector4)value;
        return Newtonsoft.Json.JsonConvert.SerializeObject(new { x = v.x, y = v.y, z = v.z, w = v.w });
    }

    if (value is UnityEngine.Quaternion) {
        var q = (UnityEngine.Quaternion)value;
        return Newtonsoft.Json.JsonConvert.SerializeObject(new { x = q.x, y = q.y, z = q.z, w = q.w });
    }

    try {
        return Newtonsoft.Json.JsonConvert.SerializeObject(value, Newtonsoft.Json.Formatting.None);
    } catch {
        return null;
    }
});

var getAllowedFields = new System.Func<UnityEngine.Component, System.Collections.Generic.Dictionary<string, object>>((component) => {
    var result = new System.Collections.Generic.Dictionary<string, object>();

    if (component == null) return result;

    for (int i = 0; i < ALLOWED_FIELDS.Length; i++) {
        var fieldName = ALLOWED_FIELDS[i];
        var field = findField(component, fieldName);

        if (field == null) continue;

        try {
            var value = field.GetValue(component);
            if (value != null) result[fieldName] = value;
        } catch {}
    }

    return result;
});

var hasRequiredComponents = new System.Func<UnityEngine.GameObject, bool>((go) => {
    for (int r = 0; r < REQUIRED_COMPONENTS.Length; r++) {
        var wanted = REQUIRED_COMPONENTS[r];
        var found = false;

        foreach (var component in go.GetComponents<UnityEngine.Component>()) {
            if (component == null) continue;

            if (component.GetType().Name == wanted) {
                found = true;
                break;
            }
        }

        if (!found) return false;
    }

    return true;
});

var matchesName = new System.Func<UnityEngine.GameObject, bool>((go) => {
    if (go == null || NAME_FILTERS.Length == 0) return false;

    for (int i = 0; i < NAME_FILTERS.Length; i++) {
        if (!string.IsNullOrEmpty(NAME_FILTERS[i]) &&
            go.name.IndexOf(NAME_FILTERS[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
    }

    return false;
});

var matches = new System.Collections.Generic.List<UnityEngine.Transform>();
var traversalStack = new System.Collections.Generic.Stack<UnityEngine.Transform>();

foreach (var root in roots)
    traversalStack.Push(root.transform);

while (traversalStack.Count > 0) {
    var current = traversalStack.Pop();
    var go = current.gameObject;

    if (hasRequiredComponents(go) || matchesName(go))
        matches.Add(current);

    for (int i = 0; i < current.childCount; i++)
        traversalStack.Push(current.GetChild(i));
}

var mgr = UnityEngine.Object.FindObjectOfType<ES3Internal.ES3ReferenceMgrBase>();
var features = new System.Collections.Generic.List<object>();

for (int index = 0; index < matches.Count; index++) {
    var transform = matches[index];
    var go = transform.gameObject;

    long es3 = -1;
    if (mgr != null) {
        try { es3 = mgr.Get(go); } catch {}
    }

    var pathParts = new System.Collections.Generic.List<string>();
    var parent = transform.parent;

    while (parent != null) {
        pathParts.Add(parent.name);
        parent = parent.parent;
    }

    pathParts.Reverse();

    var path = string.Join("/", pathParts.ToArray());

    var properties = new System.Collections.Generic.Dictionary<string, object>();

    if (EXPORT_ES3) {
        if (es3 >= 0)
            properties["es3ref"] = es3;
    }

    properties["name"] = go.name;
    properties["path"] = path;

    if (EXPORT_ACTIVE)
        properties["active"] = go.activeSelf;

    if (EXPORT_SCENE)
        properties["scene"] = scene.name;

    if (EXPORT_COMPONENTS) {
        var componentNames = new System.Collections.Generic.List<string>();

        foreach (var component in go.GetComponents<UnityEngine.Component>()) {
            if (component == null || component is UnityEngine.Transform) continue;
            componentNames.Add(component.GetType().Name);
        }

        properties["components"] = componentNames;
    }

    var reflected = new System.Collections.Generic.Dictionary<string, object>();

    foreach (var component in go.GetComponents<UnityEngine.Component>()) {
        if (component == null || component is UnityEngine.Transform) continue;

        var fields = getAllowedFields(component);

        foreach (var pair in fields) {
            if (!reflected.ContainsKey(pair.Key))
                reflected[pair.Key] = pair.Value;
        }
    }

    foreach (var pair in reflected)
        properties[pair.Key] = pair.Value;

    var pos = transform.position;

    features.Add(new {
        type = "Feature",
        geometry = new {
            type = "Point",
            coordinates = new[] { pos.x, pos.y, pos.z }
        },
        properties = properties
    });

    if ((index + 1) % 1000 == 0 || index + 1 == matches.Count) {
        System.IO.File.WriteAllText(
            progress,
            "EXPORTING\n" +
            (index + 1) + "/" + matches.Count + " (" + ((index + 1) * 100 / matches.Count) + "%)\n" +
            "Output: " + output
        );
    }
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
    matches.Count + "/" + matches.Count + " (100%)\n" +
    "Output: " + output
);

"DONE: " + matches.Count + " objects exported to " + output;
