using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

public class TiledScreenshotTool : EditorWindow
{
    private string[] resolutions = { "2K", "4K", "8K", "16K", "32K" };
    private int[] sizes = { 2048, 4096, 8192, 16384, 32768 };
    private int selectedResolution = 4;
    private int tileSize = 4096;
    private string outputFolder = "C:/temp/tiles";
    private bool preserveCameraPosition = true;
    private bool stitchTiles = false;
    private int maxStitchResolution = 16384; // Maximum resolution for stitching
    private bool showAdvancedOptions = false;

    [MenuItem("Tools/Tiled Screenshot Tool")]
    public static void ShowWindow()
    {
        GetWindow<TiledScreenshotTool>("Tiled Screenshot Tool");
    }

    private void OnGUI()
    {
        GUILayout.Label("Tiled Screenshot Settings", EditorStyles.boldLabel);

        selectedResolution = EditorGUILayout.Popup("Final Resolution", selectedResolution, resolutions);
        tileSize = EditorGUILayout.IntField("Tile Size", tileSize);
        outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
        preserveCameraPosition = EditorGUILayout.Toggle("Preserve Camera Position", preserveCameraPosition);
        
        stitchTiles = EditorGUILayout.Toggle("Stitch Tiles", stitchTiles);
        
        if (stitchTiles)
        {
            EditorGUI.indentLevel++;
            showAdvancedOptions = EditorGUILayout.Foldout(showAdvancedOptions, "Advanced Stitching Options");
            
            if (showAdvancedOptions)
            {
                maxStitchResolution = EditorGUILayout.IntField("Max Stitch Resolution", maxStitchResolution);
                EditorGUILayout.HelpBox("If final resolution exceeds max stitch resolution, " +
                    "image will be downscaled to prevent memory issues.", MessageType.Info);
            }
            EditorGUI.indentLevel--;
        }

        int finalSize = sizes[selectedResolution];
        int tilesPerSide = Mathf.CeilToInt((float)finalSize / tileSize);
        
        EditorGUILayout.HelpBox(
            "This tool will create " + tilesPerSide + "x" + tilesPerSide + " tiles of " + 
            tileSize + "x" + tileSize + " pixels.\n" +
            "Final resolution: " + finalSize + "x" + finalSize + " pixels.", 
            MessageType.Info
        );

        if (GUILayout.Button("Capture Tiled Screenshot"))
        {
            CaptureTiled();
        }
    }

