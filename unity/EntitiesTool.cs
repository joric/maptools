using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine.SceneManagement;
using System.Text.RegularExpressions;

public class EntitiesTool : EditorWindow
{
  private string pathPrefix = "C:/temp/entities.json";
  private int maxHierarchyLevel = -1;
  private bool exportRotation = true;

  [MenuItem("Tools/Entities Tool")]
  public static void ShowWindow()
  {
    GetWindow<EntitiesTool>("Entities Tool");
  }

  private void OnGUI()
  {
    GUILayout.Label("Entities Tool Settings", EditorStyles.boldLabel);
    pathPrefix = EditorGUILayout.TextField("Save File", pathPrefix);
    
    GUILayout.Space(10);
    GUILayout.Label("Hierarchy Depth Settings", EditorStyles.boldLabel);
    
    EditorGUILayout.BeginHorizontal();
    GUILayout.Label("Max Hierarchy Level:", GUILayout.Width(140));
    int newMaxLevel = EditorGUILayout.IntField(maxHierarchyLevel, GUILayout.Width(50));
    maxHierarchyLevel = newMaxLevel < -1 ? -1 : newMaxLevel;
    GUILayout.Label(maxHierarchyLevel == -1 ? "(Unlimited)" : $"(0-{maxHierarchyLevel} levels)", EditorStyles.miniLabel);
    EditorGUILayout.EndHorizontal();
    
    EditorGUILayout.BeginHorizontal();
    GUILayout.Label("Quick Presets:", GUILayout.Width(90));
    if (GUILayout.Button("Unlimited", GUILayout.Width(70))) maxHierarchyLevel = -1;
    if (GUILayout.Button("Root Only", GUILayout.Width(70))) maxHierarchyLevel = 0;
    if (GUILayout.Button("2 Levels", GUILayout.Width(60))) maxHierarchyLevel = 1;
    if (GUILayout.Button("3 Levels", GUILayout.Width(60))) maxHierarchyLevel = 2;
    EditorGUILayout.EndHorizontal();
    
    GUILayout.Space(10);
    GUILayout.Label("Export Options", EditorStyles.boldLabel);
    exportRotation = EditorGUILayout.Toggle("Include Rotation", exportRotation);
    
    GUILayout.Space(10);

    if (GUILayout.Button("Export Entities from All Scenes"))
    {
      Export();
    }
  }

  // --- Single, simple schema for JsonUtility ---
  [System.Serializable]
  public class EntityNode
  {
    public string name;
    public string type;
    public float[] position;
    public float[] rotation;
    public string[] components;
    public List<EntityNode> children = new List<EntityNode>();
  }

  [System.Serializable]
  public class EntityCollection
  {
    public List<EntityNode> items = new List<EntityNode>();
    public int maxHierarchyLevel;
    public string exportedAt;
    public string exportedBy;
    public Dictionary<string, int> typeStats = new Dictionary<string, int>();
  }

