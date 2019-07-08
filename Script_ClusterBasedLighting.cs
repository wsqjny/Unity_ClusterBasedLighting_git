using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;


struct CD_DIM
{
    public float fieldOfViewY;
    public float zNear;
    public float zFar;

    public float sD;
    public float logDimY;
    public float logDepth;

    public int clusterDimX;
    public int clusterDimY;
    public int clusterDimZ;
    public int clusterDimXYZ;
};

struct AABB
{
    public Vector4 Min;
    public Vector4 Max;
};

struct ShaderIDs
{
    internal static readonly int InverseProjectionMatrix    = Shader.PropertyToID("_InverseProjectionMatrix");
    internal static readonly int ClusterCB_ViewNear         = Shader.PropertyToID("ClusterCB_ViewNear");
    internal static readonly int ClusterCB_ScreenDimensions = Shader.PropertyToID("ClusterCB_ScreenDimensions");
    internal static readonly int ClusterCB_GridDim          = Shader.PropertyToID("ClusterCB_GridDim");
    internal static readonly int ClusterCB_Size             = Shader.PropertyToID("ClusterCB_Size");
    internal static readonly int ClusterCB_NearK            = Shader.PropertyToID("ClusterCB_NearK");
    internal static readonly int ClusterCB_LogGridDimY      = Shader.PropertyToID("ClusterCB_LogGridDimY");    
    internal static readonly int DepthTexture               = Shader.PropertyToID("DepthTexture");
    internal static readonly int RWClusterFlags             = Shader.PropertyToID("RWClusterFlags");
};

//[ExecuteInEditMode]
#if UNITY_5_4_OR_NEWER
//[ImageEffectAllowedInSceneView]
#endif
public class Script_ClusterBasedLighting : MonoBehaviour
{
    public ComputeShader    cs_ComputeClusterAABB;
    public ComputeShader    cs_ClusterSample;
    public ComputeShader    cs_AssignLightsToClusts;
    public ComputeShader    cs_FindUniqueClusters;
    public ComputeShader    cs_UpdateIndirectArgumentBuffers;

    private ComputeBuffer   cb_ClusterAABBs;
    private ComputeBuffer   cb_ClusterPointLightIndexCounter;
    private ComputeBuffer   cb_ClusterPointLightGrid;
    private ComputeBuffer   cb_ClusterPointLightIndexList;
    private ComputeBuffer   cb_PointLightPosRadius;
    private ComputeBuffer   cb_PointLightColor;

    private ComputeBuffer   cb_ClusterFlag;
    private ComputeBuffer   cb_UniqueClusters;
    private ComputeBuffer   cb_UniqueClusterCount;
    private ComputeBuffer   cb_IAB_AssignLightsToClusters;
    private ComputeBuffer   cb_IAB_DrawDebugClusters;

    public Material         mtl_DebugCluster;
    public Material         mtl_DpethPrePass;

    /// <summary>
    /// Light
    /// </summary>
    public GameObject goPointLightGroup;
    private List<Light> lightList;

    /// <summary>
    /// SceneObject
    /// </summary>
    public GameObject       go_SceneListParent;
    private List<Material>  lst_Mtl;
    private List<Mesh>      lst_Mesh;
    private List<Transform> lst_TF;
    

    private RenderTexture _rtColor;
    private RenderTexture _rtDepth;

    private Camera _camera;

    private CD_DIM m_DimData;
    private int m_ClusterGridBlockSize = 64;
    private int m_AVERAGE_OVERLAPPING_LIGHTS_PER_CLUSTER = 20;
    private int MAX_NUM_LIGHTS = 2 * 1024;

    void Start()
    {
        _camera = Camera.main;

        ////////////////////////////////////////////////////////////////////////////////////////////////
        /// Init RT
        _rtColor = new RenderTexture(Screen.width, Screen.height, 24);
        _rtDepth = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear);


        OnResize();

        ////////////////////////////////////////////////////////////////////////////////////////////////
        /// Init Light
        InitLightBuffer();
        Light[] l_Parent = goPointLightGroup.GetComponentsInChildren<Light>();
        lightList = new List<Light>();
        foreach (Light l in l_Parent)
        {
            lightList.Add(l);
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////
        ///Init SceneObject
        InitSceneObject();
    }


