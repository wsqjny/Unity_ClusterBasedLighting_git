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

[ExecuteInEditMode]
#if UNITY_5_4_OR_NEWER
[ImageEffectAllowedInSceneView]
#endif
public class Script_ClusterBasedLighting : MonoBehaviour
{
    public ComputeShader cs_ComputeClusterAABB;
    public ComputeShader cs_AssignLightsToClusts;

    public Material mtlDebugCluster;

    private ComputeBuffer cb_ClusterAABBs;
    private ComputeBuffer cb_ClusterPointLightIndexCounter;
    private ComputeBuffer cb_ClusterPointLightGrid;
    private ComputeBuffer cb_ClusterPointLightIndexList;
    private ComputeBuffer cb_PointLightPosRadius;
    private ComputeBuffer cb_PointLightColor;

    public GameObject goPointLightGroup;
    private List<Light> lightList;   


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
        
        _rtColor = new RenderTexture(Screen.width, Screen.height, 24);
        _rtDepth = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear);


        CalculateMDim(_camera);

        int stride = Marshal.SizeOf(typeof(AABB));
        cb_ClusterAABBs = new ComputeBuffer(m_DimData.clusterDimXYZ, stride);

        Pass_ComputeClusterAABB();

        ////////////////////////////////////////////////////////////////////////////////////////////////
        cb_ClusterPointLightIndexCounter = new ComputeBuffer(1, sizeof(uint));
        cb_ClusterPointLightGrid = new ComputeBuffer(m_DimData.clusterDimXYZ, sizeof(uint) * 2);
        cb_ClusterPointLightIndexList = new ComputeBuffer(m_DimData.clusterDimXYZ * m_AVERAGE_OVERLAPPING_LIGHTS_PER_CLUSTER, sizeof(uint));

        InitLightBuffer();
        Light[] l_Parent = goPointLightGroup.GetComponentsInChildren<Light>();
        lightList = new List<Light>();
        foreach (Light l in l_Parent)
        {
            lightList.Add(l);
        }

    }


    void OnRenderImage(RenderTexture sourceTexture, RenderTexture destTexture)
    {
        UpdateLightBuffer();

        Graphics.SetRenderTarget(_rtColor.colorBuffer, _rtDepth.depthBuffer);
        GL.Clear(true, true, Color.gray);

        Pass_AssignLightsToClusts();
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

    void Pass_DebugCluster()
    {
        GL.wireframe = true;

        mtlDebugCluster.SetBuffer("ClusterAABBs", cb_ClusterAABBs);
        mtlDebugCluster.SetBuffer("PointLightGrid_Cluster", cb_ClusterPointLightGrid);

        mtlDebugCluster.SetMatrix("_CameraWorldMatrix", _camera.transform.localToWorldMatrix);
        
        mtlDebugCluster.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Points, m_DimData.clusterDimXYZ);
        
        GL.wireframe = false;
    }

    void UpdateClusterCBuffer(ComputeShader cs)
    {
        int[] gridDims = { m_DimData.clusterDimX, m_DimData.clusterDimY, m_DimData.clusterDimZ };
        int[] sizes = { m_ClusterGridBlockSize, m_ClusterGridBlockSize };
        Vector4 screenDim = new Vector4((float)Screen.width, (float)Screen.height, 1.0f / Screen.width, 1.0f / Screen.height);
        float viewNear = m_DimData.zNear;

        cs.SetInts("ClusterCB_GridDim", gridDims);
        cs.SetFloat("ClusterCB_ViewNear", viewNear);
        cs.SetInts("ClusterCB_Size", sizes);
        cs.SetFloat("ClusterCB_NearK", 1.0f + m_DimData.sD);
        cs.SetFloat("ClusterCB_LogGridDimY", m_DimData.logDimY);
        cs.SetVector("ClusterCB_ScreenDimensions", screenDim);
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
}