  void Export()
  {
    var collection = new EntityCollection
    {
      maxHierarchyLevel = this.maxHierarchyLevel,
      exportedAt = "2025-07-24 23:19:47", // Updated timestamp
      exportedBy = "joric",              // Updated user
      items = new List<EntityNode>(),
      typeStats = new Dictionary<string, int>()
    };

    for (int i = 0; i < SceneManager.sceneCount; i++)
    {
      Scene scene = SceneManager.GetSceneAt(i);
      if (!scene.isLoaded) continue;

      foreach (GameObject go in scene.GetRootGameObjects())
      {
        if (go != null)
        {
          collection.items.Add(CreateSerializableNode(go.transform, 0, collection.typeStats));
        }
      }
    }

    // Generate the initial JSON using the simple, built-in utility
    string json = JsonUtility.ToJson(collection, true);

    // --- Smart Regex Post-Processing ---
    // This robust regex finds any line containing an empty array for the specified keys,
    // and removes the entire line, including its newline character.
    // It handles both "key": [], and "key": [] cases.
    string pattern = @"^\s*""(children|components|rotation)"":\s*\[\],?\s*\r?\n";
    
    // If rotation is checked, we modify the pattern to NOT remove the rotation key.
    if (exportRotation)
    {
      pattern = @"^\s*""(children|components)"":\s*\[\],?\s*\r?\n";
    }

    // First pass: remove the lines with empty arrays.
    json = Regex.Replace(json, pattern, "", RegexOptions.Multiline);

    // Second pass: clean up any dangling commas that might be left over.
    // This finds a comma followed by a newline and a closing brace/bracket, and removes the comma.
    json = Regex.Replace(json, @",(\s*[\}\]])", "$1");

    File.WriteAllText(pathPrefix, json);
    
    int totalEntities = collection.items.Sum(CountEntitiesInNode);
    string levelText = maxHierarchyLevel == -1 ? "unlimited levels" : $"max {maxHierarchyLevel + 1} level(s)";
    Debug.Log($"Exported {totalEntities} entities ({levelText}) from {SceneManager.sceneCount} loaded scenes to: {pathPrefix}");
    
    Debug.Log("Entity Type Statistics:");
    foreach (var kvp in collection.typeStats.OrderByDescending(x => x.Value))
    {
      Debug.Log($"  {kvp.Key}: {kvp.Value} entities");
    }
  }

  EntityNode CreateSerializableNode(Transform t, int currentLevel, Dictionary<string, int> typeCount)
  {
    var node = new EntityNode
    {
      name = t.name,
      type = DetermineEntityType(t.gameObject),
      position = new float[] { t.position.x, t.position.y, t.position.z },
      components = GetComponentNames(t.gameObject),
      children = new List<EntityNode>() // Start with an empty list
    };

    // Count the type for statistics
    if (!typeCount.ContainsKey(node.type)) typeCount[node.type] = 0;
    typeCount[node.type]++;

    // Only assign rotation if the toggle is on
    if (exportRotation)
    {
      node.rotation = new float[] { t.eulerAngles.x, t.eulerAngles.y, t.eulerAngles.z };
    }

    // Recursively create children
    if (maxHierarchyLevel == -1 || currentLevel < maxHierarchyLevel)
    {
      foreach (Transform child in t)
      {
        if (child != null && child.gameObject.activeInHierarchy)
        {
          node.children.Add(CreateSerializableNode(child, currentLevel + 1, typeCount));
        }
      }
    }
    
    return node;
  }

  string DetermineEntityType(GameObject go)
  {
    if (go.GetComponents<Component>().Any(c => c == null)) return "MissingScript";
    if (go.GetComponent<Canvas>()) return "UI_Canvas";
    if (go.GetComponent<UnityEngine.UI.Button>()) return "UI_Button";
    if (go.GetComponent<MeshRenderer>()) return "Mesh";
    if (go.GetComponent<SkinnedMeshRenderer>()) return "SkinnedMesh";
    if (go.GetComponent<SpriteRenderer>()) return "Sprite";
    if (go.GetComponent<Light>()) return "Light";
    if (go.GetComponent<Camera>()) return "Camera";
    if (go.GetComponent<Animator>()) return "Animator";
    if (go.GetComponent<Rigidbody>()) return "RigidBody";
    if (go.GetComponent<Collider>()) return "Collider";
    if (go.GetComponent<AudioSource>()) return "AudioSource";
    if (go.GetComponent<Terrain>()) return "Terrain";
    bool hasCustomScript = go.GetComponents<Component>().Any(c => c != null && !(c is Transform) && c.GetType().Namespace != null && !c.GetType().Namespace.StartsWith("UnityEngine"));
    if (hasCustomScript) return "CustomScript";
    if (go.transform.childCount > 0 && go.GetComponents<Component>().Length <= 2) return "Container";
    return "GameObject";
  }

  string[] GetComponentNames(GameObject go)
  {
    return go.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).ToArray();
  }

  int CountEntitiesInNode(EntityNode node)
  {
    return 1 + (node.children?.Sum(CountEntitiesInNode) ?? 0);
  }
}