    void OnRenderImage(RenderTexture sourceTexture, RenderTexture destTexture)
    {
        Graphics.SetRenderTarget(_rtColor.colorBuffer, _rtDepth.depthBuffer);
        GL.Clear(true, true, Color.gray);

        UpdateLightBuffer();

        Pass_DepthPre();

        Pass_ClusterSample_CSVer();

        Pass_FinduniqueCluster();

        ComputeBuffer.CopyCount(cb_UniqueClusters, cb_UniqueClusterCount, 0);

        Pass_UpdateIndirectArgumentBuffers();

        Pass_AssignLightsToClusts();

        Graphics.SetRenderTarget(_rtColor.colorBuffer, _rtDepth.depthBuffer);
        Pass_DrawSceneColor();
        Pass_DebugCluster();

        Graphics.Blit(_rtColor, destTexture);
    }


    void Pass_ComputeClusterAABB()
    {
        var projectionMatrix = GL.GetGPUProjectionMatrix(_camera.projectionMatrix, false);
        var projectionMatrixInvers = projectionMatrix.inverse;
        cs_ComputeClusterAABB.SetMatrix("_InverseProjectionMatrix", projectionMatrixInvers);

        UpdateClusterCBuffer(cs_ComputeClusterAABB);

        int threadGroups = Mathf.CeilToInt(m_DimData.clusterDimXYZ / 1024.0f);

        int kernel = cs_ComputeClusterAABB.FindKernel("CSMain");
        cs_ComputeClusterAABB.SetBuffer(kernel, "RWClusterAABBs", cb_ClusterAABBs);
        cs_ComputeClusterAABB.Dispatch(kernel, threadGroups, 1, 1);

        AABB[] output = new AABB[100];
        cb_ClusterAABBs.GetData(output);

        //  Debug.Log(output);
    }

    void Pass_AssignLightsToClusts()
    {
        ClearLightGirdIndexCounter();

        int kernel = cs_AssignLightsToClusts.FindKernel("CSMain");

        //Output
        cs_AssignLightsToClusts.SetBuffer(kernel, "RWPointLightIndexCounter_Cluster", cb_ClusterPointLightIndexCounter);
        cs_AssignLightsToClusts.SetBuffer(kernel, "RWPointLightGrid_Cluster", cb_ClusterPointLightGrid);
        cs_AssignLightsToClusts.SetBuffer(kernel, "RWPointLightIndexList_Cluster", cb_ClusterPointLightIndexList);

        //Input
        cs_AssignLightsToClusts.SetInt("PointLightCount", lightList.Count);
        cs_AssignLightsToClusts.SetMatrix("_CameraLastViewMatrix", _camera.transform.worldToLocalMatrix);
        cs_AssignLightsToClusts.SetBuffer(kernel, "PointLights", cb_PointLightPosRadius);
        cs_AssignLightsToClusts.SetBuffer(kernel, "ClusterAABBs", cb_ClusterAABBs);

        cs_AssignLightsToClusts.Dispatch(kernel, m_DimData.clusterDimXYZ, 1, 1);
    }

    void Pass_DepthPre()
    {
        mtl_DpethPrePass.SetPass(0);
        DrawMeshListNow();
    }

    void Pass_ClusterSample_CSVer()
    {
        ClearClusterFlag();
        UpdateClusterCBuffer(cs_ClusterSample);

        var projectionMatrix = GL.GetGPUProjectionMatrix(_camera.projectionMatrix, true);
        var projectionMatrixInvers = projectionMatrix.inverse;        

        int kernel = cs_ClusterSample.FindKernel("CSMain");
        Vector4 screenDim = new Vector4((float)Screen.width, (float)Screen.height, 1.0f / Screen.width, 1.0f / Screen.height);

        cs_ClusterSample.SetTexture(kernel, ShaderIDs.DepthTexture, _rtDepth);
        cs_ClusterSample.SetBuffer(kernel, ShaderIDs.RWClusterFlags, cb_ClusterFlag);
        cs_ClusterSample.SetMatrix(ShaderIDs.InverseProjectionMatrix, projectionMatrixInvers);        
        cs_ClusterSample.SetVector(ShaderIDs.ClusterCB_ScreenDimensions, screenDim);

        cs_ClusterSample.Dispatch(kernel, Mathf.CeilToInt(Screen.width / 32.0f), Mathf.CeilToInt(Screen.height / 32.0f), 1);
    }

