var TILES_X=16;
var TILES_Z=16;
var TILE_WIDTH=4096;
var TILE_HEIGHT=4096;
var RENDER_SCALE=2;
var OUTPUT_DIR=@"E:\Temp\map_tiles";

var USE_CUSTOM_BOUNDS=false;
var CUSTOM_MIN_X=-500f;
var CUSTOM_MAX_X=500f;
var CUSTOM_MIN_Z=-500f;
var CUSTOM_MAX_Z=500f;

var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
var renderers=scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<UnityEngine.Renderer>(true)).Where(x=>x!=null&&x.enabled).ToArray();
var result="";

if(renderers.Length==0)
    result="No renderers found";
else
{
    var bounds=renderers[0].bounds;
    for(int i=1;i<renderers.Length;i++) bounds.Encapsulate(renderers[i].bounds);

    var minX=USE_CUSTOM_BOUNDS?CUSTOM_MIN_X:bounds.min.x;
    var maxX=USE_CUSTOM_BOUNDS?CUSTOM_MAX_X:bounds.max.x;
    var minZ=USE_CUSTOM_BOUNDS?CUSTOM_MIN_Z:bounds.min.z;
    var maxZ=USE_CUSTOM_BOUNDS?CUSTOM_MAX_Z:bounds.max.z;

    var cx=(minX+maxX)*0.5f;
    var cz=(minZ+maxZ)*0.5f;
    var size=Mathf.Max(maxX-minX,maxZ-minZ);

    minX=cx-size*0.5f;
    maxX=cx+size*0.5f;
    minZ=cz-size*0.5f;
    maxZ=cz+size*0.5f;

    var width=size;
    var height=size;
    var totalTiles=TILES_X*TILES_Z;
    var renderWidth=TILE_WIDTH*RENDER_SCALE;
    var renderHeight=TILE_HEIGHT*RENDER_SCALE;
    var stopFile=System.IO.Path.Combine(OUTPUT_DIR,"stop.txt");

    System.IO.Directory.CreateDirectory(OUTPUT_DIR);

    var volumes=UnityEngine.Object.FindObjectsOfType<UnityEngine.Rendering.Volume>(true);
    var fogStates=new System.Collections.Generic.Dictionary<UnityEngine.Rendering.HighDefinition.Fog,bool>();
    var vignetteStates=new System.Collections.Generic.Dictionary<UnityEngine.Rendering.HighDefinition.Vignette,bool>();

    foreach(var v in volumes)
    {
        if(v==null||v.profile==null) continue;

        UnityEngine.Rendering.HighDefinition.Fog fog;
        if(v.profile.TryGet<UnityEngine.Rendering.HighDefinition.Fog>(out fog))
        {
            fogStates[fog]=fog.active;
            fog.active=false;
        }

        UnityEngine.Rendering.HighDefinition.Vignette vignette;
        if(v.profile.TryGet<UnityEngine.Rendering.HighDefinition.Vignette>(out vignette))
        {
            vignetteStates[vignette]=vignette.active;
            vignette.active=false;
        }
    }

    var previousAA=UnityEngine.QualitySettings.antiAliasing;
    UnityEngine.QualitySettings.antiAliasing=0;

    var cameraGO=new UnityEngine.GameObject("__MapCaptureCamera");
    var camera=cameraGO.AddComponent<UnityEngine.Camera>();

    camera.orthographic=true;
    camera.aspect=(float)TILE_WIDTH/TILE_HEIGHT;
    camera.clearFlags=UnityEngine.CameraClearFlags.SolidColor;
    camera.backgroundColor=new UnityEngine.Color(0.08f,0.08f,0.08f,1f);
    camera.cullingMask=-1;
    camera.nearClipPlane=0.1f;
    camera.farClipPlane=5000f;
    camera.allowHDR=false;
    camera.allowMSAA=false;

    var rt=new UnityEngine.RenderTexture(renderWidth,renderHeight,24,UnityEngine.RenderTextureFormat.ARGB32);
    rt.antiAliasing=1;
    rt.filterMode=UnityEngine.FilterMode.Bilinear;
    rt.wrapMode=UnityEngine.TextureWrapMode.Clamp;
    rt.Create();
    camera.targetTexture=rt;

    var smallRT=new UnityEngine.RenderTexture(TILE_WIDTH,TILE_HEIGHT,0,UnityEngine.RenderTextureFormat.ARGB32);
    smallRT.antiAliasing=1;
    smallRT.filterMode=UnityEngine.FilterMode.Point;
    smallRT.wrapMode=UnityEngine.TextureWrapMode.Clamp;
    smallRT.Create();

    var inv=System.Globalization.CultureInfo.InvariantCulture;

    var technical=$@"Scene: {scene.name}
Renderers: {renderers.Length}
Custom bounds: {USE_CUSTOM_BOUNDS}
Original center: {cx}, {cz}
Bounds X: {minX} .. {maxX}
Bounds Z: {minZ} .. {maxZ}
World size: {width} x {height}
Tiles: {TILES_X} x {TILES_Z}
Tile resolution: {TILE_WIDTH} x {TILE_HEIGHT}
Render resolution: {renderWidth} x {renderHeight}
Supersampling: {RENDER_SCALE}x
Total resolution: {TILES_X*TILE_WIDTH} x {TILES_Z*TILE_HEIGHT}
World units per tile: {width/TILES_X} x {height/TILES_Z}
Pixels per world unit: {TILE_WIDTH/(width/TILES_X)}
Render pixels per world unit: {renderWidth/(width/TILES_X)}
Fog: DISABLED
Vignette: DISABLED
HDR: DISABLED
Camera MSAA: {camera.allowMSAA}
QualitySettings MSAA: {UnityEngine.QualitySettings.antiAliasing}x
RenderTexture MSAA: {rt.antiAliasing}x
Downsample: {renderWidth}x{renderHeight} -> {TILE_WIDTH}x{TILE_HEIGHT}
Initial warmup renders: 3
Subsequent renders: 1 per tile
Stop file: {stopFile}
Output: {OUTPUT_DIR}

Copy into HTML:

const mapConfig = {{
    size: [{width.ToString(inv)}, {height.ToString(inv)}],
    center: [{cx.ToString(inv)}, {cz.ToString(inv)}],
    bounds: [{minX.ToString(inv)}, {maxZ.ToString(inv)}, {maxX.ToString(inv)}, {minZ.ToString(inv)}]
}};
";

    System.IO.File.WriteAllText(System.IO.Path.Combine(OUTPUT_DIR,"technical.txt"),technical);

    var done=0;
    var stopped=false;
    var warmedUp=false;

    for(int tz=0;tz<TILES_Z&&!stopped;tz++)
    {
        for(int tx=0;tx<TILES_X;tx++)
        {
            if(System.IO.File.Exists(stopFile))
            {
                stopped=true;
                break;
            }

            done++;

            var file=System.IO.Path.Combine(OUTPUT_DIR,$"tile_{tx:D3}_{tz:D3}.png");

            if(System.IO.File.Exists(file))
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(OUTPUT_DIR,"progress.txt"),technical+$"\nProgress: {done}/{totalTiles} ({done*100/totalTiles}%)\nSkipped existing tile: {tx}, {tz}\n");
                continue;
            }

            var tileMinX=minX+width*tx/TILES_X;
            var tileMaxX=minX+width*(tx+1)/TILES_X;
            var tileMinZ=minZ+height*tz/TILES_Z;
            var tileMaxZ=minZ+height*(tz+1)/TILES_Z;

            var tileCX=(tileMinX+tileMaxX)*0.5f;
            var tileCZ=(tileMinZ+tileMaxZ)*0.5f;

            camera.transform.position=new UnityEngine.Vector3(tileCX,bounds.max.y+1000f,tileCZ);
            camera.transform.rotation=UnityEngine.Quaternion.Euler(90f,0f,0f);
            camera.orthographicSize=(tileMaxZ-tileMinZ)*0.5f;
            camera.aspect=1f;

            UnityEngine.RenderTexture.active=rt;

            if(!warmedUp)
            {
                camera.Render();
                camera.Render();
                camera.Render();
                warmedUp=true;
            }

            camera.Render();

            UnityEngine.Graphics.Blit(rt,smallRT);

            UnityEngine.RenderTexture.active=smallRT;

            var texture=new UnityEngine.Texture2D(TILE_WIDTH,TILE_HEIGHT,UnityEngine.TextureFormat.RGB24,false);
            texture.ReadPixels(new UnityEngine.Rect(0,0,TILE_WIDTH,TILE_HEIGHT),0,0);
            texture.Apply();

            var png=texture.EncodeToPNG();
            System.IO.File.WriteAllBytes(file,png);

            UnityEngine.Object.Destroy(texture);

            System.IO.File.WriteAllText(System.IO.Path.Combine(OUTPUT_DIR,"progress.txt"),technical+$"\nProgress: {done}/{totalTiles} ({done*100/totalTiles}%)\nRendered tile: {tx}, {tz}\n");
        }
    }

    camera.targetTexture=null;
    UnityEngine.RenderTexture.active=null;

    foreach(var pair in fogStates)
        if(pair.Key!=null) pair.Key.active=pair.Value;

    foreach(var pair in vignetteStates)
        if(pair.Key!=null) pair.Key.active=pair.Value;

    UnityEngine.QualitySettings.antiAliasing=previousAA;

    rt.Release();
    smallRT.Release();

    UnityEngine.Object.Destroy(rt);
    UnityEngine.Object.Destroy(smallRT);
    UnityEngine.Object.Destroy(cameraGO);

    if(stopped)
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(OUTPUT_DIR,"progress.txt"),technical+$"\nProgress: STOPPED\nCompleted/checked tiles: {done}/{totalTiles}\nStop file detected: {stopFile}\nDelete stop.txt and run again to resume.\n");
        result=$"STOPPED\n\n{technical}\nCompleted/checked tiles: {done}/{totalTiles}";
    }
    else
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(OUTPUT_DIR,"progress.txt"),technical+$"\nProgress: DONE ({totalTiles}/{totalTiles})\n");
        result=$"DONE\n\n{technical}\nTiles rendered: {totalTiles}";
    }
}

result;
