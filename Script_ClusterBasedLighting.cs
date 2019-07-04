
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
#if UNITY_5_4_OR_NEWER
[ImageEffectAllowedInSceneView]
#endif
public class Script_ClusterBasedLighting : MonoBehaviour
{
    private RenderTexture _rtColor;
    private RenderTexture _rtDepth;


    void Start()
    {
        _rtColor = new RenderTexture(Screen.width, Screen.height, 24);
        _rtDepth = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear);
    }
  
    void Update()
    {
        
    }

    void OnRenderImage(RenderTexture sourceTexture, RenderTexture destTexture)
    {
        Graphics.SetRenderTarget(_rtColor.colorBuffer, _rtDepth.depthBuffer);
        GL.Clear(true, true, Color.gray);

        Graphics.Blit(_rtColor, destTexture);
    }

}