    void Pass_FinduniqueCluster()
    {
        ClearUniqueCluster();

        cb_UniqueClusters.SetCounterValue(0);

        int threadGroups = Mathf.CeilToInt(m_DimData.clusterDimXYZ / 1024.0f);

        int kernel = cs_FindUniqueClusters.FindKernel("CSMain");
        cs_FindUniqueClusters.SetBuffer(kernel, "RWUniqueClusters", cb_UniqueClusters);
        cs_FindUniqueClusters.SetBuffer(kernel, "ClusterFlags", cb_ClusterFlag);
        cs_FindUniqueClusters.Dispatch(kernel, threadGroups, 1, 1);
    }

    void Pass_UpdateIndirectArgumentBuffers()
    {
        int kernel = cs_UpdateIndirectArgumentBuffers.FindKernel("CSMain");
        cs_UpdateIndirectArgumentBuffers.SetBuffer(kernel, "ClusterCounter", cb_UniqueClusterCount);
        cs_UpdateIndirectArgumentBuffers.SetBuffer(kernel, "AssignLightsToClustersIndirectArgumentBuffer", cb_IAB_AssignLightsToClusters);
        cs_UpdateIndirectArgumentBuffers.SetBuffer(kernel, "DebugClustersIndirectArgumentBuffer", cb_IAB_DrawDebugClusters);
        cs_UpdateIndirectArgumentBuffers.Dispatch(kernel, 1, 1, 1);
    }
    


    void Pass_DrawSceneColor()
    {
        //GL.wireframe = true;
        for (int i = 0; i < lst_Mesh.Count; i++)
        {
            UpdateClusterCBufferForMtl(lst_Mtl[i]);

            lst_Mtl[i].SetBuffer("PointLightGrid_Cluster", cb_ClusterPointLightGrid);
            lst_Mtl[i].SetBuffer("PointLightIndexList_Cluster", cb_ClusterPointLightIndexList);
            lst_Mtl[i].SetBuffer("PointLights", cb_PointLightPosRadius);
            lst_Mtl[i].SetBuffer("PointLightsColors", cb_PointLightColor);

            lst_Mtl[i].SetPass(0);
            Graphics.DrawMeshNow(lst_Mesh[i], lst_TF[i].localToWorldMatrix);
        }
        //GL.wireframe = false;
    }

    void Pass_DebugCluster()
    {
        GL.wireframe = true;

        mtl_DebugCluster.SetBuffer("ClusterAABBs", cb_ClusterAABBs);
        mtl_DebugCluster.SetBuffer("PointLightGrid_Cluster", cb_ClusterPointLightGrid);

        mtl_DebugCluster.SetMatrix("_CameraWorldMatrix", _camera.transform.localToWorldMatrix);
        
        mtl_DebugCluster.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Points, m_DimData.clusterDimXYZ);
        