    private void CaptureTiled()
    {
        // Get the active camera
        Camera cam = null;
        foreach (var c in Camera.allCameras)
        {
            if (c.isActiveAndEnabled)
            {
                cam = c;
                break;
            }
        }

        if (cam == null)
        {
            Debug.LogError("No active camera found.");
            return;
        }

        // Check if the camera is orthographic
        if (!cam.orthographic)
        {
            Debug.LogError("Camera must be orthographic for tiled screenshots.");
            return;
        }

        // Create output directory if it doesn't exist
        if (!Directory.Exists(outputFolder))
        {
            Directory.CreateDirectory(outputFolder);
        }

        int finalSize = sizes[selectedResolution];
        int tilesPerSide = Mathf.CeilToInt((float)finalSize / tileSize);
        float originalSize = cam.orthographicSize;
        Vector3 originalPosition = cam.transform.position;
        
        // Calculate the size of each tile in world units
        float tileWorldSize = originalSize * 2 * (tileSize / (float)finalSize * tilesPerSide);
        float tileStep = tileWorldSize / tilesPerSide;

        Debug.Log($"Creating {tilesPerSide}x{tilesPerSide} tiles of {tileSize}x{tileSize} pixels");
        Debug.Log($"Original camera orthographicSize: {originalSize}, position: {originalPosition}");
        Debug.Log($"Tile world size: {tileWorldSize}, tile step: {tileStep}");

        // Store camera settings
        RenderTexture originalTargetTexture = cam.targetTexture;
        
        // Create render texture for tiles
        RenderTexture rt = new RenderTexture(tileSize, tileSize, 24, RenderTextureFormat.ARGB32);
        rt.antiAliasing = 8; // 8x MSAA
        
        // Calculate the starting point (bottom-left corner in X-Z plane)
        Vector3 startPosition = new Vector3(
            originalPosition.x - (tileWorldSize / 2) + (tileStep / 2),
            originalPosition.y, // Keep Y (height) the same
            originalPosition.z - (tileWorldSize / 2) + (tileStep / 2) // Use Z as the second axis
        );

        // Set camera orthographicSize to match the tile size
        float tileOrthographicSize = tileStep / 2;
        cam.orthographicSize = tileOrthographicSize;
        
        // Create a 2D array to store tile filenames if we're stitching
        // This makes the order clearer during stitching
        string[,] tileFilenamesGrid = stitchTiles ? new string[tilesPerSide, tilesPerSide] : null;
        
        try
        {
            EditorUtility.DisplayProgressBar("Capturing Tiles", "Starting capture...", 0);
            
            // Create and capture each tile
            for (int z = 0; z < tilesPerSide; z++) // Using z for rows
            {
                for (int x = 0; x < tilesPerSide; x++) // Using x for columns
                {
                    float progress = (z * tilesPerSide + x) / (float)(tilesPerSide * tilesPerSide);
                    EditorUtility.DisplayProgressBar("Capturing Tiles", 
                        $"Capturing tile {z * tilesPerSide + x + 1} of {tilesPerSide * tilesPerSide}", 
                        progress);
                    
                    // Position the camera for this tile (X and Z axes for ground plane)
                    Vector3 tilePosition = new Vector3(
                        startPosition.x + (x * tileStep),
                        originalPosition.y, // Keep the same height
                        startPosition.z + (z * tileStep)
                    );
                    cam.transform.position = tilePosition;

                    // Capture this tile
                    string tileFilename = Path.Combine(outputFolder, $"tile_x{x}_z{z}.png");
                    CaptureTile(cam, rt, tileFilename);
                    
                    // Store filename for stitching later
                    if (stitchTiles)
                    {
                        tileFilenamesGrid[z, x] = tileFilename;
                    }

                    Debug.Log($"Captured tile at position {tilePosition}, saved to {tileFilename}");
                }
            }
            
            EditorUtility.ClearProgressBar();
            
            // Stitch tiles if requested
            if (stitchTiles)
            {
                EditorUtility.DisplayProgressBar("Stitching Tiles", "Starting stitching...", 0);
                StitchTiles(tileFilenamesGrid, tilesPerSide, finalSize);
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"All tiles have been saved to: {outputFolder}");
            EditorUtility.DisplayDialog("Tiled Screenshot", 
                $"Successfully captured {tilesPerSide}x{tilesPerSide} tiles to {outputFolder}" + 
                (stitchTiles ? "\nStitched image has also been created." : ""), 
                "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError($"Error during capture: {e.Message}\n{e.StackTrace}");
            EditorUtility.DisplayDialog("Error", $"An error occurred: {e.Message}", "OK");
        }
        finally
        {
            // Restore camera settings
            if (preserveCameraPosition)
            {
                cam.transform.position = originalPosition;
                cam.orthographicSize = originalSize;
            }
            
            cam.targetTexture = originalTargetTexture;
            
            // Clean up render texture
            if (rt != null)
            {
                RenderTexture.active = null;
                rt.Release();
                DestroyImmediate(rt);
            }
        }
    }

    private void CaptureTile(Camera cam, RenderTexture rt, string filename)
    {
        // Set up rendering
        cam.targetTexture = rt;
        RenderTexture.active = rt;
        
        // Render and read pixels
        cam.Render();
        Texture2D screenshot = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        screenshot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        screenshot.Apply();
        
        // Save to file
        byte[] bytes = screenshot.EncodeToPNG();
        File.WriteAllBytes(filename, bytes);
        
        // Clean up texture
        DestroyImmediate(screenshot);
    }
    
    private void StitchTiles(string[,] tileFilenamesGrid, int tilesPerSide, int finalSize)
    {
        if (tileFilenamesGrid == null)
            return;
            
        // Calculate what resolution we can actually use without running out of memory
        int targetResolution = Mathf.Min(finalSize, maxStitchResolution);
        float downsampleFactor = (float)targetResolution / finalSize;
        
        // Calculate the actual stitched image size
        int stitchedSize = Mathf.RoundToInt(tileSize * tilesPerSide * downsampleFactor);
        
        Debug.Log($"Creating stitched image of size {stitchedSize}x{stitchedSize} " + 
                 (downsampleFactor < 1.0f ? $"(downsampled from {finalSize}x{finalSize})" : ""));
        
        // Create a new texture for the stitched image
        Texture2D stitchedTexture = null;
        
        try
        {
            stitchedTexture = new Texture2D(stitchedSize, stitchedSize, TextureFormat.RGB24, false);
            int totalTiles = tilesPerSide * tilesPerSide;
            int tileCounter = 0;
            
            // Process tiles in correct order (z=0 is bottom in the world)
            for (int z = 0; z < tilesPerSide; z++)
            {
                for (int x = 0; x < tilesPerSide; x++)
                {
                    tileCounter++;
                    EditorUtility.DisplayProgressBar("Stitching Tiles", 
                        $"Processing tile {tileCounter} of {totalTiles}",
                        tileCounter / (float)totalTiles);
                    
                    // Get the tile filename
                    string tilePath = tileFilenamesGrid[z, x];
                    if (string.IsNullOrEmpty(tilePath) || !File.Exists(tilePath))
                    {
                        Debug.LogWarning($"Tile file not found at z={z}, x={x}");
                        continue;
                    }
                    
                    // Load tile efficiently
                    Texture2D tileTexture = LoadAndResizeImage(tilePath, 
                        Mathf.RoundToInt(tileSize * downsampleFactor));
                    
                    if (tileTexture == null)
                        continue;
                    
                    // Calculate position in stitched texture - FIXED for correct ordering
                    // For a top-down view, z=0 should be at the bottom of the final image
                    int xPos = Mathf.RoundToInt(x * tileSize * downsampleFactor);
                    int yPos = Mathf.RoundToInt(z * tileSize * downsampleFactor);
                    
                    // Copy pixels to stitched texture
                    for (int py = 0; py < tileTexture.height; py++)
                    {
                        for (int px = 0; px < tileTexture.width; px++)
                        {
                            // Skip pixels outside the bounds
                            if (xPos + px >= stitchedTexture.width || yPos + py >= stitchedTexture.height)
                                continue;
                                
                            stitchedTexture.SetPixel(xPos + px, yPos + py, tileTexture.GetPixel(px, py));
                        }
                    }
                    
                    // Clean up tile texture
                    DestroyImmediate(tileTexture);
                    
                    // Apply changes to stitched texture every few tiles to prevent GPU memory issues
                    if (tileCounter % 4 == 0 || tileCounter == totalTiles)
                    {
                        stitchedTexture.Apply();
                    }
                }
            }
            
            // Save stitched image
            string stitchedFilePath = Path.Combine(outputFolder, $"stitched_{stitchedSize}x{stitchedSize}.png");
            byte[] bytes = stitchedTexture.EncodeToPNG();
            File.WriteAllBytes(stitchedFilePath, bytes);
            
            Debug.Log($"Stitched image saved to: {stitchedFilePath}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error during stitching: {e.Message}\n{e.StackTrace}");
        }
        finally
        {
            // Clean up texture
            if (stitchedTexture != null)
                DestroyImmediate(stitchedTexture);
        }
    }
    
    private Texture2D LoadAndResizeImage(string filePath, int targetSize)
    {
        try
        {
            // Load the image data
            byte[] fileData = File.ReadAllBytes(filePath);
            
            // Create a temporary texture and load the image
            Texture2D tempTexture = new Texture2D(2, 2);
            if (!tempTexture.LoadImage(fileData))
            {
                Debug.LogError($"Failed to load image: {filePath}");
                DestroyImmediate(tempTexture);
                return null;
            }
            
            // If no resizing is needed
            if (tempTexture.width == targetSize)
                return tempTexture;
                
            // Create resized texture
            Texture2D resizedTexture = new Texture2D(targetSize, targetSize, TextureFormat.RGB24, false);
            
            float scale = (float)targetSize / tempTexture.width;
            
            for (int y = 0; y < targetSize; y++)
            {
                for (int x = 0; x < targetSize; x++)
                {
                    float srcX = x / scale;
                    float srcY = y / scale;
                    
                    // Simple bilinear sampling
                    resizedTexture.SetPixel(x, y, tempTexture.GetPixelBilinear(srcX / tempTexture.width, srcY / tempTexture.height));
                }
            }
            
            resizedTexture.Apply();
            DestroyImmediate(tempTexture);
            
            return resizedTexture;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error loading image {filePath}: {e.Message}");
            return null;
        }
    }
}