        GL.wireframe = false;
    }

    void DrawMeshListNow()
    {
        for (int i = 0; i < lst_Mesh.Count; i++)
        {
            Graphics.DrawMeshNow(lst_Mesh[i], lst_TF[i].localToWorldMatrix);
        }
    }

    void UpdateClusterCBuffer(ComputeShader cs)
    {
        int[] gridDims = { m_DimData.clusterDimX, m_DimData.clusterDimY, m_DimData.clusterDimZ };
        int[] sizes = { m_ClusterGridBlockSize, m_ClusterGridBlockSize };
        Vector4 screenDim = new Vector4((float)Screen.width, (float)Screen.height, 1.0f / Screen.width, 1.0f / Screen.height);
        float viewNear = m_DimData.zNear;

        cs.SetInts(ShaderIDs.ClusterCB_GridDim, gridDims);        
        cs.SetInts(ShaderIDs.ClusterCB_Size, sizes);
        cs.SetFloat(ShaderIDs.ClusterCB_ViewNear, viewNear);
        cs.SetFloat(ShaderIDs.ClusterCB_NearK, 1.0f + m_DimData.sD);
        cs.SetFloat(ShaderIDs.ClusterCB_LogGridDimY, m_DimData.logDimY);
        cs.SetVector(ShaderIDs.ClusterCB_ScreenDimensions, screenDim);
    }

    void UpdateClusterCBufferForMtl(Material mtl)
    {
        int[] gridDims = { m_DimData.clusterDimX, m_DimData.clusterDimY, m_DimData.clusterDimZ };
        int[] sizes = { m_ClusterGridBlockSize, m_ClusterGridBlockSize };
        Vector4 screenDim = new Vector4((float)Screen.width, (float)Screen.height, 1.0f / Screen.width, 1.0f / Screen.height);
        float viewNear = m_DimData.zNear;

        mtl.SetInt("ClusterCB_GridDimX", gridDims[0]);
        mtl.SetInt("ClusterCB_GridDimY", gridDims[1]);
        mtl.SetInt("ClusterCB_GridDimZ", gridDims[2]);
        mtl.SetFloat("ClusterCB_ViewNear", viewNear);
        mtl.SetInt("ClusterCB_SizeX", sizes[0]);
        mtl.SetInt("ClusterCB_SizeY", sizes[1]);
        mtl.SetFloat("ClusterCB_NearK", 1.0f + m_DimData.sD);
        mtl.SetFloat("ClusterCB_LogGridDimY", m_DimData.logDimY);
        mtl.SetVector("ClusterCB_ScreenDimensions", screenDim);
    }

    void UpdateLightBuffer()
    {
        List<Vector4> lightPosRatioList = new List<Vector4>();
        foreach (var lit in lightList)
        {
            lightPosRatioList.Add(new Vector4(lit.transform.position.x, lit.transform.position.y, lit.transform.position.z, lit.range));
        }

        cb_PointLightPosRadius.SetData(lightPosRatioList);
    }

    void CalculateMDim(Camera cam)
    {
        // The half-angle of the field of view in the Y-direction.
        float fieldOfViewY = cam.fieldOfView * Mathf.Deg2Rad * 0.5f;//Degree 2 Radiance:  Param.CameraInfo.Property.Perspective.fFovAngleY * 0.5f;
        float zNear = cam.nearClipPlane;// Param.CameraInfo.Property.Perspective.fMinVisibleDistance;
        float zFar = cam.farClipPlane;// Param.CameraInfo.Property.Perspective.fMaxVisibleDistance;

        // Number of clusters in the screen X direction.
        int clusterDimX = Mathf.CeilToInt(Screen.width / (float)m_ClusterGridBlockSize);
        // Number of clusters in the screen Y direction.
        int clusterDimY = Mathf.CeilToInt(Screen.height / (float)m_ClusterGridBlockSize);

        // The depth of the cluster grid during clustered rendering is dependent on the 
        // number of clusters subdivisions in the screen Y direction.
        // Source: Clustered Deferred and Forward Shading (2012) (Ola Olsson, Markus Billeter, Ulf Assarsson).
        float sD = 2.0f * Mathf.Tan(fieldOfViewY) / (float)clusterDimY;
        float logDimY = 1.0f / Mathf.Log(1.0f + sD);

        float logDepth = Mathf.Log(zFar / zNear);
        int clusterDimZ = Mathf.FloorToInt(logDepth * logDimY);

        m_DimData.zNear = zNear;
        m_DimData.zFar = zFar;
        m_DimData.sD = sD;
        m_DimData.fieldOfViewY = fieldOfViewY;
        m_DimData.logDepth = logDepth;
        m_DimData.logDimY = logDimY;
        m_DimData.clusterDimX = clusterDimX;
        m_DimData.clusterDimY = clusterDimY;
        m_DimData.clusterDimZ = clusterDimZ;
        m_DimData.clusterDimXYZ = clusterDimX * clusterDimY * clusterDimZ;
    }

    void ClearClusterFlag()
    {
        float[] flags = new float[m_DimData.clusterDimXYZ];
        for (int i = 0; i < m_DimData.clusterDimXYZ; i++)
        {
            flags[i] = 0.0f;
        }
        cb_ClusterFlag.SetData(flags);
    }

    void ClearLightGirdIndexCounter()
    {
        uint[] uCounter = { 0 };
        cb_ClusterPointLightIndexCounter.SetData(uCounter);

        Vector2Int[] vec2Girds = new Vector2Int[m_DimData.clusterDimXYZ];
        for (int i = 0; i < m_DimData.clusterDimXYZ; i++)
        {
            vec2Girds[i] = new Vector2Int(0, 0);
        }
        cb_ClusterPointLightGrid.SetData(vec2Girds);
    }

    void ClearUniqueCluster()
    {
        uint[] uniqueClusters = new uint[m_DimData.clusterDimXYZ];
        for (int i = 0; i < m_DimData.clusterDimXYZ; i++)
        {
            uniqueClusters[i] = 0;
        }
        cb_UniqueClusters.SetData(uniqueClusters);
    }

    void OnResize()
    {
        ////////////////////////////////////////////////////////////////////////////////////////////////
        /// Init Dim
        CalculateMDim(_camera);

        int stride = Marshal.SizeOf(typeof(AABB));
        cb_ClusterAABBs = new ComputeBuffer(m_DimData.clusterDimXYZ, stride);
        Pass_ComputeClusterAABB();

        ////////////////////////////////////////////////////////////////////////////////////////////////
        ///Init Cluster Buffers
        InitClusterBuffers();
    }

    void InitClusterBuffers()
    {
        cb_ClusterPointLightIndexCounter    = new ComputeBuffer(1, sizeof(uint));
        cb_UniqueClusterCount               = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
        cb_IAB_AssignLightsToClusters       = new ComputeBuffer(1, sizeof(uint) * 3, ComputeBufferType.IndirectArguments);
        cb_IAB_DrawDebugClusters            = new ComputeBuffer(1, sizeof(uint) * 4, ComputeBufferType.IndirectArguments);


        cb_ClusterPointLightGrid            = new ComputeBuffer(m_DimData.clusterDimXYZ, sizeof(uint) * 2);
        cb_ClusterPointLightIndexList       = new ComputeBuffer(m_DimData.clusterDimXYZ * m_AVERAGE_OVERLAPPING_LIGHTS_PER_CLUSTER, sizeof(uint));

        cb_ClusterFlag                      = new ComputeBuffer(m_DimData.clusterDimXYZ, sizeof(float));
        cb_UniqueClusters                   = new ComputeBuffer(m_DimData.clusterDimXYZ, sizeof(uint), ComputeBufferType.Counter);
    }

    void InitLightBuffer()
    {
        cb_PointLightPosRadius = new ComputeBuffer(MAX_NUM_LIGHTS, sizeof(float) * 4);

        cb_PointLightColor = new ComputeBuffer(MAX_NUM_LIGHTS, sizeof(float) * 4);
        Vector4[] colors = new Vector4[MAX_NUM_LIGHTS];
        for (int i = 0; i < MAX_NUM_LIGHTS; i++)
        {
            colors[i] = GenerateRadomColor();
        }

        cb_PointLightColor.SetData(colors);
    }
    Vector4 GenerateRadomColor()
    {
        float r = Random.Range(0.0f, 1.0f);
        float g = Random.Range(0.0f, 1.0f);
        float b = Random.Range(0.0f, 1.0f);
        float a = 1.0f;

        float fIntensity = Random.Range(0.1f, 10.0f);
        return new Vector4(r * fIntensity, g * fIntensity, b * fIntensity, a);
    }

    void InitSceneObject()
    {
        lst_Mesh    = new List<Mesh>();
        lst_TF      = new List<Transform>();
        lst_Mtl     = new List<Material>();

        MeshFilter[] mf_Parent = go_SceneListParent.GetComponentsInChildren<MeshFilter>();
        foreach (MeshFilter mf in mf_Parent)
        {
            lst_Mesh.Add(mf.mesh);
        }

        Transform[] tf_Parent = go_SceneListParent.GetComponentsInChildren<Transform>();
        foreach (Transform tf in tf_Parent)
        {
            lst_TF.Add(tf);
        }

        MeshRenderer[] mr_Parent = go_SceneListParent.GetComponentsInChildren<MeshRenderer>();
        foreach (MeshRenderer mr in mr_Parent)
        {
            Material mtl = new Material(Shader.Find("ClusterBasedLightingGit/Shader_Color"));
            //Material mtl = new Material(Shader.Find("Unlit/Texture"));
            mtl.SetTexture("_MainTex", mr.material.GetTexture("_MainTex"));
            lst_Mtl.Add(mtl);
        }
    }
}
